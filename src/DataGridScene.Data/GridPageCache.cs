using DataGridScene.Core;

namespace DataGridScene.Data;

/// <summary>Bounded completed-page LRU plus an independently bounded request queue. In-flight
/// requests remain coalesced even when completed pages are evicted. Thread-safe; source code
/// runs outside the cache lock. A source must not mutate records after returning a page.</summary>
public sealed class GridPageCache : IDisposable
{
    private sealed class Generation(GridQuery query)
    {
        public readonly GridQuery Query = query;
        public readonly CancellationTokenSource Cancellation = new();
        public int Active;
        public bool Retired, Cancelling, Disposed, HasMetadata;
        public string? Revision;
        public int? TotalCount;
        public Exception? Fault;
    }
    private sealed class Entry(int page, Generation generation)
    {
        public readonly int Page = page;
        public readonly Generation Generation = generation;
        public readonly CancellationToken Token = generation.Cancellation.Token;
        public readonly TaskCompletionSource<GridPage> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Cached(GridPage Page, LinkedListNode<int> Node);
    private readonly object _gate = new();
    private readonly IGridPageSource _source;
    private readonly Dictionary<int, Cached> _cached = [];
    private readonly Dictionary<int, Entry> _pending = [];
    private readonly Queue<Entry> _queue = [];
    private readonly LinkedList<int> _lru = [];
    private Generation _generation;
    private int _active;
    private bool _disposed, _pumping;
    public int PageSize { get; }
    public int Capacity { get; }
    public int MaximumConcurrency { get; }
    public int MaximumPendingPages { get; }
    public int CachedPageCount { get { lock (_gate) return _cached.Count; } }
    /// <summary>Current-generation queued plus active requests, not waiter count.</summary>
    public int PendingPageCount { get { lock (_gate) return _pending.Count; } }
    /// <summary>Includes cancelled-generation sources that have not returned yet.</summary>
    public int ActiveFetchCount { get { lock (_gate) return _active; } }
    public long FetchCount { get; private set; }
    public long CacheHitCount { get; private set; }
    public long CoalescedReadCount { get; private set; }

    // Retain the existing constructor signature for independently compiled consumers.
    public GridPageCache(IGridPageSource source, int pageSize = 128, int capacity = 16)
        : this(source, pageSize, capacity, maximumConcurrency: 4, maximumPendingPages: 4096) { }
    public GridPageCache(IGridPageSource source, int pageSize, int capacity, int maximumConcurrency, int maximumPendingPages)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (pageSize < 1 || pageSize > 10000 || capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maximumConcurrency < 1 || maximumConcurrency > 256 || maximumPendingPages < maximumConcurrency || maximumPendingPages > 65536)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        _source = source; PageSize = pageSize; Capacity = capacity;
        MaximumConcurrency = maximumConcurrency; MaximumPendingPages = maximumPendingPages;
        _generation = new(QuerySnapshot.Capture(new(), source.Schema));
    }
    public void SetQuery(GridQuery query)
    {
        query = QuerySnapshot.Capture(query, _source.Schema);
        DataSourceValidation.Validate(query, _source.Capabilities, 0, PageSize);
        Generation previous; bool cancelPrevious;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _generation;
            _generation = new(query);
            cancelPrevious = !previous.Retired;
            if (cancelPrevious) Retire(previous);
            foreach (var entry in _pending.Values) entry.Completion.TrySetCanceled(entry.Token);
            _pending.Clear(); _queue.Clear(); _cached.Clear(); _lru.Clear();
        }
        if (cancelPrevious) Cancel(previous);
        Pump();
    }
    /// <summary>Cancel a caller's wait without cancelling shared work. Already-cancelled
    /// callers do not schedule I/O. Queue overflow fails explicitly rather than dropping work.</summary>
    public Task<GridPage> GetPageAsync(int page, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        _ = checked(page * PageSize);
        Task<GridPage> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<GridPage>(cancellationToken);
            if (_generation.Fault is { } fault) return Task.FromException<GridPage>(fault);
            if (_cached.TryGetValue(page, out var cached))
            {
                _lru.Remove(cached.Node); _lru.AddFirst(cached.Node); CacheHitCount++;
                return Task.FromResult(cached.Page);
            }
            if (_pending.TryGetValue(page, out var entry)) CoalescedReadCount++;
            else
            {
                if (_pending.Count >= MaximumPendingPages) throw new InvalidOperationException("The pending page request limit was reached.");
                entry = new(page, _generation); _pending.Add(page, entry); _queue.Enqueue(entry);
            }
            task = entry.Completion.Task;
        }
        Pump();
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }
    private void Pump()
    {
        lock (_gate) { if (_pumping) return; _pumping = true; }
        // A synchronous source may finish inside LoadAsync. Keeping one pump owner
        // avoids recursive dispatch and stack growth for a large ready queue.
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                if (_disposed || _active >= MaximumConcurrency || _queue.Count == 0)
                { _pumping = false; return; }
                entry = _queue.Dequeue();
                if (entry.Generation.Retired) continue;
                _active++; entry.Generation.Active++; FetchCount++;
            }
            _ = LoadAsync(entry);
        }
    }
    private async Task LoadAsync(Entry entry)
    {
        GridPage? page = null; Exception? error = null;
        try
        {
            entry.Token.ThrowIfCancellationRequested();
            page = await _source.FetchAsync(entry.Generation.Query, checked(entry.Page * PageSize), PageSize, entry.Token).ConfigureAwait(false);
            page = DataSourceValidation.FreezePage(page, entry.Page * PageSize, PageSize, _source.Schema);
        }
        catch (Exception exception) { error = exception; }
        Generation? cancel = null;
        lock (_gate)
        {
            var generation = entry.Generation;
            _active--; generation.Active--;
            bool current = !_disposed && ReferenceEquals(generation, _generation) && !generation.Retired;
            if (current)
            {
                _pending.Remove(entry.Page);
                if (error is null)
                {
                    bool mixed = generation.HasMetadata &&
                        (generation.Revision != page!.Revision || generation.TotalCount is int count && page.TotalCount is int next && count != next);
                    // Even a page without a total must fit a count learned from another page.
                    int? knownTotal = page!.TotalCount ?? generation.TotalCount;
                    if (knownTotal is int total && page.Records.Count > 0 && (long)page.Offset + page.Records.Count > total) mixed = true;
                    // A previously unknown total cannot contradict a retained page either.
                    if (knownTotal is int learned && _cached.Values.Any(c => c.Page.Records.Count > 0 && (long)c.Page.Offset + c.Page.Records.Count > learned)) mixed = true;
                    if (mixed)
                    {
                        error = generation.Fault = new InvalidDataException("Pages from different source revisions or totals cannot be combined. Refresh the query.");
                        Retire(generation); cancel = generation;
                        foreach (var pending in _pending.Values) pending.Completion.TrySetException(error);
                        _pending.Clear(); _queue.Clear(); _cached.Clear(); _lru.Clear();
                    }
                    else
                    {
                        generation.HasMetadata = true; generation.Revision = page.Revision;
                        generation.TotalCount = knownTotal;
                        var node = _lru.AddFirst(entry.Page); _cached.Add(entry.Page, new(page, node));
                        while (_cached.Count > Capacity)
                        { int victim = _lru.Last!.Value; _lru.RemoveLast(); _cached.Remove(victim); }
                    }
                }
                if (error is OperationCanceledException) entry.Completion.TrySetCanceled(entry.Token);
                else if (error is not null) entry.Completion.TrySetException(error);
                else entry.Completion.TrySetResult(page!);
            }
            else entry.Completion.TrySetCanceled(entry.Token);
            DisposeRetired(generation);
        }
        if (cancel is not null) Cancel(cancel);
        Pump();
    }
    // Called with the lock held. Token sources outlive every active source operation.
    private static void Retire(Generation generation) { generation.Retired = true; generation.Cancelling = true; }
    private static void DisposeRetired(Generation generation)
    {
        if (generation.Retired && !generation.Cancelling && generation.Active == 0 && !generation.Disposed)
        { generation.Disposed = true; generation.Cancellation.Dispose(); }
    }
    private void Cancel(Generation generation)
    {
        try { generation.Cancellation.Cancel(); }
        catch (AggregateException) { /* A source cancellation callback must not strand other waiters. */ }
        finally { lock (_gate) { generation.Cancelling = false; DisposeRetired(generation); } }
    }
    public void Dispose()
    {
        Generation generation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; generation = _generation;
            if (!generation.Retired) Retire(generation);
            else generation = null!;
            foreach (var entry in _pending.Values) entry.Completion.TrySetCanceled(entry.Token);
            _pending.Clear(); _queue.Clear(); _cached.Clear(); _lru.Clear();
        }
        if (generation is not null) Cancel(generation);
    }
}

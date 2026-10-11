using DataGridScene.Core;
using System.Net.Http.Json;
using System.Text.Json;

namespace DataGridScene.Data;

[Flags]
public enum DataCapabilities { None = 0, Sort = 1, Filter = 2, Search = 4, Summaries = 8 }
public sealed record GridPage(int Offset, IReadOnlyList<GridRecord> Records, int? TotalCount, string? Revision = null);
public interface IGridPageSource
{
    GridSchema Schema { get; }
    DataCapabilities Capabilities { get; }
    ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken);
}
public static class DataSourceValidation
{
    public static void Validate(GridQuery query, DataCapabilities capabilities, int offset, int count)
    {
        if (offset < 0 || count < 1 || count > 10000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (query.EffectiveGroupSummaries.Count > 0 && query.GroupSummaries is not null || query.GroupSummarySorts.Count > 0 || query.GroupFooterDisplay != GroupFooterDisplayMode.Hidden) throw new NotSupportedException("The record-page contract does not represent group summaries or footers.");
        if (query.Groups.Count > 0) throw new NotSupportedException("The record-page contract does not represent server group rows.");
        if (query.Sorts.Count > 0 && !capabilities.HasFlag(DataCapabilities.Sort)) throw new NotSupportedException("This source does not support sorting.");
        if (query.Filter is not null && !capabilities.HasFlag(DataCapabilities.Filter)) throw new NotSupportedException("This source does not support filtering.");
        if (query.Search.Length > 0 && !capabilities.HasFlag(DataCapabilities.Search)) throw new NotSupportedException("This source does not support search.");
        if (query.Summaries.Count > 0 && !capabilities.HasFlag(DataCapabilities.Summaries)) throw new NotSupportedException("This source does not support summaries.");
    }
}
public sealed class LocalPageSource(GridTable table) : IGridPageSource
{
    public GridSchema Schema => table.Schema;
    public DataCapabilities Capabilities => DataCapabilities.Sort | DataCapabilities.Filter | DataCapabilities.Search;
    public ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); query = QuerySnapshot.Capture(query, Schema); DataSourceValidation.Validate(query, Capabilities, offset, count);
        using var view = new GridView(table); view.SetQuery(query);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GridPage(offset, view.FilteredRecords.Skip(offset).Take(count).ToArray(), view.FilteredRecords.Count, table.Revision.ToString()));
    }
}

/// <summary>POST JSON adapter. Credentials and HttpClient lifetime remain the caller's responsibility.</summary>
public sealed class HttpJsonPageSource(HttpClient client, Uri endpoint, GridSchema schema, DataCapabilities capabilities) : IGridPageSource
{
    public GridSchema Schema => schema;
    public DataCapabilities Capabilities => capabilities;
    public sealed record Request(GridQuery Query, int Offset, int Count);
    public sealed record RecordDto(long Id, JsonElement[] Values, long? ParentId = null);
    public sealed record Response(int Offset, RecordDto[] Records, int? TotalCount, string? Revision);
    public async ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken)
    {
        query = QuerySnapshot.Capture(query, Schema);
        DataSourceValidation.Validate(query, Capabilities, offset, count);
        using var response = await client.PostAsJsonAsync(endpoint, new Request(query, offset, count), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<Response>(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Empty page response.");
        if (payload.Offset != offset || payload.Records.Length > count || payload.TotalCount is < 0) throw new InvalidDataException("Invalid page metadata.");
        var table = new GridTable(schema);
        table.AddRange(payload.Records.Select(row => new GridRecord(row.Id, row.Values.Cast<object?>(), parentId: row.ParentId)));
        return new(offset, table.Records.ToArray(), payload.TotalCount, payload.Revision);
    }
}

/// <summary>Bounded LRU cache with coalesced reads, independent caller cancellation and stale-query suppression.</summary>
public sealed class GridPageCache : IDisposable
{
    private sealed record Entry(int Page, TaskCompletionSource<GridPage> Completion, LinkedListNode<int> Node, int Generation);
    private readonly object _gate = new();
    private readonly IGridPageSource _source;
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly LinkedList<int> _lru = [];
    private CancellationTokenSource _generationCancellation = new();
    private GridQuery _query = new();
    private int _generation;
    private bool _disposed;
    public int PageSize { get; }
    public int Capacity { get; }
    public int CachedPageCount { get { lock (_gate) return _entries.Count; } }
    public GridPageCache(IGridPageSource source, int pageSize = 128, int capacity = 16)
    {
        if (pageSize < 1 || pageSize > 10000 || capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _source = source; PageSize = pageSize; Capacity = capacity;
    }
    public void SetQuery(GridQuery query)
    {
        query = QuerySnapshot.Capture(query, _source.Schema);
        DataSourceValidation.Validate(query, _source.Capabilities, 0, PageSize);
        CancellationTokenSource previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _generationCancellation; _generationCancellation = new(); _generation++; _query = query;
            foreach (var entry in _entries.Values) entry.Completion.TrySetCanceled();
            _entries.Clear(); _lru.Clear();
        }
        previous.Cancel(); previous.Dispose();
    }
    public Task<GridPage> GetPageAsync(int page, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        _ = checked(page * PageSize);
        Entry entry; GridQuery query; CancellationToken generationToken; bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(page, out entry!)) { _lru.Remove(entry.Node); _lru.AddFirst(entry.Node); }
            else
            {
                var node = _lru.AddFirst(page);
                entry = new(page, new(TaskCreationOptions.RunContinuationsAsynchronously), node, _generation);
                _entries.Add(page, entry); start = true;
                while (_entries.Count > Capacity)
                {
                    int last = _lru.Last!.Value; _lru.RemoveLast(); _entries.Remove(last);
                }
            }
            query = _query; generationToken = _generationCancellation.Token;
        }
        if (start) _ = LoadAsync(entry, query, generationToken);
        return cancellationToken.CanBeCanceled ? entry.Completion.Task.WaitAsync(cancellationToken) : entry.Completion.Task;
    }
    private async Task LoadAsync(Entry entry, GridQuery query, CancellationToken token)
    {
        try
        {
            var page = await _source.FetchAsync(query, checked(entry.Page * PageSize), PageSize, token).ConfigureAwait(false);
            if (page.Offset != entry.Page * PageSize || page.Records.Count > PageSize || page.TotalCount is < 0 || page.TotalCount is int total && page.Records.Count > 0 && page.Offset + page.Records.Count > total)
                throw new InvalidDataException("The source returned inconsistent page metadata.");
            if (page.Records.Select(row => row.Id).Distinct().Count() != page.Records.Count) throw new InvalidDataException("Duplicate row identities within a page.");
            lock (_gate)
            {
                if (_disposed || entry.Generation != _generation || token.IsCancellationRequested) entry.Completion.TrySetCanceled();
                else entry.Completion.TrySetResult(page);
            }
        }
        catch (OperationCanceledException) { RemoveFailed(entry); entry.Completion.TrySetCanceled(); }
        catch (Exception error) { RemoveFailed(entry); entry.Completion.TrySetException(error); }
    }
    private void RemoveFailed(Entry entry)
    {
        lock (_gate)
            if (_entries.TryGetValue(entry.Page, out var current) && ReferenceEquals(current, entry)) { _entries.Remove(entry.Page); _lru.Remove(entry.Node); }
    }
    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; cancellation = _generationCancellation;
            foreach (var entry in _entries.Values) entry.Completion.TrySetCanceled();
            _entries.Clear(); _lru.Clear();
        }
        cancellation.Cancel(); cancellation.Dispose();
    }
}

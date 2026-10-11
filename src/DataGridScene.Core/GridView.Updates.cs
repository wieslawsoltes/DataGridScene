namespace DataGridScene.Core;

public enum GridViewChangeKind { Reset, Values, Expansion }

public sealed partial class GridView
{
    private HashSet<string>? _shapingDependencies;
    private long _knownTableRevision;
    private int _deferDepth;
    private GridQuery? _pendingQuery;
    private bool _refreshPending, _flattenPending, _valuesPending;
    private Dictionary<string, int> _specialRowIndexes = new(StringComparer.Ordinal);
    public GridViewChangeKind LastChangeKind { get; private set; }
    public long RowsRevision { get; private set; }
    public long RefreshCount { get; private set; }
    public long ValueUpdateCount { get; private set; }
    public double LastRefreshMilliseconds { get; private set; }
    public bool IsRefreshDeferred => _deferDepth > 0;
    /// <summary>The staged query within DeferRefresh; Query always describes the published rows.</summary>
    public GridQuery RequestedQuery => _pendingQuery ?? Query;
    public bool HasNonDataRows => _specialRowIndexes.Count != 0;

    /// <summary>Batch query, expansion and table notifications into one refresh on outer disposal.
    /// Not a data transaction: table edits are committed immediately. Use short synchronous scopes.
    /// Query/Rows stay paired until successful publication; rejected query shaping leaves that pair intact.</summary>
    public IDisposable DeferRefresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _deferDepth++; return new RefreshScope(this);
    }
    private sealed class RefreshScope(GridView owner) : IDisposable
    {
        private GridView? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndRefresh();
    }
    private void EndRefresh()
    {
        if (--_deferDepth != 0 || _disposed) return;
        var query = _pendingQuery ?? Query;
        bool refresh = _refreshPending, flatten = _flattenPending, values = _valuesPending;
        _pendingQuery = null; _refreshPending = _flattenPending = _valuesPending = false;
        if (refresh) { Build(query); Query = query; Notify(); }
        else if (flatten) Reflatten();
        else if (values) PublishValues();
    }
    private void PublishValues() { ValueUpdateCount++; Notify(GridViewChangeKind.Values); }
    private void HandleTableChange(GridChangedEventArgs change)
    {
        if (_disposed) return;
        bool valuesOnly = !_refreshPending && change.Kind == GridChangeKind.Edit &&
            change.Revision == _knownTableRevision + 1 && change.Revision == Table.Revision &&
            change.CellChanges is { Count: > 0 } cells && _shapingDependencies is not null &&
            cells.All(cell => !_shapingDependencies.Contains(cell.Field));
        if (valuesOnly)
        {
            _knownTableRevision = change.Revision;
            if (_deferDepth > 0) _valuesPending = true;
            else PublishValues();
        }
        else Refresh();
    }
    /// <summary>Stable-key lookup without a second index for every data row.</summary>
    public int IndexOfRow(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.StartsWith("row:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(4),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id))
        {
            int index = IndexOfRecord(id);
            return index >= 0 && Rows[index].Key == key ? index : -1;
        }
        return _specialRowIndexes.GetValueOrDefault(key, -1);
    }
}

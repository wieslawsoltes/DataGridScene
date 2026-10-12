namespace DataGridScene.Core;

/// <summary>Version-checked edit transactions. External writes invalidate undo history.</summary>
public sealed class EditHistory
{
    private readonly GridTable _table;
    internal GridTable Table => _table;
    private readonly Stack<ChangeSet> _undo = [];
    private readonly Stack<ChangeSet> _redo = [];
    private long _knownRevision;
    private bool _applying;
    public int Capacity { get; set; } = 100;
    public bool CanUndo { get { Synchronize(); return _undo.Count > 0; } }
    public bool CanRedo { get { Synchronize(); return _redo.Count > 0; } }
    public event EventHandler? Changed;
    public EditHistory(GridTable table) { _table = table; _knownRevision = table.Revision; }
    private void Synchronize()
    {
        // Table notifications can query command availability during a transaction.
        if (_applying || _knownRevision == _table.Revision) return;
        _undo.Clear(); _redo.Clear(); _knownRevision = _table.Revision;
    }
    public EditTransaction Begin(long recordId) => new(this, _table, recordId);
    private ChangeSet Apply(IEnumerable<CellEdit> edits, IReadOnlyDictionary<long, int>? expectedVersions = null)
    {
        if (_applying) throw new InvalidOperationException("Reentrant history mutation is not supported.");
        _applying = true;
        try { return _table.Apply(edits, expectedVersions); }
        finally { _knownRevision = _table.Revision; _applying = false; }
    }
    public ChangeSet Execute(IEnumerable<CellEdit> edits, IReadOnlyDictionary<long, int>? expectedVersions = null)
    {
        Synchronize(); var change = Apply(edits, expectedVersions);
        if (change.Changes.Count > 0)
        {
            _undo.Push(change); _redo.Clear();
            if (_undo.Count > Math.Max(1, Capacity))
            {
                var newest = _undo.Take(Math.Max(1, Capacity)).Reverse().ToArray();
                _undo.Clear(); foreach (var item in newest) _undo.Push(item);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return change;
    }
    public bool Undo()
    {
        Synchronize(); if (_undo.Count == 0) return false;
        var change = _undo.Peek();
        Apply(change.Changes.Select(cell => new CellEdit(cell.RecordId, cell.Field, cell.Before)));
        _undo.Pop(); _redo.Push(change); Changed?.Invoke(this, EventArgs.Empty); return true;
    }
    public bool Redo()
    {
        Synchronize(); if (_redo.Count == 0) return false;
        var change = _redo.Peek();
        Apply(change.Changes.Select(cell => new CellEdit(cell.RecordId, cell.Field, cell.After)));
        _redo.Pop(); _undo.Push(change); Changed?.Invoke(this, EventArgs.Empty); return true;
    }
}
public sealed class EditTransaction
{
    private readonly EditHistory _history;
    private readonly GridTable _table;
    private readonly int _version;
    private readonly GridRecord _record;
    private readonly Dictionary<string, object?> _pending = new(StringComparer.OrdinalIgnoreCase);
    public long RecordId { get; }
    public bool IsCompleted { get; private set; }
    internal EditTransaction(EditHistory history, GridTable table, long recordId)
    { _history = history; _table = table; RecordId = recordId; _record = table[recordId]; _version = _record.Version; }
    public void Set(string field, object? value)
    {
        ObjectDisposedException.ThrowIf(IsCompleted, this);
        _ = _table.Schema[field]; _pending[field] = value;
    }
    public ChangeSet Commit()
    {
        ObjectDisposedException.ThrowIf(IsCompleted, this);
        if (!_table.Contains(RecordId) || !ReferenceEquals(_record, _table[RecordId])) throw new GridConcurrencyException($"Record {RecordId} was removed or replaced.");
        var result = _history.Execute(_pending.Select(pair => new CellEdit(RecordId, pair.Key, pair.Value)), new Dictionary<long, int> { [RecordId] = _version });
        IsCompleted = true; return result;
    }
    public void Cancel() { _pending.Clear(); IsCompleted = true; }
}

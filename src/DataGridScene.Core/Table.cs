using System.Collections.ObjectModel;

namespace DataGridScene.Core;

public sealed class GridRecord
{
    private object?[] _values;
    public long Id { get; }
    public int Version { get; private set; }
    public object? Tag { get; }
    public long? ParentId { get; }
    public object? this[int index] => _values[index];
    public int Count => _values.Length;

    public GridRecord(long id, IEnumerable<object?> values, object? tag = null, long? parentId = null)
    {
        Id = id; _values = values.ToArray(); Tag = tag; ParentId = parentId;
    }
    public object?[] CopyValues() => (object?[])_values.Clone();
    internal void Replace(object?[] values) { _values = values; Version++; }
}

public sealed record GridValidationError(long? RecordId, string? Field, string Message);
public sealed class GridValidationException(IReadOnlyList<GridValidationError> errors) : Exception(string.Join("; ", errors.Select(e => e.Field is null ? e.Message : $"{e.Field}: {e.Message}")))
{
    public IReadOnlyList<GridValidationError> Errors { get; } = errors;
}
public sealed class GridConcurrencyException(string message) : InvalidOperationException(message);
public sealed record CellEdit(long RecordId, string Field, object? Value);
public sealed record CellChange(long RecordId, string Field, object? Before, object? After);
public sealed record ChangeSet(IReadOnlyList<CellChange> Changes, long Revision);
public enum GridChangeKind { Reset, Insert, Remove, Edit }
public sealed class GridChangedEventArgs(GridChangeKind kind, long revision) : EventArgs
{
    public GridChangeKind Kind { get; } = kind;
    public long Revision { get; } = revision;
}

/// <summary>UI-independent, single-writer table. Marshal writes to the owning synchronization context.</summary>
public sealed class GridTable
{
    private readonly List<GridRecord> _rows = [];
    private readonly Dictionary<long, GridRecord> _byId = [];
    private int _updateDepth;
    private bool _pendingChange;
    public GridSchema Schema { get; }
    public ReadOnlyCollection<GridRecord> Records { get; }
    public long Revision { get; private set; }
    public event EventHandler<GridChangedEventArgs>? Changed;

    public GridTable(GridSchema schema) { Schema = schema; Records = _rows.AsReadOnly(); }
    public GridRecord this[long id] => _byId.TryGetValue(id, out var row) ? row : throw new KeyNotFoundException($"Record {id} no longer exists.");
    public bool Contains(long id) => _byId.ContainsKey(id);

    public IDisposable BeginUpdate()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }
    private sealed class UpdateScope(GridTable table) : IDisposable
    {
        private GridTable? _table = table;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _table, null);
            if (owner is null) return;
            if (--owner._updateDepth == 0 && owner._pendingChange)
            {
                owner._pendingChange = false;
                owner.Changed?.Invoke(owner, new(GridChangeKind.Reset, owner.Revision));
            }
        }
    }
    private void Notify(GridChangeKind kind)
    {
        Revision++;
        if (_updateDepth > 0) _pendingChange = true;
        else Changed?.Invoke(this, new(kind, Revision));
    }

    public void Add(GridRecord record) => AddRange([record]);
    public void AddRange(IEnumerable<GridRecord> records)
    {
        var incoming = records.ToArray();
        var ids = new HashSet<long>();
        var normalized = new List<GridRecord>(incoming.Length);
        foreach (var record in incoming)
        {
            if (_byId.ContainsKey(record.Id) || !ids.Add(record.Id)) throw new ArgumentException($"Duplicate record identity: {record.Id}");
            var values = Schema.Normalize(record.CopyValues());
            normalized.Add(new GridRecord(record.Id, values, record.Tag, record.ParentId));
        }
        if (normalized.Count == 0) return;
        foreach (var record in normalized) { _rows.Add(record); _byId.Add(record.Id, record); }
        Notify(GridChangeKind.Insert);
    }

    public void Remove(IEnumerable<long> recordIds)
    {
        var ids = recordIds.ToHashSet();
        foreach (long id in ids) if (!_byId.ContainsKey(id)) throw new KeyNotFoundException($"Record {id} no longer exists.");
        if (ids.Count == 0) return;
        _rows.RemoveAll(row => ids.Contains(row.Id));
        foreach (long id in ids) _byId.Remove(id);
        Notify(GridChangeKind.Remove);
    }

    public void Clear()
    {
        if (_rows.Count == 0) return;
        _rows.Clear(); _byId.Clear(); Notify(GridChangeKind.Reset);
    }

    /// <summary>Validate and stage every cell before committing any mutation.</summary>
    public ChangeSet Apply(IEnumerable<CellEdit> edits, IReadOnlyDictionary<long, int>? expectedVersions = null)
    {
        var candidates = new Dictionary<long, object?[]>();
        var seen = new HashSet<(long, int)>();
        var changes = new List<CellChange>();
        var errors = new List<GridValidationError>();
        foreach (var edit in edits)
        {
            var row = this[edit.RecordId];
            if (expectedVersions is not null && (!expectedVersions.TryGetValue(row.Id, out int version) || row.Version != version))
                throw new GridConcurrencyException($"Record {row.Id} changed while it was being edited.");
            var column = Schema[edit.Field];
            if (!seen.Add((row.Id, column.Index))) throw new ArgumentException($"Duplicate edit for {row.Id}/{column.Field}.");
            if (column.IsReadOnly) { errors.Add(new(row.Id, column.Field, "This field is read-only.")); continue; }
            if (!column.TryConvert(edit.Value, Schema.Culture, out var value, out string? error))
            { errors.Add(new(row.Id, column.Field, error!)); continue; }
            if (Equals(row[column.Index], value)) continue;
            if (!candidates.TryGetValue(row.Id, out var values)) candidates[row.Id] = values = row.CopyValues();
            values[column.Index] = value;
            changes.Add(new(row.Id, column.Field, row[column.Index], value));
        }
        foreach (var (id, values) in candidates)
        {
            string? error = Schema.RowValidator?.Invoke(values);
            if (error is not null) errors.Add(new(id, null, error));
        }
        if (errors.Count > 0) throw new GridValidationException(errors);
        if (changes.Count == 0) return new(changes.AsReadOnly(), Revision);
        foreach (var (id, values) in candidates) this[id].Replace(values);
        Notify(GridChangeKind.Edit);
        return new(changes.AsReadOnly(), Revision);
    }
}

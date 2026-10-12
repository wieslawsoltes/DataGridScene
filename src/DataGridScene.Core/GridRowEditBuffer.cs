using System.Collections.ObjectModel;
using System.Globalization;

namespace DataGridScene.Core;

/// <summary>A staged, single-row edit with one version check and one undo transaction.
/// No table writes, validation calls or subscriptions occur while values are staged.
/// Like GridTable, use this object only on the owning single-writer context.</summary>
public sealed class GridRowEditBuffer : IDisposable
{
    private readonly GridTable _table;
    private readonly EditHistory _history;
    private readonly GridRecord _record;
    private readonly int _version;
    private readonly Func<bool> _canEdit;
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object?[] _original, _values;
    private bool _committing;
    public long RecordId => _record.Id;
    public int OriginalVersion => _version;
    public CultureInfo Culture => _table.Schema.Culture;
    public ReadOnlyCollection<GridColumn> Columns { get; }
    public bool IsActive { get; private set; } = true;
    public bool CanEdit => IsActive && _canEdit();
    public bool IsDirty => IsActive && Enumerable.Range(0, _values.Length).Any(i => !Equals(_original[i], _values[i]));
    public string? Error { get; private set; }
    public IReadOnlyList<GridValidationError> Errors { get; private set; } = Array.Empty<GridValidationError>();
    public ChangeSet? CommittedChange { get; private set; }

    public GridRowEditBuffer(GridTable table, EditHistory history, long recordId,
        IEnumerable<string>? fields = null, Func<bool>? canEdit = null)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(history);
        if (!ReferenceEquals(history.Table, table)) throw new ArgumentException("History must belong to the row's table.", nameof(history));
        _table = table; _history = history; _record = table[recordId]; _version = _record.Version;
        _canEdit = canEdit ?? (() => true);
        var columns = fields is null ? table.Schema.Columns.ToArray() : fields.Select(field => table.Schema[field]).ToArray();
        if (columns.Length == 0) throw new ArgumentException("At least one field is required.", nameof(fields));
        for (int i = 0; i < columns.Length; i++)
            if (!_indices.TryAdd(columns[i].Field, i)) throw new ArgumentException("Fields must be unique.", nameof(fields));
        Columns = Array.AsReadOnly(columns);
        _original = columns.Select(column => column.GetValue(_record)).ToArray();
        _values = (object?[])_original.Clone();
    }
    private int Index(string field)
    {
        ObjectDisposedException.ThrowIf(!IsActive, this);
        return _indices.TryGetValue(field, out int index) ? index : throw new ArgumentException($"Field is not part of this row form: {field}", nameof(field));
    }
    public object? GetValue(string field) => _values[Index(field)];
    public object? GetOriginalValue(string field) => _original[Index(field)];
    public void SetValue(string field, object? value)
    {
        int index = Index(field);
        if (_committing) throw new InvalidOperationException("A row commit is in progress.");
        if (!CanEdit || Columns[index].IsReadOnly) throw new InvalidOperationException("This field is read-only.");
        _values[index] = value;
        Error = null; Errors = Array.Empty<GridValidationError>();
    }
    public bool TryCommit()
    {
        if (_committing) return false;
        try
        {
            ObjectDisposedException.ThrowIf(!IsActive, this);
            if (!CanEdit) throw new InvalidOperationException("This row is read-only.");
            // Check even an untouched form: empty edit sets otherwise have no versions
            // for GridTable.Apply to inspect. Never silently accept a replaced identity.
            if (!_table.Contains(RecordId) || !ReferenceEquals(_table[RecordId], _record))
                throw new GridConcurrencyException($"Record {RecordId} was removed or replaced.");
            if (_record.Version != _version) throw new GridConcurrencyException($"Record {RecordId} changed while it was being edited. Cancel and reopen the form.");
            _committing = true;
            var edits = Enumerable.Range(0, _values.Length).Where(i => !Equals(_original[i], _values[i]))
                .Select(i => new CellEdit(RecordId, Columns[i].Field, _values[i])).ToArray();
            CommittedChange = _history.Execute(edits, new Dictionary<long, int> { [RecordId] = _version });
            Error = null; Errors = Array.Empty<GridValidationError>(); IsActive = false;
            Array.Clear(_original); Array.Clear(_values);
            return true;
        }
        catch (Exception error)
        {
            Error = error.Message;
            Errors = error is GridValidationException validation ? Array.AsReadOnly(validation.Errors.ToArray())
                : Array.AsReadOnly(new[] { new GridValidationError(RecordId, null, error.Message) });
            return false;
        }
        finally { _committing = false; }
    }
    public void Dispose()
    {
        if (_committing) throw new InvalidOperationException("A row commit is in progress.");
        IsActive = false; Array.Clear(_original); Array.Clear(_values);
        Error = null; Errors = Array.Empty<GridValidationError>();
    }
}

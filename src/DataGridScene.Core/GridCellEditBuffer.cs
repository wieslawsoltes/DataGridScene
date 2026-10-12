using System.ComponentModel;

namespace DataGridScene.Core;

/// <summary>A single-identity, version-checked binding buffer. Recycled visuals receive a new
/// buffer; a retained old buffer can never write to the replacement cell. All methods are
/// single-writer and must run on the owning UI context.</summary>
public sealed class GridCellEditBuffer : INotifyPropertyChanged, IDisposable
{
    private readonly GridTable _table;
    private readonly EditHistory _history;
    private readonly Func<bool> _canEdit;
    private int _version;
    private object? _original, _value;
    private bool _committing;
    private int _bindingDepth;
    public System.Globalization.CultureInfo Culture => _table.Schema.Culture;
    public GridRecord Record { get; }
    public GridColumn Column { get; }
    public CellAddress Address => new(Record.Id, Column.Field);
    public long RecordId => Record.Id;
    public string Field => Column.Field;
    public bool IsActive { get; private set; } = true;
    public bool CanEdit => IsActive && !Column.IsReadOnly && _canEdit();
    public bool IsDirty => !Equals(_original, _value);
    public bool IsCommitting => _committing;
    public string? Error { get; private set; }
    public object? Value
    {
        get => _value;
        set
        {
            // Late binding/event callbacks from a recycled visual are harmless.
            if (!IsActive || _committing || _bindingDepth > 0 || Equals(_value, value)) return;
            _value = value; Error = null; Notify();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public GridCellEditBuffer(GridTable table, EditHistory history, long recordId, string field, Func<bool>? canEdit = null)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(history);
        if (!ReferenceEquals(table, history.Table)) throw new ArgumentException("Edit history must belong to the binding table.", nameof(history));
        _table = table; _history = history; Record = table[recordId]; Column = table.Schema[field];
        _canEdit = canEdit ?? (() => true); ReadCurrent();
    }
    private void ReadCurrent() { _version = Record.Version; _original = _value = Column.GetValue(Record); }
    private void RequireIdentity()
    {
        if (!IsActive) throw new ObjectDisposedException(nameof(GridCellEditBuffer), "The cell binding has been recycled.");
        if (!_table.Contains(RecordId) || !ReferenceEquals(_table[RecordId], Record))
            throw new GridConcurrencyException($"Record {RecordId} was removed or replaced.");
    }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    /// <summary>Update pristine buffers after external writes; never overwrite an uncommitted edit.</summary>
    public void Refresh()
    {
        if (!IsActive || _committing) return;
        if (!IsDirty && _table.Contains(RecordId) && ReferenceEquals(_table[RecordId], Record)) ReadCurrent();
        Notify();
    }
    public bool TryCommit()
    {
        if (_committing || _bindingDepth > 0) return true;
        try
        {
            RequireIdentity();
            if (!IsDirty) { Error = null; return true; }
            if (!CanEdit) throw new InvalidOperationException("This cell is read-only.");
            _committing = true;
            _history.Execute([new(RecordId, Field, _value)], new Dictionary<long,int> { [RecordId] = _version });
            ReadCurrent(); Error = null; return true;
        }
        catch (Exception error) { Error = error.Message; return false; }
        finally { _committing = false; Notify(); }
    }
    public void Cancel()
    {
        if (!IsActive || _committing) return;
        if (_table.Contains(RecordId) && ReferenceEquals(_table[RecordId], Record)) ReadCurrent();
        else _value = _original;
        Error = null; Notify();
    }
    /// <summary>Push model values into native controls without accepting their synchronous change events as user edits.</summary>
    public void SynchronizeBinding(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _bindingDepth++;
        try { update(); } finally { _bindingDepth--; }
    }
    /// <summary>Invoke trusted cell actions and surface errors through the binding validation state.</summary>
    public bool Invoke(Action<GridCellEditBuffer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try { RequireIdentity(); action(this); Error = null; return true; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { Notify(); }
    }
    public void Dispose()
    {
        if (!IsActive) return;
        IsActive = false; _value = _original = null; PropertyChanged = null;
    }
}

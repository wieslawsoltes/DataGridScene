using System.Collections.Immutable;

namespace DataGridScene.Core;

public readonly record struct CellAddress(long RecordId, string Field);
public enum GridSelectionMode { MultipleRows, SingleRow, Cells }

/// <summary>
/// Identity-based selection. Rectangular cell selections share immutable field sets across rows,
/// avoiding one HashSet allocation per row when selecting a large block or the entire data set.
/// </summary>
public sealed partial class GridSelection
{
    private readonly HashSet<long> _selected = [];
    private readonly Dictionary<long, ImmutableHashSet<string>> _cells = [];
    private long _selectedCellCount;
    private GridSelectionMode _mode;
    private int _updateDepth;
    private bool _pending;
    public GridSelectionMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_mode == value) return;
            _mode = value; Clear();
        }
    }
    public CellAddress? FocusedCell { get; private set; }
    public CellAddress? Anchor { get; private set; }
    /// <summary>Number of selected rows, or rows containing selected cells in cell mode.</summary>
    public int Count => Mode == GridSelectionMode.Cells ? _cells.Count : _selected.Count;
    public long SelectedCellCount => Mode == GridSelectionMode.Cells ? _selectedCellCount : 0;
    public IReadOnlyList<long> SelectedIds => EnumerateSelectedIds().ToArray();
    /// <summary>Enumerate current IDs without copying the whole selection. Do not mutate while enumerating.</summary>
    public IEnumerable<long> EnumerateSelectedIds()
    {
        if (Mode == GridSelectionMode.Cells)
        { foreach (long id in _cells.Keys) yield return id; }
        else { foreach (long id in _selected) yield return id; }
    }
    private void SetCells(long id, ImmutableHashSet<string> fields)
    {
        _selectedCellCount += fields.Count - (_cells.TryGetValue(id, out var old) ? old.Count : 0);
        if (fields.IsEmpty) _cells.Remove(id); else _cells[id] = fields;
    }
    private void RemoveCells(long id)
    {
        if (_cells.Remove(id, out var fields)) _selectedCellCount -= fields.Count;
    }
    private void ClearCells() { _cells.Clear(); _selectedCellCount = 0; }
    public event EventHandler? Changed;
    public bool Contains(long id) => Mode == GridSelectionMode.Cells ? _cells.ContainsKey(id) : _selected.Contains(id);
    public bool ContainsCell(long id, string field) => Mode == GridSelectionMode.Cells
        ? _cells.TryGetValue(id, out var fields) && fields.Contains(field) : _selected.Contains(id);
    public int SelectedFieldsInRow(long id) => _cells.TryGetValue(id, out var fields) ? fields.Count : 0;
    public IEnumerable<CellAddress> EnumerateCells()
    {
        foreach (var (id, fields) in _cells)
            foreach (string field in fields) yield return new(id, field);
    }
    private void Notify()
    {
        if (_updateDepth > 0) _pending = true;
        else Changed?.Invoke(this, EventArgs.Empty);
    }
    public IDisposable BeginUpdate()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }
    private sealed class UpdateScope(GridSelection owner) : IDisposable
    {
        private GridSelection? _owner = owner;
        public void Dispose()
        {
            var selection = Interlocked.Exchange(ref _owner, null);
            if (selection is null) return;
            if (--selection._updateDepth == 0 && selection._pending)
            { selection._pending = false; selection.Notify(); }
        }
    }
    private static CellAddress Canonical(GridView view, CellAddress cell)
    {
        _ = view.Table[cell.RecordId];
        return cell with { Field = view.Table.Schema[cell.Field].Field };
    }
    private static string[] Order(GridView view, IReadOnlyList<string>? columnOrder)
    {
        string[] fields = columnOrder is null ? view.Table.Schema.Columns.Select(column => column.Field).ToArray()
            : columnOrder.Select(field => view.Table.Schema[field].Field).ToArray();
        if (fields.Length == 0 || fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
            throw new ArgumentException("Selection columns must be nonempty and unique.", nameof(columnOrder));
        return fields;
    }
    public void Focus(GridView view, CellAddress cell, bool setAnchor = false)
    {
        cell = Canonical(view, cell); FocusedCell = cell;
        if (setAnchor || Anchor is null) Anchor = cell;
        Notify();
    }
    public void Select(GridView view, CellAddress cell, bool extend = false, bool toggle = false, IReadOnlyList<string>? columnOrder = null)
    {
        cell = Canonical(view, cell);
        if (Mode == GridSelectionMode.SingleRow)
        {
            _selected.Clear(); _selected.Add(cell.RecordId); Anchor = FocusedCell = cell; Notify(); return;
        }
        string[]? fields = Mode == GridSelectionMode.Cells ? Order(view, columnOrder) : null;
        if (fields is not null && !fields.Contains(cell.Field, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The focused column is not in the displayed column order.", nameof(cell));
        if (extend && Anchor is { } anchor)
        {
            int a = view.IndexOfRecord(anchor.RecordId), b = view.IndexOfRecord(cell.RecordId);
            if (!toggle) { _selected.Clear(); ClearCells(); }
            ImmutableHashSet<string>? block = null;
            if (fields is not null)
            {
                int first = Array.FindIndex(fields, field => field.Equals(anchor.Field, StringComparison.OrdinalIgnoreCase));
                int last = Array.FindIndex(fields, field => field.Equals(cell.Field, StringComparison.OrdinalIgnoreCase));
                if (first < 0) first = last;
                block = fields.Skip(Math.Min(first, last)).Take(Math.Abs(last - first) + 1).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            }
            void Add(long id)
            {
                if (block is null) _selected.Add(id);
                else SetCells(id, toggle && _cells.TryGetValue(id, out var previous) ? previous.Union(block) : block);
            }
            if (a >= 0 && b >= 0)
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    if (view.Rows[i].Kind == GridRowKind.Data) Add(view.Rows[i].Record!.Id);
            }
            else Add(cell.RecordId);
        }
        else
        {
            if (!toggle) { _selected.Clear(); ClearCells(); }
            if (Mode == GridSelectionMode.Cells)
            {
                var previous = _cells.GetValueOrDefault(cell.RecordId) ?? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
                var next = toggle && previous.Contains(cell.Field) ? previous.Remove(cell.Field) : previous.Add(cell.Field);
                SetCells(cell.RecordId, next);
            }
            else if (!toggle || !_selected.Remove(cell.RecordId)) _selected.Add(cell.RecordId);
            Anchor = cell;
        }
        FocusedCell = cell; Notify();
    }
    public void SelectAll(GridView view, IReadOnlyList<string>? columnOrder = null)
    {
        var fields = Mode == GridSelectionMode.Cells ? Order(view, columnOrder).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) : null;
        _selected.Clear(); ClearCells();
        if (Mode == GridSelectionMode.SingleRow)
        {
            if (FocusedCell is { } cell && view.FilteredRecords.Any(row => row.Id == cell.RecordId)) _selected.Add(cell.RecordId);
            else if (view.FilteredRecords.FirstOrDefault() is { } first) _selected.Add(first.Id);
        }
        else
        {
            foreach (var record in view.FilteredRecords)
                if (fields is null) _selected.Add(record.Id); else SetCells(record.Id, fields);
        }
        Notify();
    }
    public void Clear()
    {
        _selected.Clear(); ClearCells(); FocusedCell = Anchor = null; Notify();
    }
    public void Prune(GridTable table)
    {
        bool changed = _selected.RemoveWhere(id => !table.Contains(id)) > 0;
        foreach (long id in _cells.Keys.Where(id => !table.Contains(id)).ToArray()) { RemoveCells(id); changed = true; }
        if (FocusedCell is { } focused && !table.Contains(focused.RecordId)) { FocusedCell = null; changed = true; }
        if (Anchor is { } anchor && !table.Contains(anchor.RecordId)) Anchor = null;
        if (changed) Notify();
    }
}

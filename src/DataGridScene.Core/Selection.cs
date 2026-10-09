namespace DataGridScene.Core;

public readonly record struct CellAddress(long RecordId, string Field);
public sealed class GridSelection
{
    private readonly HashSet<long> _selected = [];
    public CellAddress? FocusedCell { get; private set; }
    public CellAddress? Anchor { get; private set; }
    public int Count => _selected.Count;
    public IReadOnlyList<long> SelectedIds => _selected.ToArray();
    public event EventHandler? Changed;
    public bool Contains(long id) => _selected.Contains(id);
    public void Select(GridView view, CellAddress cell, bool extend = false, bool toggle = false)
    {
        _ = view.Table[cell.RecordId]; _ = view.Table.Schema[cell.Field];
        if (extend && Anchor is { } anchor)
        {
            int a = view.IndexOfRecord(anchor.RecordId), b = view.IndexOfRecord(cell.RecordId);
            if (!toggle) _selected.Clear();
            if (a >= 0 && b >= 0)
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (view.Rows[i].Kind == GridRowKind.Data) _selected.Add(view.Rows[i].Record!.Id);
            else _selected.Add(cell.RecordId);
        }
        else
        {
            if (!toggle) _selected.Clear();
            if (!toggle || !_selected.Remove(cell.RecordId)) _selected.Add(cell.RecordId);
            Anchor = cell;
        }
        FocusedCell = cell; Changed?.Invoke(this, EventArgs.Empty);
    }
    public void SelectAll(GridView view)
    {
        _selected.Clear(); foreach (var record in view.FilteredRecords) _selected.Add(record.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Clear() { _selected.Clear(); FocusedCell = Anchor = null; Changed?.Invoke(this, EventArgs.Empty); }
    public void Prune(GridTable table)
    {
        bool changed = _selected.RemoveWhere(id => !table.Contains(id)) > 0;
        if (FocusedCell is { } focused && !table.Contains(focused.RecordId)) { FocusedCell = null; changed = true; }
        if (Anchor is { } anchor && !table.Contains(anchor.RecordId)) Anchor = null;
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}

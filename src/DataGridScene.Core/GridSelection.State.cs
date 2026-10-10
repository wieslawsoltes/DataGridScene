using System.Collections.Immutable;

namespace DataGridScene.Core;

/// <summary>Rows sharing an identical field set use one block, keeping select-all snapshots compact.</summary>
public sealed record GridCellSelectionBlock(IReadOnlyList<long> RecordIds, IReadOnlyList<string> Fields);
public sealed record GridSelectionState(GridSelectionMode Mode, IReadOnlyList<long> SelectedRows,
    IReadOnlyList<GridCellSelectionBlock> CellBlocks, CellAddress? FocusedCell = null, CellAddress? Anchor = null);

public sealed partial class GridSelection
{
    private sealed class FieldSetComparer : IEqualityComparer<ImmutableHashSet<string>>
    {
        public bool Equals(ImmutableHashSet<string>? x, ImmutableHashSet<string>? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null && x.SetEquals(y);
        public int GetHashCode(ImmutableHashSet<string> value)
        {
            int hash = 0;
            foreach (string field in value) hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(field);
            return hash;
        }
    }
    public GridSelectionState CaptureState()
    {
        IReadOnlyList<GridCellSelectionBlock> blocks = Mode == GridSelectionMode.Cells
            ? Array.AsReadOnly(_cells.GroupBy(pair => pair.Value, new FieldSetComparer())
                .Select(group => new GridCellSelectionBlock(Array.AsReadOnly(group.Select(pair => pair.Key).Order().ToArray()),
                    Array.AsReadOnly(group.Key.Order(StringComparer.Ordinal).ToArray()))).ToArray())
            : Array.Empty<GridCellSelectionBlock>();
        return new(Mode, Array.AsReadOnly(_selected.Order().ToArray()), blocks, FocusedCell, Anchor);
    }
    /// <summary>Atomically restore identity-based selection; missing records and fields are omitted.</summary>
    public void RestoreState(GridView view, GridSelectionState state)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Mode) || state.SelectedRows is null || state.CellBlocks is null ||
            state.SelectedRows.Count > 1000000 || state.CellBlocks.Count > 100000)
            throw new ArgumentException("Invalid or oversized selection state.", nameof(state));
        if (state.Mode == GridSelectionMode.Cells ? state.SelectedRows.Count != 0 : state.CellBlocks.Count != 0)
            throw new ArgumentException("Selection data does not match its mode.", nameof(state));
        if (state.Mode == GridSelectionMode.SingleRow && state.SelectedRows.Count > 1)
            throw new ArgumentException("Single-row mode permits only one selected row.", nameof(state));
        var selected = state.SelectedRows.Where(view.Table.Contains).ToHashSet();
        var cells = new Dictionary<long, ImmutableHashSet<string>>();
        long count = 0;
        foreach (var block in state.CellBlocks)
        {
            if (block is null || block.Fields is null || block.RecordIds is null || block.Fields.Count > 16384 || block.RecordIds.Count > 1000000 ||
                block.Fields.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Invalid cell selection block.", nameof(state));
            count += (long)block.RecordIds.Count * block.Fields.Count;
            if (count > 2000000) throw new ArgumentException("Selection state exceeds two million cells.", nameof(state));
            var fields = block.Fields.Where(field => view.Table.Schema.TryGetColumn(field, out _))
                .Select(field => view.Table.Schema[field].Field).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            if (fields.IsEmpty) continue;
            foreach (long id in block.RecordIds)
                if (view.Table.Contains(id)) cells[id] = cells.TryGetValue(id, out var previous) ? previous.Union(fields) : fields;
        }
        CellAddress? Resolve(CellAddress? address)
        {
            if (address is not { } cell || !view.Table.Contains(cell.RecordId) || string.IsNullOrWhiteSpace(cell.Field) ||
                !view.Table.Schema.TryGetColumn(cell.Field, out var column)) return null;
            return cell with { Field = column.Field };
        }
        var focus = Resolve(state.FocusedCell); var anchor = Resolve(state.Anchor);
        _mode = state.Mode;
        _selected.Clear(); _selected.UnionWith(selected);
        _cells.Clear(); foreach (var pair in cells) _cells.Add(pair.Key, pair.Value);
        FocusedCell = focus; Anchor = anchor;
        Notify();
    }
}

using DataGridScene.Core;
using DataGridScene.Layout;

namespace DataGridScene.Rendering.Skia;

public sealed record SummaryCell(string Key, string Field, string Text, RectD Bounds, int RowIndex = -1);

/// <summary>Shared summary line allocation for row heights, rendering, hit testing and diagnostics.</summary>
public static class SummaryGeometry
{
    public const double LineHeight = 20;
    public const double GroupLabelHeight = 28;
    public static int Lines(IEnumerable<SummaryDescriptor> items) => items.GroupBy(item => item.TargetField, StringComparer.OrdinalIgnoreCase).Select(group => group.Count()).DefaultIfEmpty(0).Max();
    public static double TotalHeight(GridQuery query, double minimum) => Math.Max(minimum, Lines(query.Summaries) * LineHeight + 10);
    public static double RowHeight(GridQuery query, GridRowKind kind, double minimum)
    {
        if (kind == GridRowKind.GroupFooter)
            return Math.Max(minimum, GroupLabelHeight + 8 + Lines(query.EffectiveGroupSummaries.Where(item => item.ShowInGroupFooter)) * LineHeight);
        if (kind == GridRowKind.Group)
            return Math.Max(30, query.GroupSummaryDisplay == GroupSummaryDisplayMode.AlignByColumns
                ? GroupLabelHeight + 8 + Lines(query.EffectiveGroupSummaries.Where(item => item.ShowInGroupRow)) * LineHeight : minimum);
        return minimum;
    }
    private static IReadOnlyList<SummaryCell> Cells(GridFrame frame, IEnumerable<SummaryDescriptor> items,
        IReadOnlyDictionary<string, object?> values, double top, int rowIndex = -1)
    {
        var slots = frame.ColumnsByField;
        var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cells = new List<SummaryCell>();
        foreach (var item in items)
        {
            int line = lines.GetValueOrDefault(item.TargetField); lines[item.TargetField] = line + 1;
            if (!slots.TryGetValue(item.TargetField, out var slot)) continue;
            cells.Add(new(item.Key, slot.Field, item.Display(values[item.Key], frame.View.Table.Schema),
                new(slot.ClipLeft, top + line * LineHeight, slot.ClipRight - slot.ClipLeft, LineHeight), rowIndex));
        }
        return cells.AsReadOnly();
    }
    public static IReadOnlyList<SummaryCell> TotalCells(GridFrame frame)
    {
        if (frame.TryGetSummaryCells(-1, out var cached)) return cached;
        return frame.CacheSummaryCells(-1, Cells(frame, frame.View.Query.Summaries, frame.View.Summaries, frame.FooterTop + 5));
    }
    public static IReadOnlyList<SummaryCell> GroupCells(GridFrame frame, int rowIndex)
    {
        if (frame.TryGetSummaryCells(rowIndex, out var cached)) return cached;
        var row = frame.View.Rows[rowIndex];
        if (row.Group is null || row.Kind == GridRowKind.Group && frame.View.Query.GroupSummaryDisplay != GroupSummaryDisplayMode.AlignByColumns) return Array.Empty<SummaryCell>();
        var items = frame.View.Query.EffectiveGroupSummaries.Where(item => row.Kind == GridRowKind.GroupFooter ? item.ShowInGroupFooter : item.ShowInGroupRow);
        return frame.CacheSummaryCells(rowIndex, Cells(frame, items, row.Group.Summaries, frame.RowBounds(rowIndex).Y + GroupLabelHeight + 4, rowIndex));
    }
    public static IEnumerable<SummaryCell> VisibleCells(GridFrame frame)
    {
        foreach (var cell in TotalCells(frame)) yield return cell;
        for (int i = frame.VisibleRows.Start; i < frame.VisibleRows.EndExclusive; i++)
            if (frame.View.Rows[i].Group is not null)
                foreach (var cell in GroupCells(frame, i))
                    if (cell.Bounds.Bottom > frame.BodyTop && cell.Bounds.Y < frame.FooterTop) yield return cell;
    }
}

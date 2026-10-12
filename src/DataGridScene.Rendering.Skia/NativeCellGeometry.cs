using DataGridScene.Core;

namespace DataGridScene.Rendering.Skia;

public sealed record NativeCellPlacement(CellAddress Address, RectD Bounds, RectD ClipBounds)
{
    /// <summary>Zero-based row-major focus rank among the requested visible cells.
    /// Independent of record IDs, painter order, and recycled visual insertion order.</summary>
    public int TabIndex { get; init; }
}

/// <summary>Enumerates only intersecting data cells, retaining full cell dimensions and
/// a separate clip. Native child controls must not shrink when a frozen pane clips them.</summary>
public static class NativeCellGeometry
{
    public static IReadOnlyList<NativeCellPlacement> Collect(GridFrame frame, IReadOnlySet<string> fields, int maximumCells = 4096)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(fields);
        if (maximumCells < 1) throw new ArgumentOutOfRangeException(nameof(maximumCells));
        if (fields.Count == 0) return Array.Empty<NativeCellPlacement>();
        var result = new List<NativeCellPlacement>();
        // Painter order may put frozen panes last. Keyboard order must instead follow
        // their actual visible position. Sort only the intersecting column slots once,
        // never all schema columns, all records, or the attached control collection.
        var columns = frame.Columns.Slots.Where(column => fields.Contains(column.Field) && column.ClipRight > column.ClipLeft)
            .OrderBy(column => column.ClipLeft).ToArray();
        if (columns.Length == 0) return Array.Empty<NativeCellPlacement>();
        double bodyBottom = Math.Min(frame.Height, frame.FooterTop);
        for (int i = frame.VisibleRows.Start; i < frame.VisibleRows.EndExclusive; i++)
        {
            var row = frame.View.Rows[i]; if (row.Kind != GridRowKind.Data) continue;
            var bounds = frame.RowBounds(i);
            double top = Math.Max(bounds.Y, frame.BodyTop), bottom = Math.Min(bounds.Bottom, bodyBottom);
            if (bottom <= top) continue;
            foreach (var column in columns)
            {
                if (result.Count == maximumCells) throw new InvalidOperationException($"Native cell limit ({maximumCells}) exceeded. Increase row height or reduce templated columns.");
                result.Add(new(new(row.Record!.Id, column.Field), new(column.X, bounds.Y, column.Width, bounds.Height),
                    new(column.ClipLeft, top, column.ClipRight - column.ClipLeft, bottom - top)) { TabIndex = result.Count });
            }
        }
        return result.AsReadOnly();
    }
}

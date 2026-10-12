using DataGridScene.Core;

namespace DataGridScene.Rendering.Skia;

public sealed record NativeCellPlacement(CellAddress Address, RectD Bounds, RectD ClipBounds);

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
        double bodyBottom = Math.Min(frame.Height, frame.FooterTop);
        for (int i = frame.VisibleRows.Start; i < frame.VisibleRows.EndExclusive; i++)
        {
            var row = frame.View.Rows[i]; if (row.Kind != GridRowKind.Data) continue;
            var bounds = frame.RowBounds(i);
            double top = Math.Max(bounds.Y, frame.BodyTop), bottom = Math.Min(bounds.Bottom, bodyBottom);
            if (bottom <= top) continue;
            foreach (var column in frame.Columns.Slots)
            {
                if (!fields.Contains(column.Field)) continue;
                if (result.Count == maximumCells) throw new InvalidOperationException($"Native cell limit ({maximumCells}) exceeded. Increase row height or reduce templated columns.");
                result.Add(new(new(row.Record!.Id, column.Field), new(column.X, bounds.Y, column.Width, bounds.Height),
                    new(column.ClipLeft, top, column.ClipRight - column.ClipLeft, bottom - top)));
            }
        }
        return result.AsReadOnly();
    }
}

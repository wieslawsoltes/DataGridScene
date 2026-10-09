using DataGridScene.Core;
using DataGridScene.Layout;

namespace DataGridScene.Rendering.Skia;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}
public sealed record GridMetrics
{
    public double RowHeight { get; init; } = 28;
    public double GroupPanelHeight { get; init; } = 38;
    public double BandHeight { get; init; } = 24;
    public double HeaderHeight { get; init; } = 34;
    public double FilterHeight { get; init; } = 28;
    public double FooterHeight { get; init; } = 30;
    public bool ShowGroupPanel { get; init; } = true;
    public bool ShowFilterRow { get; init; } = true;
    public bool ShowBands { get; init; }
}
public enum HitRegion { None, GroupPanel, GroupChip, GroupRemove, Band, Header, Filter, DataCell, Indicator, GroupRow, Detail, Summary }
public sealed record GridHit(HitRegion Region, int RowIndex = -1, string? Field = null, RectD Bounds = default, string? GroupKey = null);
public sealed record GroupChip(string Field, string Caption, RectD Bounds);

/// <summary>One shared geometry snapshot for painting, hit testing, editors and automation.</summary>
public sealed class GridFrame
{
    public GridView View { get; }
    public GridLayout Layout { get; }
    public GridSelection Selection { get; }
    public RowHeightIndex Heights { get; }
    public GridMetrics Metrics { get; }
    public ColumnViewport Columns { get; }
    public VisibleRange VisibleRows { get; }
    public IReadOnlyList<GroupChip> GroupChips { get; }
    public double Width { get; }
    public double Height { get; }
    public double OffsetY { get; }
    public double BandTop { get; }
    public double HeaderTop { get; }
    public double FilterTop { get; }
    public double BodyTop { get; }
    public double FooterTop { get; }
    public double BodyHeight => Math.Max(0, FooterTop - BodyTop);

    public GridFrame(GridView view, GridLayout layout, GridSelection selection, RowHeightIndex heights, GridMetrics metrics, double width, double height, double offsetX, double offsetY)
    {
        View = view; Layout = layout; Selection = selection; Heights = heights; Metrics = metrics;
        Width = Math.Max(0, width); Height = Math.Max(0, height);
        BandTop = metrics.ShowGroupPanel ? metrics.GroupPanelHeight : 0;
        HeaderTop = BandTop + (metrics.ShowBands ? metrics.BandHeight : 0);
        FilterTop = HeaderTop + metrics.HeaderHeight;
        BodyTop = FilterTop + (metrics.ShowFilterRow ? metrics.FilterHeight : 0);
        FooterTop = Math.Max(BodyTop, Height - metrics.FooterHeight);
        OffsetY = Math.Clamp(offsetY, 0, Math.Max(0, heights.TotalHeight - BodyHeight));
        Columns = layout.Compute(Width, offsetX);
        VisibleRows = heights.Visible(OffsetY, BodyHeight, 1);
        var chips = new List<GroupChip>(); double x = 12;
        foreach (var group in view.Query.Groups)
        {
            string caption = view.Table.Schema[group.Field].Caption;
            double chipWidth = Math.Clamp(caption.Length * 7 + 55, 100, 250);
            chips.Add(new(group.Field, caption, new(x, 6, chipWidth, 26))); x += chipWidth + 16;
        }
        GroupChips = chips.AsReadOnly();
    }
    public RectD RowBounds(int rowIndex) => new(0, BodyTop + Heights.OffsetOf(rowIndex) - OffsetY, Width, Heights[rowIndex]);
    public RectD CellBounds(int rowIndex, string field)
    {
        var slot = Columns.Slots.FirstOrDefault(column => column.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (slot is null || rowIndex < 0 || rowIndex >= Heights.Count) return default;
        var row = RowBounds(rowIndex);
        return new(slot.ClipLeft, row.Y, slot.ClipRight - slot.ClipLeft, row.Height);
    }
    public GridHit HitTest(double x, double y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return new(HitRegion.None);
        if (Metrics.ShowGroupPanel && y < BandTop)
        {
            foreach (var chip in GroupChips)
                if (chip.Bounds.Contains(x, y)) return new(x > chip.Bounds.Right - 24 ? HitRegion.GroupRemove : HitRegion.GroupChip, Field: chip.Field, Bounds: chip.Bounds);
            return new(HitRegion.GroupPanel);
        }
        var column = Columns.HitTest(x);
        if (y < HeaderTop) return new(HitRegion.Band, Field: column?.Field);
        if (y < FilterTop) return new(HitRegion.Header, Field: column?.Field, Bounds: column is null ? new(0, HeaderTop, GridLayout.IndicatorWidth, Metrics.HeaderHeight) : new(column.X, HeaderTop, column.Width, Metrics.HeaderHeight));
        if (y < BodyTop) return new(HitRegion.Filter, Field: column?.Field, Bounds: column is null ? default : new(column.ClipLeft, FilterTop, column.ClipRight - column.ClipLeft, Metrics.FilterHeight));
        if (y >= FooterTop) return new(HitRegion.Summary, Field: column?.Field);
        double dataY = y - BodyTop + OffsetY;
        if (dataY >= Heights.TotalHeight || Heights.Count == 0) return new(HitRegion.None);
        int index = Heights.RowAt(dataY); var row = View.Rows[index];
        if (row.Kind == GridRowKind.Group) return new(HitRegion.GroupRow, index, Bounds: RowBounds(index), GroupKey: row.Key);
        if (row.Kind == GridRowKind.Detail) return new(HitRegion.Detail, index, Bounds: RowBounds(index));
        if (x < GridLayout.IndicatorWidth) return new(HitRegion.Indicator, index, Bounds: new(0, RowBounds(index).Y, GridLayout.IndicatorWidth, Heights[index]));
        return column is null ? new(HitRegion.None) : new(HitRegion.DataCell, index, column.Field, CellBounds(index, column.Field));
    }
}

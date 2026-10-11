using DataGridScene.Core;
using DataGridScene.Layout;

namespace DataGridScene.Rendering.Skia;

/// <summary>Single-frame, UI-thread cache. Value-only edits and selection changes reuse
/// geometry; shape, size, scroll, row-height and column changes invalidate it.</summary>
public sealed class GridFrameCache
{
    private GridFrame? _frame;
    private long _rows, _heights;
    private int _layout;
    private double _x, _y;
    public long BuildCount { get; private set; }
    public long ReuseCount { get; private set; }
    public GridFrame Get(GridView view, GridLayout layout, GridSelection selection, RowHeightIndex heights,
        GridMetrics metrics, double width, double height, double offsetX, double offsetY)
    {
        if (_frame is { } previous && ReferenceEquals(previous.View, view) && ReferenceEquals(previous.Layout, layout) &&
            ReferenceEquals(previous.Selection, selection) && ReferenceEquals(previous.Heights, heights) &&
            previous.Metrics == metrics && previous.Width == width && previous.Height == height &&
            _rows == view.RowsRevision && _layout == layout.Revision && _heights == heights.Revision &&
            _x == offsetX && _y == offsetY)
        { ReuseCount++; return previous; }
        var next = new GridFrame(view, layout, selection, heights, metrics, width, height, offsetX, offsetY);
        _frame = next; _rows = view.RowsRevision; _layout = layout.Revision; _heights = heights.Revision;
        _x = offsetX; _y = offsetY; BuildCount++; return next;
    }
    public void Clear() => _frame = null;
}

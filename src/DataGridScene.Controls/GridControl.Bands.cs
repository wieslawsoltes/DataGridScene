using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private GridHit? _pressedBand;
    private double _bandWidth;
    private IReadOnlyDictionary<string, double>? _bandWeights;
    private int _bandColumnCount;
    private bool TryBandPressed(GridHit hit, Point point, PointerRoutedEventArgs args)
    {
        if (hit.Region != HitRegion.Band || hit.BandKey is null || ColumnLayout is null) return false;
        _previousCell = null; _previousTable = null; _validDoubleTap = false;
        _pressedBand = hit; _pressPoint = point;
        var fields = ColumnLayout.FieldsInBand(hit.BandKey).ToHashSet(StringComparer.Ordinal);
        var columns = ColumnLayout.Columns.Where(column => column.Visible && fields.Contains(column.Field)).ToArray();
        _bandWidth = columns.Sum(column => column.Width);
        _bandColumnCount = columns.Length;
        _bandWeights = columns.ToDictionary(column => column.Field, column => column.Width);
        _resizing = Math.Abs(point.X - hit.Bounds.Right) <= 6;
        _dragging = false; _surface.CapturePointer(args.Pointer);
        return true;
    }
    private bool TryBandMoved(Point point)
    {
        if (_pressedBand?.BandKey is not string key || ColumnLayout is null || Frame is null) return false;
        if (_resizing)
        {
            double width = Math.Clamp(_bandWidth + point.X - _pressPoint.X,
                _bandColumnCount * GridLayout.MinimumWidth, _bandColumnCount * GridLayout.MaximumWidth);
            Safe(() => ColumnLayout.SetBandWidth(key, width, _bandWeights));
        }
        else if (Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 6)
        {
            _dragging = true;
            var target = Frame.HitTest(point.X, point.Y);
            Renderer.DragMarkerX = target.Region == HitRegion.Band ? target.Bounds.X : null;
            Redraw();
        }
        return true;
    }
    private bool TryBandReleased(Point point, PointerRoutedEventArgs args)
    {
        if (_pressedBand?.BandKey is not string key || Frame is null) return false;
        bool moving = _dragging && !_resizing;
        _pressedBand = null; _bandWeights = null;
        _resizing = _dragging = false; Renderer.DragMarkerX = null;
        _surface.ReleasePointerCapture(args.Pointer);
        if (moving)
        {
            var target = Frame.HitTest(point.X, point.Y);
            if (target.BandKey is not null && target.BandKey != key)
                Safe(() => ColumnLayout!.MoveBandBefore(key, target.BandKey));
        }
        Redraw();
        return true;
    }
    private void BestFitBand(string key)
    {
        if (!CommitEdit()) return;
        var fields = ColumnLayout!.FieldsInBand(key).ToHashSet(StringComparer.Ordinal);
        var states = ColumnLayout.Columns.Select(state =>
        {
            if (!fields.Contains(state.Field)) return state;
            var column = Table!.Schema[state.Field];
            double width = Renderer.MeasureText(column.Caption, true) + 48;
            foreach (var record in View!.FilteredRecords)
                width = Math.Max(width, Renderer.MeasureText(column.Display(column.GetValue(record), Table.Schema.Culture)) + 24);
            return state with { Width = Math.Clamp(width, 50, 640) };
        }).ToArray();
        ColumnLayout.RestoreColumns(states);
    }
    private void ShowBandContext(string key, Point point)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Menu("Best fit band", () => BestFitBand(key)));
        menu.Items.Add(Menu("Freeze band left", () => ColumnLayout!.SetBandPin(key, ColumnPin.Left)));
        menu.Items.Add(Menu("Freeze band right", () => ColumnLayout!.SetBandPin(key, ColumnPin.Right)));
        menu.Items.Add(Menu("Unfreeze band", () => ColumnLayout!.SetBandPin(key, ColumnPin.None)));
        menu.Items.Add(Menu("Hide band", () => ColumnLayout!.SetBandVisible(key, false)));
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = point });
    }
}

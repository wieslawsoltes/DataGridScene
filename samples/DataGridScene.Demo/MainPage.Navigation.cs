using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using DataGridScene.Rendering.Skia;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    // Read-only diagnostics over the real navigation ScrollViewer. A child's layout
    // bounds may extend beyond its parent's viewport; those are not clickable pixels.
    public object NavigationSnapshot()
    {
        var origin = _navigation.TransformToVisual(this).TransformPoint(new Point());
        var scroller = _navigation.Child as ScrollViewer;
        return new
        {
            visible = _navigation.Visibility == Visibility.Visible && _navigation.ActualWidth > 0,
            bounds = new RectD(origin.X, origin.Y, _navigation.ActualWidth, _navigation.ActualHeight),
            verticalOffset = scroller?.VerticalOffset ?? 0,
            scrollableHeight = scroller?.ScrollableHeight ?? 0
        };
    }
}

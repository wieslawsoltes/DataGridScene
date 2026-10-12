using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using DataGridScene.Rendering.Skia;
using Windows.Foundation;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    // Read actual native template parts, their effective visibility and measured
    // geometry. Diagnostics never force a template state or modify scrolling.
    public object ScrollBarsSnapshot()
    {
        object Describe(ScrollBar bar)
        {
            RectD Bounds(FrameworkElement element)
            {
                var p = element.TransformToVisual(this).TransformPoint(new Point());
                return new(p.X, p.Y, element.ActualWidth, element.ActualHeight);
            }
            bool Visible(FrameworkElement element)
            {
                for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                    if (current is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity <= 0.01)) return false;
                return element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0;
            }
            var parts = new List<object>();
            void Visit(DependencyObject root)
            {
                if (root is FrameworkElement element && (element is Thumb || element is RepeatButton))
                    parts.Add(new { name = element.Name, kind = element is Thumb ? "thumb" : "button", visible = Visible(element), bounds = Bounds(element) });
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Visit(VisualTreeHelper.GetChild(root, i));
            }
            Visit(bar);
            return new { axis = bar.Orientation.ToString().ToLowerInvariant(), bar.Value, bar.Maximum, bar.ViewportSize,
                bar.SmallChange, bar.LargeChange, bar.IsEnabled, indicator = bar.IndicatorMode.ToString(), visible = Visible(bar), bounds = Bounds(bar), parts };
        }
        return new { vertical = Describe(DataGrid.VerticalScrollBar), horizontal = Describe(DataGrid.HorizontalScrollBar) };
    }
}

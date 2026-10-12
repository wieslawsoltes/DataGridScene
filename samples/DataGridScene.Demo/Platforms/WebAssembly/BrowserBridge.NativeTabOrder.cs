using DataGridScene.Controls;
using DataGridScene.Rendering.Skia;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateNativeTabOrder()
    {
        var results = NativeTabOrderValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(result => result.Milliseconds)), Json);
    }

    [JSExport]
    public static string SnapshotNativeTabOrder()
    {
        var grid = Page.DataGrid;
        var focused = grid.XamlRoot is null ? null : FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject;
        static bool Within(DependencyObject? child, DependencyObject parent)
        {
            for (; child is not null; child = VisualTreeHelper.GetParent(child))
                if (ReferenceEquals(child, parent)) return true;
            return false;
        }
        // Only inspect existing elements. This diagnostic never realizes, focuses,
        // rearranges or assigns TabIndex, even if a template has not loaded yet.
        var cells = grid.GetNativeCells().Select(info => {
            var element = grid.GetCellControl(info.Address.RecordId, info.Address.Field)!;
            var body = VisualTreeHelper.GetParent(element);
            var host = body is null ? null : VisualTreeHelper.GetParent(body) as UIElement;
            return new { address = info.Address, info.ControlType, info.Bounds, info.ClipBounds,
                tabIndex = host?.TabIndex, focused = Within(focused, element) };
        }).ToArray();
        return JsonSerializer.Serialize(new { cells, focusedCell = cells.FirstOrDefault(cell => cell.focused)?.address,
            focusedType = focused?.GetType().Name,
            focusedId = focused is null ? null : AutomationProperties.GetAutomationId(focused),
            writes = grid.CellControlTabIndexWriteCount,
            attached = grid.AttachedCellControlCount, created = grid.CreatedCellControlCount,
            attaches = grid.CellControlTreeAttachCount, detaches = grid.CellControlTreeDetachCount,
            binds = grid.CellControlBindCount, sizes = grid.CellControlSizeWriteCount,
            pooled = grid.PooledCellControlCount, realized = grid.RealizedCellControlCount,
            grid.HasPendingCellEdits, grid.LastError }, Json);
    }

    [JSExport]
    public static string ConfigureNativeTabTemplate(bool reverse, bool disableSecond)
    {
        try
        {
            Page.DataGrid.SetCellTemplate("Notes", new GridCellTemplate(() => {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var first = new Button { Content = "First", Width = 72, MinHeight = 0, Height = 28, Padding = new Thickness(2), TabIndex = reverse ? 20 : 10 };
                var second = new Button { Content = "Second", Width = 72, MinHeight = 0, Height = 28, Padding = new Thickness(2), TabIndex = reverse ? 10 : 20, IsEnabled = !disableSecond };
                AutomationProperties.SetAutomationId(first, "Composite-first");
                AutomationProperties.SetAutomationId(second, "Composite-second");
                panel.Children.Add(first); panel.Children.Add(second); return panel;
            }));
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
}

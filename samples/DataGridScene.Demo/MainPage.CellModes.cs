using DataGridScene.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private readonly CheckBox _nativeModeToggle = new() { Content = "Native cells", FontSize = 12,
        MinHeight = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private void InitializeNativeModeUI()
    {
        // The same real sample/data/query is used in both modes, with no duplicate
        // sample implementation to drift. Explicit native/adaptive templates still win.
        if (VisualTreeHelper.GetParent(_search) is StackPanel panel) panel.Children.Insert(0, _nativeModeToggle);
        _actions["cell-mode"] = _nativeModeToggle;
        AutomationProperties.SetName(_nativeModeToggle, "Use native cell controls in every sample");
        ToolTipService.SetToolTip(_nativeModeToggle, "Switch cell content without resetting data, selection, layout, filters or undo history");
        _nativeModeToggle.Click += (_, _) =>
        {
            Run(() => DataGrid.CellRenderingMode = _nativeModeToggle.IsChecked == true
                ? GridCellRenderingMode.NativeControls : GridCellRenderingMode.Skia);
            _nativeModeToggle.IsChecked = DataGrid.CellRenderingMode == GridCellRenderingMode.NativeControls;
        };
    }
    public object NativeScrollingSnapshot() => new
    {
        mode = DataGrid.CellRenderingMode.ToString(),
        focusedType = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot)?.GetType().Name,
        focusedControl = XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement element
            ? AutomationProperties.GetAutomationId(element) : null,
        DataGrid.AttachedCellControlCount, DataGrid.CellControlTreeAttachCount, DataGrid.CellControlTreeDetachCount,
        DataGrid.CellControlSizeWriteCount, DataGrid.CellControlTranslationWriteCount,
        toggle = _nativeModeToggle.IsChecked
    };
}

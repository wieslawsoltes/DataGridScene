using DataGridScene.Controls;
using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private void InitializeNativeCellUI()
    {
        InitializeNativeModeUI();
        if (_navigation.Child is not ScrollViewer { Content: StackPanel navigation }) return;
        var button = Button("scenario-Native controls", "Native controls", ApplyNativeCellScenario);
        button.Height = button.MinHeight = button.MaxHeight = 40;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        navigation.Children.Insert(navigation.Children.IndexOf(_actions["scenario-Performance"]), button);
        var adaptive = Button("adaptive-templates", "Adaptive cell editors", () =>
        {
            if (Scenario != "Native controls") ApplyNativeCellScenario();
            SetAdaptiveCellTemplates(!_adaptiveTemplates);
        });
        navigation.Children.Insert(navigation.Children.IndexOf(button) + 1, adaptive);
    }
    private void ApplyNativeCellScenario() => ApplyNativeCellScenario(10000);
    public void ApplyNativeCellScenario(int count)
    {
        if (count < 1 || count > 100000) throw new ArgumentOutOfRangeException(nameof(count));
        if (!DataGrid.CommitEdit()) return;
        _adaptiveTemplates = false; _templateFault = false;
        Reload(count); Scenario = "Native controls"; DataGrid.RowHeight = 36; DataGrid.ShowBands = false;
        var visible = new HashSet<string> { "OrderID", "Company", "Quantity", "OrderDate", "Progress", "Status", "Verified", "Notes" };
        using (DataGrid.BeginDataUpdate())
        {
            foreach (var column in DataGrid.Table!.Schema.Columns)
                DataGrid.ColumnLayout!.SetVisible(column.Field, visible.Contains(column.Field));
            DataGrid.ColumnLayout!.SetPin("Status", DataGridScene.Layout.ColumnPin.None);
            DataGrid.ColumnLayout.SetWidth("Quantity", 118); DataGrid.ColumnLayout.SetWidth("OrderDate", 166);
            DataGrid.ColumnLayout.SetWidth("Progress", 145); DataGrid.ColumnLayout.SetWidth("Notes", 200);
            DataGrid.SetCellTemplate("Company", GridCellTemplates.TextBox());
            DataGrid.SetCellTemplate("Quantity", GridCellTemplates.NumberBox(1, 100000));
            DataGrid.SetCellTemplate("Status", GridCellTemplates.ComboBox(OrderData.Statuses));
            DataGrid.SetCellTemplate("Verified", GridCellTemplates.CheckBox());
            DataGrid.SetCellTemplate("Progress", GridCellTemplates.ProgressBar());
            DataGrid.SetCellEditTemplate("Progress", GridCellTemplates.Slider());
            DataGrid.SetCellEditTemplate("OrderDate", GridCellTemplates.CalendarDatePicker());
            // An ordinary composite DataTemplate with inherited DataContext and native
            // binding. The action reads the current buffer, not the row at creation time.
            DataGrid.SetCellDataTemplate("Notes", new DataTemplate(() =>
            {
                var panel = new Grid { ColumnSpacing = 5, Padding = new Thickness(4, 1, 4, 1) };
                panel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var caption = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                caption.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath("Value") }); panel.Children.Add(caption);
                var open = new Button { Content = "Open", FontSize = 11, Padding = new Thickness(6, 1, 6, 1), MinHeight = 26, MinWidth = 42, CornerRadius = new CornerRadius(1) };
                open.Click += (_, _) => { if (open.DataContext is GridCellEditBuffer buffer) buffer.Invoke(cell => SetMessage($"Opened native action for order {cell.RecordId}")); };
                Grid.SetColumn(open, 1); panel.Children.Add(open); return panel;
            }));
        }
        RefreshStatus();
    }
    private bool _adaptiveTemplates, _templateFault;
    public bool AdaptiveTemplatesEnabled => Scenario == "Native controls" && _adaptiveTemplates;
    public void SetAdaptiveCellTemplates(bool enabled)
    {
        if (!DataGrid.CommitEdit()) return;
        _adaptiveTemplates = enabled;
        if (!enabled)
        {
            DataGrid.SetCellTemplateSelector("Progress", null);
            DataGrid.SetCellEditTemplateSelector("OrderDate", null);
            return;
        }
        var slider = GridCellTemplates.Slider();
        var text = GridCellTemplates.TextBox();
        int progress = DataGrid.Table!.Schema["Progress"].Index;
        // Stable template instances and read-only contexts; selectors never create UI
        // or change records. Completed rows retain the standard progress display.
        DataGrid.SetCellTemplateSelector("Progress", context =>
        {
            if (_templateFault) throw new InvalidOperationException("Template selector rejected the row.");
            return Convert.ToDouble(context.Record[progress]) < 100 && !context.IsReadOnly ? slider : null;
        });
        DataGrid.SetCellEditTemplateSelector("OrderDate", context =>
            Convert.ToDouble(context.Record[progress]) >= 100 ? text : null);
    }
    public void SetTemplateSelectorFault(bool enabled)
    { _templateFault = enabled; DataGrid.InvalidateCellTemplates(); }
    // Read-only diagnostics over the actual popup visual tree. Tests click realized
    // native day elements instead of assuming the popup initially focuses a date.
    public object NativeCalendarSnapshot()
    {
        var days = new List<object>();
        var popups = XamlRoot is null ? [] : VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);
        foreach (var popup in popups)
        {
            if (!popup.IsOpen || popup.Child is not FrameworkElement root) continue;
            var origin = root.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point());
            var pending = new Stack<DependencyObject>(); pending.Push(root); int visited = 0;
            while (pending.TryPop(out var element) && ++visited <= 4096)
            {
                if (element is FrameworkElement { Visibility: Visibility.Collapsed }) continue;
                if (element is CalendarViewDayItem { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } day)
                {
                    var point = day.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point());
                    if (point.X >= origin.X && point.Y >= origin.Y &&
                        point.X + day.ActualWidth <= origin.X + root.ActualWidth + 1 &&
                        point.Y + day.ActualHeight <= origin.Y + root.ActualHeight + 1)
                        days.Add(new { date = day.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                            bounds = new DataGridScene.Rendering.Skia.RectD(point.X, point.Y, day.ActualWidth, day.ActualHeight) });
                }
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                    pending.Push(VisualTreeHelper.GetChild(element, i));
            }
        }
        return new { days };
    }

}

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
        if (_navigation.Child is not ScrollViewer { Content: StackPanel navigation }) return;
        var button = Button("scenario-Native controls", "Native controls", ApplyNativeCellScenario);
        button.Height = button.MinHeight = button.MaxHeight = 40;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        navigation.Children.Insert(navigation.Children.IndexOf(_actions["scenario-Performance"]), button);
    }
    private void ApplyNativeCellScenario() => ApplyNativeCellScenario(10000);
    public void ApplyNativeCellScenario(int count)
    {
        if (count < 1 || count > 100000) throw new ArgumentOutOfRangeException(nameof(count));
        if (!DataGrid.CommitEdit()) return;
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
}

using DataGridScene.Core;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Controls;

/// <summary>Reusable column visibility, pinning and width editor.</summary>
public sealed class ColumnChooser : UserControl
{
    private readonly StackPanel _items = new() { Spacing = 6 };
    private readonly TextBox _search = new() { PlaceholderText = "Find a column…" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red) };
    private readonly GridLayout _layout;
    private readonly GridSchema _schema;
    public ColumnChooser(GridLayout layout, GridSchema schema)
    {
        _layout = layout; _schema = schema; MinWidth = 360;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(_search); content.Children.Add(new TextBlock { Text = "Column visibility             Freeze              Width", FontSize = 12 });
        content.Children.Add(new ScrollViewer { Content = _items, MaxHeight = 450, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); content.Children.Add(_error);
        Content = content; _search.TextChanged += (_, _) => Rebuild(); Rebuild();
    }
    private void Run(Action action) { try { action(); _error.Text = ""; } catch (Exception error) { _error.Text = error.Message; } }
    private void Rebuild()
    {
        _items.Children.Clear();
        foreach (var column in _layout.Columns.OrderBy(column => column.Order).Where(column => _schema[column.Field].Caption.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(90) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(68) });
            var check = new CheckBox { Content = _schema[column.Field].Caption, IsChecked = column.Visible, MinWidth = 0 };
            check.Click += (_, _) => Run(() => { _layout.SetVisible(column.Field, check.IsChecked == true); }); row.Children.Add(check);
            var pin = new ComboBox { ItemsSource = Enum.GetValues<ColumnPin>(), SelectedItem = column.Pin, MinWidth = 80 };
            pin.SelectionChanged += (_, _) => { if (pin.SelectedItem is ColumnPin value) Run(() => _layout.SetPin(column.Field, value)); };
            Grid.SetColumn(pin, 1); row.Children.Add(pin);
            var width = new TextBox { Text = Math.Round(column.Width).ToString(System.Globalization.CultureInfo.InvariantCulture), MinWidth = 0 };
            width.LostFocus += (_, _) => Run(() => _layout.SetWidth(column.Field, double.Parse(width.Text, System.Globalization.CultureInfo.InvariantCulture)));
            Grid.SetColumn(width, 2); row.Children.Add(width); _items.Children.Add(row);
        }
    }
}

using DataGridScene.Controls;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using Windows.Foundation;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private readonly Dictionary<string, FrameworkElement> _customRowActions = new();
    private long _rowFactoryCalls, _rowEditorReleases;
    private bool _rowReaderFault;
    public void ResetCustomRowEditorDiagnostics()
    {
        if (DataGrid.ActiveRowEditForm is not null) throw new InvalidOperationException("Close the form first.");
        _customRowActions.Clear(); _rowFactoryCalls = 0; _rowEditorReleases = 0; _rowReaderFault = false;
    }
    public void SetCustomRowReaderFault(bool value) => _rowReaderFault = value;
    private RowEditFormOptions CreateCustomRowEditorOptions()
    {
        var factories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
            ["Company"] = context => {
                if (context.DataType != typeof(string)) return null;
                _rowFactoryCalls++;
                string initial = Convert.ToString(context.InitialValue, context.Culture) ?? "";
                var template = new DataTemplate(() => new TextBox { Text = initial, FontSize = 12, MinWidth = 0, MinHeight = 30,
                    Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(1), PlaceholderText = "Customer name" });
                return RowEditEditors.FromTemplate(template, context,
                    root => ((TextBox)root).Text == initial && context.InitialValue is not null ? context.InitialValue : ((TextBox)root).Text,
                    (root, readOnly) => ((TextBox)root).IsReadOnly = readOnly,
                    _ => _rowEditorReleases++);
            },
            ["Quantity"] = context => {
                if (context.DataType != typeof(int)) return null;
                _rowFactoryCalls++;
                string initial = Convert.ToString(context.InitialValue, context.Culture) ?? "";
                var input = new TextBox { Text = initial, FontSize = 12, MinWidth = 0, MinHeight = 30,
                    Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(1) };
                var less = new Button { Content = "−", MinWidth = 28, Width = 28, MinHeight = 30, Padding = new Thickness(0), FontSize = 16 };
                var more = new Button { Content = "+", MinWidth = 28, Width = 28, MinHeight = 30, Padding = new Thickness(0), FontSize = 16 };
                AutomationProperties.SetName(input, context.Caption); AutomationProperties.SetAutomationId(input, "row-edit-Quantity-input");
                AutomationProperties.SetName(less, "Decrease quantity"); AutomationProperties.SetName(more, "Increase quantity");
                var root = new Grid { ColumnSpacing = 2 };
                root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                root.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                root.Children.Add(input); Grid.SetColumn(less, 1); root.Children.Add(less); Grid.SetColumn(more, 2); root.Children.Add(more);
                void Step(int delta)
                {
                    // Native buttons change only pending text, never the row or its undo history.
                    if (!input.IsReadOnly && int.TryParse(input.Text, NumberStyles.Integer, context.Culture, out int value) &&
                        (delta > 0 ? value < int.MaxValue : value > int.MinValue)) input.Text = (value + delta).ToString(context.Culture);
                }
                RoutedEventHandler decrement = (_, _) => Step(-1), increment = (_, _) => Step(1);
                less.Click += decrement; more.Click += increment;
                _customRowActions["quantity-input"] = input; _customRowActions["quantity-less"] = less; _customRowActions["quantity-more"] = more;
                return new RowEditEditor(root, () => {
                    if (_rowReaderFault) throw new FormatException("Quantity editor could not capture its pending input.");
                    return input.Text == initial && context.InitialValue is not null ? context.InitialValue : input.Text;
                }, readOnly => { input.IsReadOnly = readOnly; less.IsEnabled = more.IsEnabled = !readOnly; },
                () => {
                    less.Click -= decrement; more.Click -= increment; _rowEditorReleases++;
                    foreach (string key in new[] { "quantity-input", "quantity-less", "quantity-more" }) _customRowActions.Remove(key);
                }, input);
            },
            ["Notes"] = context => {
                if (context.DataType != typeof(string)) return null;
                _rowFactoryCalls++;
                return RowEditEditors.Text(context, multiline: true, minimumHeight: 88);
            }
        };
        return CreateRowLayoutOptions() with { Title = "Edit order", ShowInstructions = false,
            EditorFactories = factories.Where(p => DataGrid.Table!.Schema.TryGetColumn(p.Key, out _)).ToDictionary(p => p.Key, p => p.Value) };
    }
    public object CustomRowEditorSnapshot()
    {
        var form = DataGrid.ActiveRowEditForm;
        var bounds = new Dictionary<string, RectD>();
        foreach (var (name, element) in _customRowActions)
        {
            if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var point = element.TransformToVisual(this).TransformPoint(new Point());
            bounds[name] = new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        return new {
            factoryCalls = _rowFactoryCalls, releases = _rowEditorReleases, readerFault = _rowReaderFault,
            actions = bounds, quantityText = (_customRowActions.GetValueOrDefault("quantity-input") as TextBox)?.Text,
            quantityReadOnly = (_customRowActions.GetValueOrDefault("quantity-input") as TextBox)?.IsReadOnly,
            incrementEnabled = (_customRowActions.GetValueOrDefault("quantity-more") as Button)?.IsEnabled,
            creations = form?.EditorCreationCount, captures = form?.ReaderCaptureCount, readOnlyUpdates = form?.ReadOnlyUpdateCount,
            errors = form?.ValidationErrors, multiline = (form?.Editors.GetValueOrDefault("Notes") as TextBox)?.AcceptsReturn,
            stagedName = form?.Buffer.IsActive == true ? form.Buffer.GetValue("Company") : null
        };
    }
}

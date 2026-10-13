using DataGridScene.Core;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DataGridScene.Controls;

/// <summary>Reusable cached row form. Editors never post individual cell changes.
/// TryCommit flushes actual control values and validates/applies the whole row once.
/// Resizing changes only field placement, never controls, bindings or pending input.</summary>
public sealed class RowEditForm : UserControl, IDisposable
{
    private readonly Dictionary<string, FrameworkElement> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _nulls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<object?>> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _fieldPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Grid _fields = new() { RowSpacing = 12, Padding = new Thickness(0, 0, 12, 4) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private bool _disposed;
    public GridRowEditBuffer Buffer { get; }
    public RowEditLayout LayoutDefinition { get; }
    public RowEditLayoutPlan LayoutPlan { get; private set; } = null!;
    public long LayoutReflowCount { get; private set; }
    public long FieldPlacementWriteCount { get; private set; }
    public IReadOnlyDictionary<string, FrameworkElement> FieldPanels { get; }
    public IReadOnlyDictionary<string, FrameworkElement> Editors { get; }
    public IReadOnlyDictionary<string, CheckBox> NullToggles { get; }
    public ScrollViewer FieldsViewport { get; }
    public string? ValidationMessage => Buffer.Error;
    public RowEditForm(GridRowEditBuffer buffer) : this(buffer, null) { }

    public RowEditForm(GridRowEditBuffer buffer, RowEditFormOptions? options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!buffer.IsActive) throw new ObjectDisposedException(nameof(buffer));
        options ??= new(); options.Validate();
        // Validate and own all metadata before constructing even the first editor.
        LayoutDefinition = new(buffer.Columns.Select(c => c.Field), options.Fields,
            options.MaximumColumnCount, options.MinimumColumnWidth, options.ColumnSpacing);
        Buffer = buffer; IsTabStop = false; TabFocusNavigation = KeyboardNavigationMode.Local;
        MinWidth = 240; MaxWidth = options.MaximumWidth; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Editors = new ReadOnlyDictionary<string, FrameworkElement>(_editors);
        NullToggles = new ReadOnlyDictionary<string, CheckBox>(_nulls);
        _fields.ColumnSpacing = options.ColumnSpacing;
        var columns = buffer.Columns.ToDictionary(c => c.Field, StringComparer.OrdinalIgnoreCase);
        foreach (var layout in LayoutDefinition.Fields.Where(f => f.IsVisible))
        {
            var column = columns[layout.Field];
            string caption = layout.Caption ?? column.Caption;
            object? bound = buffer.GetValue(column.Field);
            FrameworkElement control; Func<object?> read;
            if (column.DataType == typeof(bool) && bound is null or bool)
            {
                var check = new CheckBox { IsThreeState = column.AllowNull, IsChecked = bound as bool?, MinHeight = 32 };
                control = check; read = () => check.IsChecked;
            }
            else if (column.DataType == typeof(string) && column.Choices.Count > 0 && bound is null or string)
            {
                var items = column.Choices.ToList();
                if (bound is string current && !items.Contains(current)) items.Add(current);
                var choice = new ComboBox { ItemsSource = items, SelectedItem = bound, MinWidth = 0, MinHeight = 32,
                    HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
                control = choice; read = () => choice.SelectedItem;
            }
            else
            {
                string initial = Format(bound, buffer.Culture);
                var text = new TextBox { Text = initial, MinWidth = 0, MinHeight = 32, FontSize = 12,
                    Padding = new Thickness(7, 4, 7, 4), IsReadOnly = column.IsReadOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                control = text;
                read = () => text.Text == initial && bound is not null ? bound : text.Text;
            }
            AutomationProperties.SetName(control, caption);
            AutomationProperties.SetAutomationId(control, "row-edit-" + column.Field);
            AutomationProperties.SetHelpText(control, column.IsReadOnly ? "Read-only field" : column.AllowNull ? "Optional field" : "Required field");
            _editors.Add(column.Field, control);
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.Children.Add(control);
            if (column.AllowNull && control is not CheckBox && !column.IsReadOnly)
            {
                var nullCheck = new CheckBox { Content = "Null", IsChecked = bound is null, FontSize = 11, MinHeight = 30 };
                AutomationProperties.SetName(nullCheck, caption + " is null");
                AutomationProperties.SetAutomationId(nullCheck, "row-null-" + column.Field);
                _nulls.Add(column.Field, nullCheck); Grid.SetColumn(nullCheck, 1); line.Children.Add(nullCheck);
                var valueReader = read; read = () => nullCheck.IsChecked == true ? null : valueReader();
                nullCheck.Click += (_, _) => RefreshReadOnly();
            }
            _readers.Add(column.Field, read);
            var label = new TextBlock { Text = caption + (column.AllowNull ? "" : " *"), FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var field = new Grid { RowSpacing = 3, ColumnSpacing = 8, MinHeight = layout.RowSpan * 36 + (layout.RowSpan - 1) * 12 };
            if (options.CaptionPlacement == RowEditCaptionPlacement.Beside)
            {
                field.ColumnDefinitions.Add(new() { Width = new GridLength(options.CaptionWidth) });
                field.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                field.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(line, 1); Grid.SetRow(error, 1); Grid.SetColumn(error, 1);
            }
            else
            {
                field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                field.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetRow(line, 1); Grid.SetRow(error, 2);
            }
            field.Children.Add(label); field.Children.Add(line); field.Children.Add(error);
            _errors.Add(column.Field, error); _fieldPanels.Add(column.Field, field); _fields.Children.Add(field);
        }
        FieldPanels = new ReadOnlyDictionary<string, FrameworkElement>(_fieldPanels.ToDictionary(p => p.Key, p => (FrameworkElement)p.Value, StringComparer.OrdinalIgnoreCase));
        FieldsViewport = new ScrollViewer { Content = _fields, MaxHeight = options.MaximumFieldHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Changes are saved together. Update commits the row; Cancel discards the form.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(FieldsViewport); body.Children.Add(_message); Content = body;
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _fields.SizeChanged += (_, args) => ArrangeFields(args.NewSize.Width);
        ArrangeFields(options.MaximumWidth); RefreshReadOnly();
    }
    private void ArrangeFields(double width)
    {
        if (_disposed) return;
        width = Math.Max(0, width - _fields.Padding.Left - _fields.Padding.Right);
        int columns = LayoutDefinition.GetColumnCount(width);
        if (LayoutPlan is not null && LayoutPlan.ColumnCount == columns) return;
        var plan = LayoutDefinition.Arrange(width);
        _fields.ColumnDefinitions.Clear(); _fields.RowDefinitions.Clear();
        for (int i = 0; i < plan.ColumnCount; i++) _fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < plan.RowCount; i++) _fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        foreach (var item in plan.Fields)
        {
            var element = _fieldPanels[item.Field];
            if (Grid.GetRow(element) != item.Row) { Grid.SetRow(element, item.Row); FieldPlacementWriteCount++; }
            if (Grid.GetColumn(element) != item.Column) { Grid.SetColumn(element, item.Column); FieldPlacementWriteCount++; }
            if (Grid.GetRowSpan(element) != item.RowSpan) { Grid.SetRowSpan(element, item.RowSpan); FieldPlacementWriteCount++; }
            if (Grid.GetColumnSpan(element) != item.ColumnSpan) { Grid.SetColumnSpan(element, item.ColumnSpan); FieldPlacementWriteCount++; }
        }
        LayoutPlan = plan; LayoutReflowCount++;
    }
    private static string Format(object? value, CultureInfo culture) => value switch
    {
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, culture) ?? ""
    };
    public void RefreshReadOnly()
    {
        if (_disposed) return;
        foreach (var column in Buffer.Columns)
        {
            if (!_editors.TryGetValue(column.Field, out var element)) continue;
            bool enabled = Buffer.CanEdit && !column.IsReadOnly;
            if (_nulls.TryGetValue(column.Field, out var nullCheck))
            { nullCheck.IsEnabled = enabled; enabled &= nullCheck.IsChecked != true; }
            if (element is TextBox text) { if (text.IsReadOnly == enabled) text.IsReadOnly = !enabled; }
            else if (element is Control control && control.IsEnabled != enabled) control.IsEnabled = enabled;
        }
    }
    public bool TryCommit()
    {
        if (_disposed) return false;
        if (Buffer.CanEdit)
            foreach (var column in Buffer.Columns.Where(c => !c.IsReadOnly && _readers.ContainsKey(c.Field)))
                Buffer.SetValue(column.Field, _readers[column.Field]());
        bool saved = Buffer.TryCommit();
        _message.Text = Buffer.Error ?? ""; _message.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (field, element) in _errors)
        {
            element.Text = string.Join("\n", Buffer.Errors.Where(e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase)).Select(e => e.Message));
            element.Visibility = element.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!saved && Buffer.Errors.FirstOrDefault(e => e.Field is not null)?.Field is string invalid &&
            _editors.TryGetValue(invalid, out var editor) && editor is Control control) control.Focus(FocusState.Programmatic);
        return saved;
    }
    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Buffer.Dispose(); _readers.Clear();
    }
}

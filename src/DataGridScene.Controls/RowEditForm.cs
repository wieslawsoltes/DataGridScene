using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DataGridScene.Controls;

/// <summary>Reusable cached row form. Editors never post individual cell changes.
/// TryCommit flushes actual control values and validates/applies the whole row once.
/// Disposing the form cancels an uncommitted buffer; it never commits on focus loss.</summary>
public sealed class RowEditForm : UserControl, IDisposable
{
    private readonly Dictionary<string, FrameworkElement> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _nulls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<object?>> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private bool _disposed;
    public GridRowEditBuffer Buffer { get; }
    public IReadOnlyDictionary<string, FrameworkElement> Editors { get; }
    public IReadOnlyDictionary<string, CheckBox> NullToggles { get; }
    public ScrollViewer FieldsViewport { get; }
    public string? ValidationMessage => Buffer.Error;

    public RowEditForm(GridRowEditBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!buffer.IsActive) throw new ObjectDisposedException(nameof(buffer));
        if (buffer.Columns.Count > 256) throw new ArgumentException("Choose at most 256 fields for a row form.", nameof(buffer));
        Buffer = buffer; IsTabStop = false; MinWidth = 240; MaxWidth = 520;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Editors = new ReadOnlyDictionary<string, FrameworkElement>(_editors);
        NullToggles = new ReadOnlyDictionary<string, CheckBox>(_nulls);
        var fields = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 12, 4) };
        foreach (var column in buffer.Columns)
        {
            object? original = buffer.GetOriginalValue(column.Field);
            FrameworkElement control;
            Func<object?> read;
            if (column.DataType == typeof(bool))
            {
                var check = new CheckBox { IsThreeState = column.AllowNull, IsChecked = original as bool?, MinHeight = 32 };
                control = check; read = () => check.IsChecked;
            }
            else if (column.DataType == typeof(string) && column.Choices.Count > 0)
            {
                var items = column.Choices.ToList();
                if (original is string current && !items.Contains(current)) items.Add(current);
                var choice = new ComboBox { ItemsSource = items, SelectedItem = original, MinWidth = 0, MinHeight = 32,
                    HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
                control = choice; read = () => choice.SelectedItem;
            }
            else
            {
                string initial = Format(original, buffer.Culture);
                var text = new TextBox { Text = initial, MinWidth = 0, MinHeight = 32, FontSize = 12,
                    Padding = new Thickness(7, 4, 7, 4), IsReadOnly = column.IsReadOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch };
                control = text;
                // Do not parse a display string unless it changed. This preserves
                // exact decimals/Int64, null versus empty, and subsecond date offsets.
                // An explicitly unchecked Null control permits entering an empty string.
                read = () => text.Text == initial && original is not null ? original : text.Text;
            }
            AutomationProperties.SetName(control, column.Caption);
            AutomationProperties.SetAutomationId(control, "row-edit-" + column.Field);
            AutomationProperties.SetHelpText(control, column.IsReadOnly ? "Read-only field" : column.AllowNull ? "Optional field" : "Required field");
            _editors.Add(column.Field, control);
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(control);
            if (column.AllowNull && column.DataType != typeof(bool) && !column.IsReadOnly)
            {
                var nullCheck = new CheckBox { Content = "Null", IsChecked = original is null, FontSize = 11, MinHeight = 30 };
                AutomationProperties.SetName(nullCheck, column.Caption + " is null");
                AutomationProperties.SetAutomationId(nullCheck, "row-null-" + column.Field);
                _nulls.Add(column.Field, nullCheck); Grid.SetColumn(nullCheck, 1); line.Children.Add(nullCheck);
                var valueReader = read; read = () => nullCheck.IsChecked == true ? null : valueReader();
                nullCheck.Click += (_, _) => RefreshReadOnly();
            }
            _readers.Add(column.Field, read);
            var field = new StackPanel { Spacing = 3 };
            field.Children.Add(new TextBlock { Text = column.Caption + (column.AllowNull ? "" : " *"), FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            field.Children.Add(line);
            var error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            _errors.Add(column.Field, error); field.Children.Add(error); fields.Children.Add(field);
        }
        FieldsViewport = new ScrollViewer { Content = fields, MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Changes are saved together. Update commits the row; Cancel discards the form.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(FieldsViewport); body.Children.Add(_message); Content = body;
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        RefreshReadOnly();
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
            bool enabled = Buffer.CanEdit && !column.IsReadOnly;
            if (_nulls.TryGetValue(column.Field, out var nullCheck))
            { nullCheck.IsEnabled = enabled; enabled &= nullCheck.IsChecked != true; }
            var element = _editors[column.Field];
            if (element is TextBox text) text.IsReadOnly = !enabled;
            else if (element is Control control) control.IsEnabled = enabled;
        }
    }
    public bool TryCommit()
    {
        if (_disposed) return false;
        if (Buffer.CanEdit)
            foreach (var column in Buffer.Columns.Where(column => !column.IsReadOnly))
                Buffer.SetValue(column.Field, _readers[column.Field]());
        bool saved = Buffer.TryCommit();
        _message.Text = Buffer.Error ?? ""; _message.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (field, element) in _errors)
        {
            element.Text = string.Join("\n", Buffer.Errors.Where(error => string.Equals(error.Field, field, StringComparison.OrdinalIgnoreCase)).Select(error => error.Message));
            element.Visibility = element.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!saved && Buffer.Errors.FirstOrDefault(error => error.Field is not null)?.Field is string invalid &&
            _editors.TryGetValue(invalid, out var editor) && editor is Control control) control.Focus(FocusState.Programmatic);
        return saved;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Buffer.Dispose(); _readers.Clear();
    }
}

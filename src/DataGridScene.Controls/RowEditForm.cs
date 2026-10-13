using DataGridScene.Core;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;

namespace DataGridScene.Controls;

/// <summary>Reusable cached row form. Editors never post individual cell changes.
/// Update captures every pending editor value before atomically staging the batch.
/// Resizing changes only field placement, never editors, bindings or pending input.</summary>
public sealed class RowEditForm : UserControl, IDisposable
{
    private readonly Dictionary<string, FrameworkElement> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RowEditEditor> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RowEditEditor> _ownedBindings = [];
    private readonly Dictionary<string, CheckBox> _nulls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _errorBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Grid> _fieldPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Grid _fields = new() { RowSpacing = 12, Padding = new Thickness(0, 0, 12, 4) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Border _validationSurface = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(9, 7, 9, 7), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly SolidColorBrush _errorBrush = new(), _errorBackground = new(), _transparent = new(Microsoft.UI.Colors.Transparent);
    private readonly List<string> _cleanupErrors = [];
    private IReadOnlyList<GridValidationError> _permissionErrors = Array.Empty<GridValidationError>();
    private bool _disposed, _capturing, _refreshing;
    public GridRowEditBuffer Buffer { get; }
    public RowEditLayout LayoutDefinition { get; }
    public RowEditLayoutPlan LayoutPlan { get; private set; } = null!;
    public long LayoutReflowCount { get; private set; }
    public long FieldPlacementWriteCount { get; private set; }
    public long EditorCreationCount { get; private set; }
    public long ReaderCaptureCount { get; private set; }
    public long ReadOnlyUpdateCount { get; private set; }
    public IReadOnlyDictionary<string, FrameworkElement> FieldPanels { get; }
    public IReadOnlyDictionary<string, FrameworkElement> Editors { get; }
    public IReadOnlyDictionary<string, CheckBox> NullToggles { get; }
    public IReadOnlyList<string> CleanupErrors => _cleanupErrors.AsReadOnly();
    public ScrollViewer FieldsViewport { get; }
    public string? ValidationMessage { get; private set; }
    public IReadOnlyList<GridValidationError> ValidationErrors { get; private set; } = Array.Empty<GridValidationError>();
    public RowEditForm(GridRowEditBuffer buffer) : this(buffer, null) { }

    public RowEditForm(GridRowEditBuffer buffer, RowEditFormOptions? options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!buffer.IsActive) throw new ObjectDisposedException(nameof(buffer));
        options ??= new(); options.Validate();
        LayoutDefinition = new(buffer.Columns.Select(c => c.Field), options.Fields,
            options.MaximumColumnCount, options.MinimumColumnWidth, options.ColumnSpacing);
        var columns = buffer.Columns.ToDictionary(c => c.Field, StringComparer.OrdinalIgnoreCase);
        var factories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, factory) in options.EditorFactories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field); ArgumentNullException.ThrowIfNull(factory);
            if (!columns.ContainsKey(field)) throw new ArgumentException($"Unknown row editor field: {field}", nameof(options));
            if (!factories.TryAdd(field, factory)) throw new ArgumentException($"Duplicate row editor factory: {field}", nameof(options));
        }
        Buffer = buffer; IsTabStop = false; TabFocusNavigation = KeyboardNavigationMode.Local;
        MinWidth = 240; MaxWidth = options.MaximumWidth; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Editors = new ReadOnlyDictionary<string, FrameworkElement>(_editors);
        NullToggles = new ReadOnlyDictionary<string, CheckBox>(_nulls);
        _fields.ColumnSpacing = options.ColumnSpacing;
        bool editable = buffer.CanEdit;
        try
        {
            foreach (var layout in LayoutDefinition.Fields.Where(f => f.IsVisible))
            {
                var column = columns[layout.Field]; string caption = layout.Caption ?? column.Caption;
                var context = new RowEditEditorContext(buffer.RecordId, column.Field, caption, column.DataType,
                    column.AllowNull, !editable || column.IsReadOnly, buffer.GetValue(column.Field), buffer.Culture,
                    Array.AsReadOnly(column.Choices.ToArray()));
                var binding = factories.TryGetValue(column.Field, out var factory) ? factory(context) : null;
                binding ??= RowEditEditors.Default(context);
                // Claim before owning: a rejected element already belonging to another
                // form must never have that other form's cleanup callback invoked here.
                binding.Claim(); _ownedBindings.Add(binding); _bindings.Add(column.Field, binding); EditorCreationCount++;
                var control = binding.Element;
                AutomationProperties.SetName(control, caption);
                AutomationProperties.SetAutomationId(control, "row-edit-" + column.Field);
                AutomationProperties.SetHelpText(control, column.IsReadOnly ? "Read-only field" : column.AllowNull ? "Optional field" : "Required field");
                _editors.Add(column.Field, control);
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); line.Children.Add(control);
                if (column.AllowNull && binding.UseNullToggle && !column.IsReadOnly)
                {
                    var nullCheck = new CheckBox { Content = "Null", IsChecked = context.InitialValue is null, FontSize = 11, MinHeight = 30 };
                    AutomationProperties.SetName(nullCheck, caption + " is null");
                    AutomationProperties.SetAutomationId(nullCheck, "row-null-" + column.Field);
                    _nulls.Add(column.Field, nullCheck); Grid.SetColumn(nullCheck, 1); line.Children.Add(nullCheck);
                    nullCheck.Click += (_, _) => RefreshReadOnly();
                }
                var label = new TextBlock { Text = caption + (column.AllowNull ? "" : " *"), FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = _errorBrush };
                AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                var editorBorder = new Border { Child = line, BorderThickness = new Thickness(1), BorderBrush = _transparent, CornerRadius = new CornerRadius(2) };
                var field = new Grid { RowSpacing = 3, ColumnSpacing = 8, MinHeight = layout.RowSpan * 36 + (layout.RowSpan - 1) * 12 };
                if (options.CaptionPlacement == RowEditCaptionPlacement.Beside)
                {
                    field.ColumnDefinitions.Add(new() { Width = new GridLength(options.CaptionWidth) });
                    field.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    field.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                    field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    label.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(editorBorder, 1); Grid.SetRow(error, 1); Grid.SetColumn(error, 1);
                }
                else
                {
                    field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    field.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                    field.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    Grid.SetRow(editorBorder, 1); Grid.SetRow(error, 2);
                }
                field.Children.Add(label); field.Children.Add(editorBorder); field.Children.Add(error);
                _errors.Add(column.Field, error); _errorBorders.Add(column.Field, editorBorder);
                _fieldPanels.Add(column.Field, field); _fields.Children.Add(field);
            }
        }
        catch { DisposeEditors(); throw; }
        FieldPanels = new ReadOnlyDictionary<string, FrameworkElement>(_fieldPanels.ToDictionary(p => p.Key, p => (FrameworkElement)p.Value, StringComparer.OrdinalIgnoreCase));
        FieldsViewport = new ScrollViewer { Content = _fields, MaxHeight = options.MaximumFieldHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel { Spacing = 10 };
        if (options.ShowInstructions) body.Children.Add(new TextBlock { Text = "Changes are saved together. Update commits the row; Cancel discards the form.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        _message.Foreground = _errorBrush; _validationSurface.Child = _message;
        _validationSurface.Background = _errorBackground; _validationSurface.BorderBrush = _errorBrush;
        body.Children.Add(FieldsViewport); body.Children.Add(_validationSurface); Content = body;
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _fields.SizeChanged += FieldsSizeChanged; ActualThemeChanged += ThemeChanged;
        UpdateValidationPalette(); ArrangeFields(options.MaximumWidth); RefreshReadOnly();
    }
    private void FieldsSizeChanged(object sender, SizeChangedEventArgs args) => ArrangeFields(args.NewSize.Width);
    private void ThemeChanged(FrameworkElement sender, object args) => UpdateValidationPalette();
    private void UpdateValidationPalette()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _errorBrush.Color = dark ? Windows.UI.Color.FromArgb(255, 255, 180, 171) : Windows.UI.Color.FromArgb(255, 180, 35, 24);
        _errorBackground.Color = dark ? Windows.UI.Color.FromArgb(255, 64, 35, 35) : Windows.UI.Color.FromArgb(255, 255, 241, 240);
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
    public void RefreshReadOnly()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            bool canEdit = Buffer.CanEdit;
            var failures = new List<GridValidationError>();
            foreach (var column in Buffer.Columns)
            {
                if (!_bindings.TryGetValue(column.Field, out var binding)) continue;
                bool enabled = canEdit && !column.IsReadOnly;
                if (_nulls.TryGetValue(column.Field, out var nullCheck))
                {
                    if (nullCheck.IsEnabled != enabled) nullCheck.IsEnabled = enabled;
                    enabled &= nullCheck.IsChecked != true;
                }
                try { if (binding.RefreshReadOnly(!enabled)) ReadOnlyUpdateCount++; }
                catch (Exception error) { failures.Add(new(Buffer.RecordId, column.Field, "Editor permission update failed: " + error.Message)); }
            }
            _permissionErrors = failures.AsReadOnly();
        }
        catch (Exception error) { _permissionErrors = Array.AsReadOnly(new[] { new GridValidationError(Buffer.RecordId, null, error.Message) }); }
        finally { _refreshing = false; }
        if (_permissionErrors.Count > 0) ShowValidation(_permissionErrors[0].Message, _permissionErrors);
    }
    public bool TryCommit()
    {
        if (_disposed || _capturing || _refreshing || !Buffer.IsActive) return false;
        _capturing = true;
        string? currentField = null;
        try
        {
            RefreshReadOnly();
            if (_permissionErrors.Count > 0) return false;
            if (Buffer.CanEdit)
            {
                var captured = new List<KeyValuePair<string, object?>>(_bindings.Count);
                foreach (var column in Buffer.Columns)
                {
                    if (column.IsReadOnly || !_bindings.TryGetValue(column.Field, out var binding)) continue;
                    currentField = column.Field; object? value = null;
                    if (!_nulls.TryGetValue(column.Field, out var toggle) || toggle.IsChecked != true)
                    { ReaderCaptureCount++; value = binding.ReadValue(); }
                    captured.Add(new(column.Field, value));
                }
                currentField = null;
                Buffer.SetValues(captured);
            }
            bool saved = Buffer.TryCommit();
            ShowValidation(Buffer.Error, Buffer.Errors);
            if (!saved) FocusInvalidEditor();
            return saved;
        }
        catch (Exception error)
        {
            string message = currentField is null ? error.Message : $"{currentField}: {error.Message}";
            ShowValidation(message, Array.AsReadOnly(new[] { new GridValidationError(Buffer.RecordId, currentField, message) }));
            FocusInvalidEditor(); return false;
        }
        finally { _capturing = false; }
    }
    private void ShowValidation(string? message, IReadOnlyList<GridValidationError> errors)
    {
        ValidationMessage = message; ValidationErrors = errors;
        if (_message.Text != (message ?? "")) _message.Text = message ?? "";
        var visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (_validationSurface.Visibility != visibility) _validationSurface.Visibility = visibility;
        var byField = errors.Where(e => e.Field is not null).ToLookup(e => e.Field!, StringComparer.OrdinalIgnoreCase);
        foreach (var (field, element) in _errors)
        {
            string text = string.Join("\n", byField[field].Select(e => e.Message));
            if (element.Text != text) element.Text = text;
            visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (element.Visibility != visibility) element.Visibility = visibility;
            var brush = text.Length == 0 ? _transparent : _errorBrush;
            if (!ReferenceEquals(_errorBorders[field].BorderBrush, brush)) _errorBorders[field].BorderBrush = brush;
        }
    }
    private void FocusInvalidEditor()
    {
        foreach (var error in ValidationErrors)
            if (error.Field is { } field && _bindings.TryGetValue(field, out var binding) && binding.FocusTarget is { } control && control.IsEnabled)
            { control.Focus(FocusState.Programmatic); break; }
    }
    private void DisposeEditors()
    {
        // Continue releasing later editors even when application cleanup throws.
        // Preserve failures for the host to inspect; never turn a saved transaction
        // into an apparent Cancel because a post-close cleanup callback failed.
        for (int i = _ownedBindings.Count - 1; i >= 0; i--)
            try { _ownedBindings[i].Dispose(); } catch (Exception error) { _cleanupErrors.Add(error.ToString()); }
        _ownedBindings.Clear(); _bindings.Clear();
    }
    public new void Dispose()
    {
        if (_disposed) return;
        if (_capturing || _refreshing) throw new InvalidOperationException("A row form operation is in progress.");
        _disposed = true; _fields.SizeChanged -= FieldsSizeChanged; ActualThemeChanged -= ThemeChanged;
        DisposeEditors(); Buffer.Dispose();
    }
}

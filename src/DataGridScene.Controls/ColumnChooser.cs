using DataGridScene.Core;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.System;

namespace DataGridScene.Controls;

public enum ColumnChooserScope { All, Visible, Hidden }

/// <summary>Native virtualized column visibility list with retained pin/width editors.
/// Changes apply immediately. Invalid or conflicting width text remains available until
/// corrected or cancelled; opening/searching the chooser never changes column geometry.</summary>
public sealed partial class ColumnChooser : UserControl, IDisposable
{
    private readonly GridLayout _layout;
    private readonly GridSchema _schema;
    private readonly ListView _list = new() { Height = 280, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _search = new() { PlaceholderText = "Find a column…", MaxLength = 1024, MinWidth = 0, MinHeight = 30 };
    private readonly ComboBox _scope = new() { ItemsSource = Enum.GetValues<ColumnChooserScope>(), SelectedItem = ColumnChooserScope.All, MinWidth = 90, MinHeight = 30 };
    private readonly TextBlock _count = new() { FontSize = 11, Opacity = .7 };
    private readonly TextBlock _caption = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _width = new() { MinWidth = 0, MinHeight = 30 };
    private readonly ComboBox _pin = new() { ItemsSource = Enum.GetValues<ColumnPin>(), MinWidth = 85, MinHeight = 30 };
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, FrameworkElement> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColumnChooserEntry> _byField = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ColumnChooserEntry> _ordered = Array.Empty<ColumnChooserEntry>();
    private IReadOnlyList<ColumnChooserEntry> _results = Array.Empty<ColumnChooserEntry>();
    private ColumnChooserEntry? _selected;
    private bool _sync, _widthDirty, _subscribed, _disposed;
    private double _widthBaseline;
    private int _revision = -1;
    private string? _appliedSearch;
    private ColumnChooserScope? _appliedScope;
    public IReadOnlyList<ColumnChooserEntry> Entries => _ordered;
    public IReadOnlyList<ColumnChooserEntry> Results => _results;
    public IReadOnlyDictionary<string, FrameworkElement> Actions => _actions;
    public string SearchText => _search.Text;
    public string? SelectedField => _selected?.Field;
    public string WidthText { get => _width.Text; set { ThrowIfDisposed(); _width.Text = value ?? throw new ArgumentNullException(nameof(value)); } }
    public bool HasPendingWidth => _widthDirty;
    public string ValidationMessage => _error.Text;
    public long MetadataRefreshCount { get; private set; }
    public long SearchPassCount { get; private set; }
    public long ListPublicationCount { get; private set; }
    public long CreatedRowCount { get; private set; }
    public long RowBindCount { get; private set; }
    /// <summary>Optional synchronous host validation, invoked only before a changed layout write.
    /// Throw to reject without losing width input or leaving a checked/pin control out of sync.</summary>
    public Action? ValidateChange { get; set; }
    public ColumnChooserScope FilterScope
    {
        get => _scope.SelectedItem is ColumnChooserScope value ? value : ColumnChooserScope.All;
        set { ThrowIfDisposed(); if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _scope.SelectedItem = value; }
    }
    public ColumnChooser(GridLayout layout, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(layout); ArgumentNullException.ThrowIfNull(schema);
        if (layout.Columns.Count != schema.Columns.Count) throw new ArgumentException("Chooser layout and schema must contain the same fields.");
        _layout = layout; _schema = schema; MinWidth = 320;
        foreach (var column in layout.Columns) _byField.Add(column.Field, new ColumnChooserEntry(column, schema[column.Field].Caption));
        _ordered = Array.AsReadOnly(_byField.Values.OrderBy(entry => entry.Order).ToArray());
        _selected = _ordered.FirstOrDefault();
        var content = new StackPanel { Spacing = 8 };
        var find = new Grid { ColumnSpacing = 6 };
        find.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); find.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        find.Children.Add(_search); Grid.SetColumn(_scope, 1); find.Children.Add(_scope); content.Children.Add(find);
        content.Children.Add(_count);
        _list.ItemTemplate = new DataTemplate(() => { CreatedRowCount++; return new ChoicePresenter(this, CreatedRowCount); });
        content.Children.Add(_list); content.Children.Add(_caption);
        var details = new Grid { ColumnSpacing = 6, RowSpacing = 3 };
        details.RowDefinitions.Add(new() { Height = GridLength.Auto }); details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        details.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        details.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); details.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var pinLabel = new TextBlock { Text = "Freeze", FontSize = 11, Opacity = .7 };
        var widthLabel = new TextBlock { Text = "Width", FontSize = 11, Opacity = .7 }; Grid.SetColumn(widthLabel, 1);
        details.Children.Add(pinLabel); details.Children.Add(widthLabel);
        Grid.SetRow(_pin, 1); details.Children.Add(_pin); Grid.SetRow(_width, 1); Grid.SetColumn(_width, 1); details.Children.Add(_width);
        Button Make(string key, string text, Action action)
        {
            var button = new Button { Content = text, MinHeight = 30, Padding = new Thickness(8, 3, 8, 3), FontSize = 12 };
            button.Click += (_, _) => action(); _actions[key] = button; AutomationProperties.SetName(button, text); return button;
        }
        var apply = Make("apply-width", "Apply", () => CommitWidth()); Grid.SetRow(apply, 1); Grid.SetColumn(apply, 2); details.Children.Add(apply);
        var cancel = Make("cancel-width", "Cancel width", CancelWidth); Grid.SetRow(cancel, 1); Grid.SetColumn(cancel, 3); details.Children.Add(cancel);
        content.Children.Add(details); content.Children.Add(_error);
        content.Children.Add(new TextBlock { Text = "Changes apply immediately. Arrow keys select; Space shows or hides.", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        Content = content;
        _actions["search"] = _search; _actions["scope"] = _scope; _actions["list"] = _list;
        _actions["width"] = _width; _actions["pin"] = _pin; _actions["caption"] = _caption;
        AutomationProperties.SetName(_search, "Find a column"); AutomationProperties.SetName(_scope, "Columns to display");
        AutomationProperties.SetName(_list, "Column visibility"); AutomationProperties.SetName(_width, "Column width"); AutomationProperties.SetName(_pin, "Freeze column");
        AutomationProperties.SetName(_error, "Column layout error");
        _search.TextChanged += (_, _) => { if (_sync || _disposed) return; _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshMatches(); };
        _scope.SelectionChanged += (_, _) => { if (!_sync && !_disposed) RefreshMatches(); };
        _list.SelectionChanged += (_, _) => { if (!_sync && !_disposed && _list.SelectedItem is ColumnChooserEntry entry && !SelectColumn(entry.Field)) SyncSelection(); };
        _list.KeyDown += (_, args) =>
        {
            if (_disposed || args.Key != VirtualKey.Space || _list.SelectedItem is not ColumnChooserEntry entry) return;
            for (var source = args.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, _list); source = VisualTreeHelper.GetParent(source))
                if (source is CheckBox) return;
            SetColumnVisible(entry.Field, !entry.Visible); args.Handled = true;
        };
        _pin.SelectionChanged += (_, _) => { if (!_sync && !_disposed && _selected is not null && _pin.SelectedItem is ColumnPin pin) { SetColumnPin(_selected.Field, pin); SyncSelection(); } };
        _width.TextChanged += (_, _) => { if (!_sync && !_disposed) _widthDirty = true; };
        _width.LostFocus += (_, _) => { if (!_disposed) CommitWidth(); };
        _width.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) { CommitWidth(); args.Handled = true; }
            else if (args.Key == VirtualKey.Escape) { CancelWidth(); args.Handled = true; }
        };
        Loaded += (_, _) => { if (!_disposed) { Subscribe(); Refresh(); } };
        Unloaded += (_, _) => { _searchTimer.Stop(); Unsubscribe(); };
        ActualThemeChanged += (_, _) => UpdateErrorTheme();
        Subscribe(); Refresh(); CancelWidth(); UpdateErrorTheme();
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void Subscribe() { if (_subscribed) return; _layout.Changed += LayoutChanged; _subscribed = true; }
    private void Unsubscribe() { if (!_subscribed) return; _layout.Changed -= LayoutChanged; _subscribed = false; }
    private void LayoutChanged(object? sender, EventArgs args) => Refresh();
    /// <summary>Refresh existing column metadata after external layout changes. Loaded controls
    /// subscribe automatically; repeated revisions do no work. No records are enumerated.</summary>
    public void Refresh()
    {
        ThrowIfDisposed();
        if (_revision == _layout.Revision) return;
        bool reorder = false, rematch = _revision < 0;
        foreach (var column in _layout.Columns)
        {
            var entry = _byField[column.Field]; reorder |= entry.Order != column.Order;
            rematch |= entry.Update(column);
        }
        _revision = _layout.Revision; MetadataRefreshCount++;
        if (reorder) _ordered = Array.AsReadOnly(_byField.Values.OrderBy(entry => entry.Order).ToArray());
        if (rematch || reorder) RefreshMatches(force: true);
        else UpdateCount();
        SyncSelection();
    }
    public void SetSearch(string text)
    {
        ThrowIfDisposed(); ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024) throw new ArgumentException("Column search is limited to 1,024 characters.", nameof(text));
        _searchTimer.Stop(); bool old = _sync; _sync = true;
        try { _search.Text = text; } finally { _sync = old; }
        RefreshMatches();
    }
    private void RefreshMatches(bool force = false)
    {
        if (_disposed) return;
        string search = _search.Text.Trim(); var scope = FilterScope;
        if (!force && search == _appliedSearch && scope == _appliedScope) return;
        _appliedSearch = search; _appliedScope = scope; SearchPassCount++;
        var next = _ordered.Where(entry => (scope == ColumnChooserScope.All || entry.Visible == (scope == ColumnChooserScope.Visible)) &&
            (entry.Caption.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Field.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Band.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!_results.SequenceEqual(next))
        {
            _results = Array.AsReadOnly(next); bool old = _sync; _sync = true;
            try { _list.ItemsSource = _results; ListPublicationCount++; } finally { _sync = old; }
        }
        UpdateCount(); SyncSelection();
    }
    private void UpdateCount() => _count.Text = $"{_results.Count:N0} of {_ordered.Count:N0} columns · {_ordered.Count(entry => entry.Visible):N0} visible";
    public bool SelectColumn(string field)
    {
        ThrowIfDisposed(); Refresh();
        if (!_byField.TryGetValue(field, out var entry)) throw new ArgumentException("Unknown column.", nameof(field));
        if (ReferenceEquals(entry, _selected)) return true;
        if (!CommitWidth()) return false;
        _selected = entry; _widthDirty = false; SyncSelection(); return true;
    }
    private void SyncSelection()
    {
        bool old = _sync; _sync = true;
        try
        {
            _list.SelectedItem = _selected is not null && _results.Contains(_selected) ? _selected : null;
            _pin.IsEnabled = _width.IsEnabled = _selected is not null;
            _actions["apply-width"].SetValue(Control.IsEnabledProperty, _selected is not null);
            _actions["cancel-width"].SetValue(Control.IsEnabledProperty, _selected is not null);
            _caption.Text = _selected is null ? "No column selected" : $"{_selected.Caption}  [{_selected.Field}]";
            _pin.SelectedItem = _selected?.Pin;
            if (!_widthDirty)
            {
                _widthBaseline = _selected?.Width ?? 0;
                string text = _selected is null ? "" : _widthBaseline.ToString("R", CultureInfo.InvariantCulture);
                if (_width.Text != text) _width.Text = text;
            }
        }
        finally { _sync = old; }
    }
    private bool Run(Action action)
    {
        try { ThrowIfDisposed(); action(); SetError(""); return true; }
        catch (Exception error) { SetError(error.Message); return false; }
    }
    private void SetError(string message) { _error.Text = message; _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
    private void UpdateErrorTheme() => _error.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 255, 160, 160) : Windows.UI.Color.FromArgb(255, 180, 30, 30));
    public bool SetColumnVisible(string field, bool visible) => Run(() =>
    {
        Refresh(); var entry = _byField.TryGetValue(field, out var found) ? found : throw new ArgumentException("Unknown column.", nameof(field));
        if (entry.Visible == visible) return;
        ValidateChange?.Invoke(); _layout.SetVisible(entry.Field, visible); Refresh();
    });
    public bool SetColumnPin(string field, ColumnPin pin) => Run(() =>
    {
        if (!Enum.IsDefined(pin)) throw new ArgumentOutOfRangeException(nameof(pin));
        Refresh(); var entry = _byField.TryGetValue(field, out var found) ? found : throw new ArgumentException("Unknown column.", nameof(field));
        if (entry.Pin == pin) return;
        ValidateChange?.Invoke(); _layout.SetPin(entry.Field, pin); Refresh();
    });
    public bool CommitWidth()
    {
        if (!_widthDirty || _selected is null) return !_disposed;
        return Run(() =>
        {
            Refresh();
            if (!double.TryParse(_width.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) || !double.IsFinite(width) || width < GridLayout.MinimumWidth || width > GridLayout.MaximumWidth)
                throw new FormatException($"Width must be a finite number from {GridLayout.MinimumWidth} to {GridLayout.MaximumWidth} (use a decimal point).");
            if (_selected.Width != _widthBaseline) throw new InvalidOperationException("This column width changed elsewhere. Cancel width to load the current value before editing it.");
            if (width != _selected.Width) { ValidateChange?.Invoke(); _layout.SetWidth(_selected.Field, width); Refresh(); }
            _widthDirty = false; SyncSelection();
        });
    }
    public void CancelWidth() { if (_disposed) return; _widthDirty = false; Refresh(); SyncSelection(); SetError(""); }
    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _searchTimer.Stop(); Unsubscribe(); ValidateChange = null;
        foreach (var row in _realized.ToArray()) row.Detach();
        _realized.Clear();
    }
}

/// <summary>Stable column metadata identity. Layout values are refreshed in place by the chooser;</n/// consumers cannot write them or replace the owned result lists.</summary>
public sealed class ColumnChooserEntry
{
    public string Field { get; }
    public string Caption { get; }
    public string Band { get; private set; }
    public bool Visible { get; private set; }
    public ColumnPin Pin { get; private set; }
    public double Width { get; private set; }
    internal int Order { get; private set; }
    internal event EventHandler? Changed;
    internal ColumnChooserEntry(ColumnState state, string caption)
    { Field = state.Field; Caption = caption; Band = ""; Update(state); }
    internal bool Update(ColumnState state)
    {
        string band = string.Join(" / ", state.BandPath ?? Array.Empty<string>());
        bool rematch = Band != band || Visible != state.Visible;
        bool changed = rematch || Pin != state.Pin || Width != state.Width || Order != state.Order;
        Band = band; Visible = state.Visible; Pin = state.Pin; Width = state.Width; Order = state.Order;
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return rematch;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Controls;

public sealed record FilterPanelToken(string Key, string Caption, string Description);

/// <summary>Reusable native filter strip with independent enable, edit, remove and clear commands.
/// Equal state does not rebuild controls. Expansion and history are bounded native surfaces.</summary>
public sealed partial class GridFilterPanel : UserControl
{
    private readonly Grid _layout = new() { ColumnSpacing = 4, Padding = new Thickness(6, 3, 6, 3) };
    private readonly StackPanel _tokens = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private readonly CheckBox _enabled = new() { MinWidth = 28, MinHeight = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, FrameworkElement> _actions = new(StringComparer.Ordinal);
    private IReadOnlyList<FilterPanelToken> _items = Array.Empty<FilterPanelToken>();
    private bool _dark;
    public IReadOnlyList<FilterPanelToken> Items => _items;
    public bool IsFilterEnabled => _enabled.IsChecked == true;
    public long ContentBuildCount { get; private set; }
    public IReadOnlyDictionary<string, FrameworkElement> Actions => new System.Collections.ObjectModel.ReadOnlyDictionary<string, FrameworkElement>(_actions);
    public event EventHandler<bool>? EnableRequested;
    public event EventHandler<string>? RemoveRequested;
    public event EventHandler<string>? TokenEditRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? ClearRequested;
    public GridFilterPanel()
    {
        IsTabStop = false; Visibility = Visibility.Collapsed;
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _enabled.IsChecked = true; AutomationProperties.SetName(_enabled, "Enable filter");
        ToolTipService.SetToolTip(_enabled, "Temporarily disable or enable filters without losing conditions");
        _enabled.Click += (_, _) => EnableRequested?.Invoke(this, _enabled.IsChecked == true);
        _actions["filter-panel-enable"] = _enabled; _layout.Children.Add(_enabled);
        var scroll = new ScrollViewer { Content = _tokens, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled };
        Grid.SetColumn(scroll, 1); _layout.Children.Add(scroll);
        InitializeHistoryAndExpansion();
        var edit = ActionButton("Edit Filter", () => EditRequested?.Invoke(this, EventArgs.Empty));
        _actions["filter-panel-edit"] = edit; Grid.SetColumn(edit, 4); _layout.Children.Add(edit);
        var clear = ActionButton("×", () => ClearRequested?.Invoke(this, EventArgs.Empty));
        AutomationProperties.SetName(clear, "Clear filter conditions");
        _actions["filter-panel-clear"] = clear; Grid.SetColumn(clear, 5); _layout.Children.Add(clear);
        Content = _layout; ApplyTheme(false);
    }
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, FontSize = 12, MinHeight = 26, Height = 26, MinWidth = 26,
            Padding = new Thickness(6, 1, 6, 1), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(1) };
        button.Click += (_, _) => action(); return button;
    }
    public void SetState(IEnumerable<FilterPanelToken> items, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(items);
        var next = items.Take(1026).ToArray();
        if (next.Length > 1025 || next.Any(item => item is null || string.IsNullOrEmpty(item.Key)) || next.Select(item => item.Key).Distinct().Count() != next.Length)
            throw new ArgumentException("Filter tokens must have unique keys and at most 1,025 entries.", nameof(items));
        _enabled.IsChecked = enabled; _tokens.Opacity = enabled ? 1 : 0.55;
        Visibility = next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _expandButton.IsEnabled = next.Length > 0;
        if (next.Length == 0) IsExpanded = false;
        if (_items.SequenceEqual(next)) return;
        _items = Array.AsReadOnly(next); Rebuild();
    }
    private void Rebuild()
    {
        _tokens.Children.Clear();
        foreach (string key in _actions.Keys.Where(key => key.StartsWith("filter-token-", StringComparison.Ordinal)).ToArray()) _actions.Remove(key);
        foreach (var item in _items)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var caption = new TextBlock { Text = item.Caption, FontSize = 12, Margin = new Thickness(6, 4, 4, 4) };
            content.Children.Add(new Border { Background = new SolidColorBrush(_dark ? Windows.UI.Color.FromArgb(255, 81, 67, 43) : Windows.UI.Color.FromArgb(255, 251, 226, 187)), Child = caption });
            content.Children.Add(new TextBlock { Text = item.Description, FontSize = 12, MaxWidth = 220, Margin = new Thickness(6, 4, 6, 4), TextTrimming = TextTrimming.CharacterEllipsis });
            var chip = ActionButton("", () => TokenEditRequested?.Invoke(this, item.Key));
            chip.Content = content; chip.Padding = new Thickness(0);
            ToolTipService.SetToolTip(chip, item.Caption + ": " + item.Description);
            AutomationProperties.SetName(chip, item.Caption + ": " + item.Description);
            var remove = ActionButton("×", () => RemoveRequested?.Invoke(this, item.Key));
            AutomationProperties.SetName(remove, "Remove " + item.Caption + " filter");
            var token = new StackPanel { Orientation = Orientation.Horizontal };
            token.Children.Add(chip); token.Children.Add(remove);
            _tokens.Children.Add(new Border { Background = new SolidColorBrush(_dark ? Windows.UI.Color.FromArgb(255, 39, 69, 54) : Windows.UI.Color.FromArgb(255, 218, 238, 224)), CornerRadius = new CornerRadius(1), Child = token });
            _actions["filter-token-edit-" + item.Key] = chip; _actions["filter-token-remove-" + item.Key] = remove;
        }
        ContentBuildCount++;
    }
    public void ApplyTheme(bool dark)
    {
        if (_dark == dark && ContentBuildCount > 0) return;
        _dark = dark; RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _layout.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 42, 42, 42) : Windows.UI.Color.FromArgb(255, 246, 246, 246));
        UpdateHistoryTheme();
        if (_items.Count > 0) Rebuild();
    }
}

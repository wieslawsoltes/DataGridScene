using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Controls;

public sealed record FilterPanelHistoryItem(long Id, string Description, bool IsCurrent = false);

public sealed partial class GridFilterPanel
{
    private readonly Button _historyButton = ActionButton("", () => { });
    private readonly Button _expandButton = ActionButton("", () => { });
    private readonly VectorIcon _historyIcon = new(SceneGlyph.DropDown, 16);
    private readonly VectorIcon _expandIcon = new(SceneGlyph.Expand, 16);
    private readonly VectorIcon _collapseIcon = new(SceneGlyph.Collapse, 16);
    private readonly TextBlock _expression = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(6, 5, 6, 5) };
    private readonly ScrollViewer _expressionScroll = new() { MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, Visibility = Visibility.Collapsed };
    private IReadOnlyList<FilterPanelHistoryItem> _history = Array.Empty<FilterPanelHistoryItem>();
    private MenuFlyout? _historyPopup;
    private bool _expanded;
    public IReadOnlyList<FilterPanelHistoryItem> HistoryItems => _history;
    public bool IsHistoryOpen => _historyPopup is not null;
    public long HistoryBuildCount { get; private set; }
    public string ExpandedExpression => _expression.Text;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            _expressionScroll.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _expandButton.Content = value ? _collapseIcon : _expandIcon;
            string label = value ? "Collapse filter expression" : "Expand filter expression";
            AutomationProperties.SetName(_expandButton, label); ToolTipService.SetToolTip(_expandButton, label);
        }
    }
    public event EventHandler<long>? HistoryRequested;
    public event EventHandler? HistoryClearRequested;
    private void InitializeHistoryAndExpansion()
    {
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _historyButton.Content = _historyIcon; _historyButton.IsEnabled = false;
        AutomationProperties.SetName(_historyButton, "Recent filters"); ToolTipService.SetToolTip(_historyButton, "Recent filters");
        _historyButton.Click += (_, _) => ShowHistory(_historyButton);
        _expandButton.Content = _expandIcon; _expandButton.IsEnabled = false;
        AutomationProperties.SetName(_expandButton, "Expand filter expression"); ToolTipService.SetToolTip(_expandButton, "Expand filter expression");
        _expandButton.Click += (_, _) => IsExpanded = !IsExpanded;
        Grid.SetColumn(_historyButton, 2); _layout.Children.Add(_historyButton);
        Grid.SetColumn(_expandButton, 3); _layout.Children.Add(_expandButton);
        _actions["filter-panel-history"] = _historyButton; _actions["filter-panel-expand"] = _expandButton;
        _expressionScroll.Content = _expression; Grid.SetRow(_expressionScroll, 1); Grid.SetColumnSpan(_expressionScroll, 6); _layout.Children.Add(_expressionScroll);
        AutomationProperties.SetName(_expression, "Full filter expression");
        Unloaded += (_, _) => CloseHistory();
    }
    public void SetExpression(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Length > 16384) throw new ArgumentException("Filter expression display is limited to 16,384 characters.", nameof(expression));
        if (_expression.Text != expression) _expression.Text = expression;
        _expression.Opacity = IsFilterEnabled ? 1 : .55;
    }
    public void SetHistory(IEnumerable<FilterPanelHistoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var next = items.Take(101).ToArray();
        if (next.Length > 100 || next.Any(item => item is null || item.Id <= 0 || item.Description is null || item.Description.Length > 16384) || next.Select(item => item.Id).Distinct().Count() != next.Length)
            throw new ArgumentException("History requires unique positive identities and at most 100 bounded descriptions.", nameof(items));
        if (_history.SequenceEqual(next)) return;
        CloseHistory(); _history = Array.AsReadOnly(next); _historyButton.IsEnabled = next.Length > 0;
    }
    /// <summary>Build native menu items only on explicit opening; never on paint or MRU capture.
    /// The anchor can belong to an application toolbar when the automatic panel is hidden.</summary>
    public void ShowHistory(FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor); CloseHistory();
        var popup = new MenuFlyout();
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 500d));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 320d));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _dark ? ElementTheme.Dark : ElementTheme.Light));
        popup.MenuFlyoutPresenterStyle = style;
        foreach (var entry in _history)
        {
            string title = entry.Description.Length <= 140 ? entry.Description : entry.Description[..139] + "…";
            var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = entry.IsCurrent };
            AutomationProperties.SetName(item, entry.Description); ToolTipService.SetToolTip(item, entry.Description);
            item.Click += (_, _) => HistoryRequested?.Invoke(this, entry.Id);
            popup.Items.Add(item); _actions["filter-history-item-" + entry.Id] = item;
        }
        if (_history.Count == 0) popup.Items.Add(new MenuFlyoutItem { Text = "No recent filters", IsEnabled = false });
        else
        {
            popup.Items.Add(new MenuFlyoutSeparator());
            var clear = new MenuFlyoutItem { Text = "Clear recent filters" };
            clear.Click += (_, _) => HistoryClearRequested?.Invoke(this, EventArgs.Empty);
            popup.Items.Add(clear); _actions["filter-history-clear"] = clear;
        }
        AddDisplayModeOptions(popup);
        _historyPopup = popup; HistoryBuildCount++;
        popup.Closed += (_, _) => { if (ReferenceEquals(_historyPopup, popup)) { _historyPopup = null; RemoveHistoryActions(); } };
        try { popup.ShowAt(anchor); }
        catch { CloseHistory(); throw; }
    }
    public void CloseHistory()
    {
        var popup = _historyPopup; _historyPopup = null; popup?.Hide(); RemoveHistoryActions();
    }
    private void RemoveHistoryActions()
    {
        foreach (string key in _actions.Keys.Where(key => key.StartsWith("filter-history-", StringComparison.Ordinal)).ToArray()) _actions.Remove(key);
    }
    private void UpdateHistoryTheme()
    {
        _historyIcon.IsDark = _expandIcon.IsDark = _collapseIcon.IsDark = _dark;
        CloseHistory();
    }
}

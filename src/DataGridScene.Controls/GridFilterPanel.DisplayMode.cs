using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Controls;

/// <summary>Presentation policy only; hiding a filter panel never disables its conditions.</summary>
public enum FilterPanelDisplayMode { Automatic, Always, Hidden }

public sealed partial class GridFilterPanel
{
    private FilterPanelDisplayMode _displayMode;
    private readonly TextBlock _emptyIndicator = new()
    {
        Text = "No filter applied", FontSize = 12, Opacity = .65,
        Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    /// <summary>Automatic follows the presence of conditions, Always retains an empty
    /// strip for editing/history access, and Hidden reserves no panel space. This is
    /// host presentation configuration, not part of filter history or workspace JSON.</summary>
    public FilterPanelDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_displayMode == value) return;
            CloseHistory();
            _displayMode = value;
            UpdateDisplayMode(_items.Count);
            DisplayModeChanged?.Invoke(this, value);
        }
    }
    public event EventHandler<FilterPanelDisplayMode>? DisplayModeChanged;
    private void InitializeDisplayMode()
    {
        AutomationProperties.SetName(_emptyIndicator, "No filter applied");
        Grid.SetColumn(_emptyIndicator, 1); _layout.Children.Add(_emptyIndicator);
        _actions["filter-panel-empty"] = _emptyIndicator;
        UpdateDisplayMode(_items.Count);
    }
    private void UpdateDisplayMode(int conditionCount)
    {
        bool hasConditions = conditionCount > 0;
        Visibility = _displayMode == FilterPanelDisplayMode.Hidden ? Visibility.Collapsed
            : _displayMode == FilterPanelDisplayMode.Always || hasConditions ? Visibility.Visible : Visibility.Collapsed;
        _emptyIndicator.Visibility = hasConditions ? Visibility.Collapsed : Visibility.Visible;
        _enabled.IsEnabled = hasConditions;
        _expandButton.IsEnabled = hasConditions;
        if (!hasConditions) IsExpanded = false;
        if (_actions.TryGetValue("filter-panel-clear", out var clear) && clear is Button button) button.IsEnabled = hasConditions;
    }
    private void AddDisplayModeOptions(MenuFlyout popup)
    {
        popup.Items.Add(new MenuFlyoutSeparator());
        var menu = new MenuFlyoutSubItem { Text = "Filter panel", RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light };
        AutomationProperties.SetName(menu, "Filter panel visibility");
        _actions["filter-history-panel-options"] = menu;
        foreach (var (mode, label, key) in new[] {
            (FilterPanelDisplayMode.Automatic, "Show when filtered", "automatic"),
            (FilterPanelDisplayMode.Always, "Always show", "always"),
            (FilterPanelDisplayMode.Hidden, "Hide panel", "hidden") })
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = _displayMode == mode,
                RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light };
            AutomationProperties.SetName(item, label);
            // No grid/query/edit command is invoked by a presentation-only option.
            item.Click += (_, _) => { DisplayMode = mode; CloseHistory(); };
            menu.Items.Add(item); _actions["filter-history-panel-" + key] = item;
        }
        popup.Items.Add(menu);
    }
}

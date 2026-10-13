using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    /// <summary>Read-only diagnostics of existing panel/menu controls. No focus, layout,
    /// realization, handlers or value writes are performed to make an assertion pass.</summary>
    public object FilterPanelSnapshot()
    {
        var grid = DataGrid; var panel = grid.FilterPanel; var actions = panel.Actions;
        var focused = XamlRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var focusPath = new HashSet<DependencyObject>();
        for (var current = focused; current is not null && focusPath.Count < 64; current = VisualTreeHelper.GetParent(current))
            if (!focusPath.Add(current)) break;
        bool Available(string key) => actions.TryGetValue(key, out var element) && element is Control { IsEnabled: true };
        var text = focused as TextBox;
        return new
        {
            visible = panel.Visibility == Visibility.Visible,
            displayMode = panel.DisplayMode.ToString(),
            emptyIndicatorVisible = panel.Visibility == Visibility.Visible && actions.TryGetValue("filter-panel-empty", out var empty) && empty.Visibility == Visibility.Visible,
            enableAvailable = Available("filter-panel-enable"), clearAvailable = Available("filter-panel-clear"),
            editAvailable = Available("filter-panel-edit"),
            items = panel.Items.Select(item => new { item.Key, item.Caption, item.Description }).ToArray(),
            panel.IsFilterEnabled, panel.ContentBuildCount, panel.IsExpanded, panel.ExpandedExpression,
            panel.IsHistoryOpen, panel.HistoryBuildCount,
            // The Controls library is trimmed; export scalar DTOs from this already
            // rooted sample assembly rather than reflecting over library records.
            history = panel.HistoryItems.Select(item => new { item.Id, item.Description, item.IsCurrent }).ToArray(),
            nativeMenu = actions.Where(pair => pair.Key.StartsWith("filter-history-", StringComparison.Ordinal)).Select(pair => new
            {
                key = pair.Key, isFocused = focusPath.Contains(pair.Value),
                isEnabled = pair.Value is Control { IsEnabled: true },
                isChecked = pair.Value is ToggleMenuFlyoutItem toggle ? (bool?)toggle.IsChecked : null,
                theme = pair.Value.ActualTheme.ToString()
            }).ToArray(),
            focusedText = text?.Text, focusedSelectionStart = text?.SelectionStart, focusedSelectionLength = text?.SelectionLength,
            grid.FilterHistoryRevision, grid.FilterHistoryUnavailableReason, grid.FilterDisplayRefreshCount
        };
    }
}

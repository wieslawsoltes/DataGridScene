using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private void InitializeSummaryUI()
    {
        if (_navigation.Child is not StackPanel navigation) return;
        var performance = _actions["scenario-Performance"];
        int destination = navigation.Children.IndexOf(performance);
        navigation.Children.Insert(destination, Button("scenario-Group summaries", "Σ   Group summaries", ApplySummaryScenario));
        var configure = Button("summaries", "Configure summaries…", () => DataGrid.ShowSummaryEditor());
        navigation.Children.Insert(destination + 2, configure);
        foreach (var (name, element) in _actions)
        {
            if (!name.StartsWith("scenario-", StringComparison.Ordinal) || element is not Button button) continue;
            // Reserve identical hit regions before the first layout. Async glyph/font
            // fallback must not move already-painted targets under the pointer.
            button.Height = 40; button.MinHeight = 40; button.MaxHeight = 40;
            button.Padding = new Thickness(12, 4, 8, 4);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            ToolTipService.SetToolTip(button, name["scenario-".Length..]);
        }
        if (navigation.Children[0] is TextBlock heading) heading.Height = 20;
        configure.Height = 32; configure.MinHeight = 32; configure.MaxHeight = 32;
        configure.HorizontalAlignment = HorizontalAlignment.Stretch;
        _navigation.Child = new ScrollViewer
        {
            Content = navigation, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }
    private void ApplySummaryScenario()
    {
        if (!DataGrid.CommitEdit()) return;
        Reload(120); Scenario = "Group summaries";
        DataGrid.ShowBands = false;
        DataGrid.ColumnLayout!.SetVisible("Salesperson", false);
        DataGrid.ColumnLayout.SetVisible("OrderDate", false);
        DataGrid.ColumnLayout.SetVisible("UnitPrice", false);
        DataGrid.ColumnLayout.SetVisible("Discount", false);
        DataGrid.ColumnLayout.SetVisible("Progress", false);
        DataGrid.ColumnLayout.SetVisible("Verified", false);
        DataGrid.ColumnLayout.SetVisible("Trend", false);
        DataGrid.SetQuery(DefaultQuery() with
        {
            Groups = [new("Region"), new("Status")],
            GroupSummaries = [
                new("OrderID", SummaryKind.Count, "orders") { ShowInColumn = "Company", ShowInGroupFooter = true, DisplayFormat = "Orders: {0:N0}" },
                new("Total", SummaryKind.Sum, "sales") { ShowInGroupFooter = true, DisplayFormat = "Sales: {0:C2}" },
                new("Total", SummaryKind.Average, "average") { ShowInGroupFooter = true, DisplayFormat = "Avg: {0:C2}" }
            ],
            GroupSummaryDisplay = GroupSummaryDisplayMode.AlignByColumns,
            GroupFooterDisplay = GroupFooterDisplayMode.ExpandedGroups,
            GroupSummarySorts = [new("Region", "sales", SortDirection.Descending), new("Status", "sales", SortDirection.Descending)]
        });
        RefreshStatus();
    }
}

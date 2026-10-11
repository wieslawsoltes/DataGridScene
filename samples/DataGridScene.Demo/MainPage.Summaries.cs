using DataGridScene.Core;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private void InitializeSummaryUI()
    {
        // Keep the feature in the shared code-first Uno sample; no browser-only substitute UI.
        if (_navigation.Child is StackPanel navigation)
        {
            navigation.Children.Insert(11, Button("scenario-Group summaries", "Σ   Group summaries", ApplySummaryScenario));
            navigation.Children.Insert(12, Button("summaries", "Configure summaries…", () => DataGrid.ShowSummaryEditor()));
        }
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

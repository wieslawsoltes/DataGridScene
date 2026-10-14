using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace DataGridScene.Demo;

internal static class FilterHistoryControlChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var timer = Stopwatch.StartNew();
        void Check(bool value) { if (!value) throw new InvalidOperationException("Filter history control contract failed."); }
        void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new("history-control." + name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("history-control." + name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("restores-split-criteria-and-columns-with-independent-query-options", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            grid.SetCriteria("[Amount] > 5"); grid.SetColumnFilter("Region", "West"); grid.SetColumnValues("Name", new(new object?[] { "Alpha" }));
            long id = grid.RecentFilters[0].Id; grid.ClearFilterConditions(); grid.SetSearch("Alpha"); grid.SortColumn("Amount", SortDirection.Descending); grid.GroupColumn("Region");
            grid.Selection.Select(grid.View!, new(1, "Name")); var options = grid.View!.Query;
            grid.ApplyRecentFilter(id);
            Check(grid.FilterPanel.Items.Count == 3 && grid.FilterTexts["Region"] == "West" && grid.ValueFilters["Name"].Values.Count == 1);
            Check(grid.View.Query.Search == "Alpha" && grid.View.Query.Sorts.SequenceEqual(options.Sorts) && grid.View.Query.Groups.SequenceEqual(options.Groups));
            Check(grid.View.FilteredRecords.Count == 1 && grid.Selection.FocusedCell == new CellAddress(1, "Name") && grid.RecentFilters[0].Id == id);
        });
        Test("current-history-and-repeated-filter-are-noop-for-shaping-and-row-heights", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Region", "West");
            grid.SetSearch("a"); grid.SortColumn("Amount", SortDirection.Descending); grid.SetRowHeight("row:1", 62);
            long refresh = grid.View!.RefreshCount, geometry = grid.RowGeometryBuildCount, history = grid.FilterHistoryRevision, controls = grid.FilterPanel.ContentBuildCount;
            for (int i = 0; i < 20; i++) { grid.SetColumnFilter("Region", "West"); grid.ApplyRecentFilter(grid.RecentFilters[0].Id); }
            Check(grid.View.RefreshCount == refresh && grid.RowGeometryBuildCount == geometry && grid.FilterHistoryRevision == history && grid.FilterPanel.ContentBuildCount == controls);
            Check(grid.HeightIndex[grid.View.IndexOfRecord(1)] == 62);
        });
        Test("suspended-current-history-is-enabled-with-one-refresh", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnValues("Name", new(new object?[] { "Alpha" }));
            long id = grid.RecentFilters[0].Id, revision = grid.FilterHistoryRevision; grid.IsFilterEnabled = false;
            long refresh = grid.View!.RefreshCount; grid.ApplyRecentFilter(id);
            Check(grid.IsFilterEnabled && grid.View.Rows.Count == 1 && grid.View.RefreshCount == refresh + 1 && grid.FilterHistoryRevision == revision);
        });
        Test("value-edits-and-selection-never-touch-history-or-popup-controls", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha");
            var entries = grid.RecentFilters; long revision = grid.FilterHistoryRevision, built = grid.FilterPanel.HistoryBuildCount;
            for (int i = 0; i < 100; i++) { grid.Table!.Apply([new(1, "Amount", i)]); grid.Selection.Select(grid.View!, new(1, "Name")); }
            Check(ReferenceEquals(entries, grid.RecentFilters) && revision == grid.FilterHistoryRevision && built == grid.FilterPanel.HistoryBuildCount);
            long display = grid.FilterDisplayRefreshCount; var displayState = grid.Renderer.FilterTexts;
            for (int i = 0; i < 20; i++)
            {
                using (grid.BeginDataUpdate())
                {
                    grid.Table!.Apply([new(1, "Amount", i)]);
                    using (grid.BeginDataUpdate()) grid.Selection.Select(grid.View!, new(1, "Name"));
                }
            }
            Check(ReferenceEquals(entries, grid.RecentFilters) && revision == grid.FilterHistoryRevision && built == grid.FilterPanel.HistoryBuildCount);
            Check(display == grid.FilterDisplayRefreshCount && ReferenceEquals(displayState, grid.Renderer.FilterTexts));
        });
        Test("clear-history-does-not-clear-active-filter-or-reinsert-on-enable", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha");
            grid.ClearFilterHistory(); long refresh = grid.View!.RefreshCount; grid.IsFilterEnabled = false; grid.IsFilterEnabled = true;
            Check(grid.RecentFilters.Count == 0 && grid.FilterTexts["Name"] == "Alpha" && grid.View.Rows.Count == 1 && grid.View.RefreshCount == refresh + 2);
            Check(!((Microsoft.UI.Xaml.Controls.Button)grid.FilterPanel.Actions["filter-panel-history"]).IsEnabled);
            long revision = grid.FilterHistoryRevision, display = grid.FilterDisplayRefreshCount;
            using (grid.BeginDataUpdate()) { grid.Table!.Apply([new(1, "Amount", 13)]); grid.SetSearch("Alpha"); grid.SortColumn("Amount", SortDirection.Descending); }
            using (grid.BeginDataUpdate()) { grid.IsFilterEnabled = false; grid.IsFilterEnabled = true; }
            using (grid.BeginDataUpdate()) { }
            Check(grid.RecentFilters.Count == 0 && grid.FilterHistoryRevision == revision && grid.FilterDisplayRefreshCount == display);
            Check(grid.FilterPanel.IsFilterEnabled && grid.FilterPanel.Items.Count == 1 && grid.View.Rows.Count == 1);
        });
        Test("clear-conditions-keeps-history-but-replacement-table-does-not", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha"); long id = grid.RecentFilters[0].Id;
            grid.ClearFilterConditions(); Check(grid.FilterPanel.Visibility == Visibility.Collapsed && grid.RecentFilters.Count == 1);
            grid.Table = ValidationSuite.Fixture(); grid.SetColumnFilter("Name", "Beta");
            Check(grid.RecentFilters.Count == 1 && grid.RecentFilters[0].Id != id); Reject<ArgumentException>(() => grid.ApplyRecentFilter(id));
        });
        Test("batch-remembers-only-successful-final-conditions", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            long display = grid.FilterDisplayRefreshCount; var originalDisplay = grid.Renderer.FilterTexts;
            using (grid.BeginDataUpdate())
            {
                grid.SetColumnFilter("Name", "Alpha"); using (grid.BeginDataUpdate()) grid.SetColumnFilter("Region", "West");
                grid.IsFilterEnabled = false; Check(grid.RecentFilters.Count == 0);
                Check(grid.FilterDisplayRefreshCount == display && ReferenceEquals(originalDisplay, grid.Renderer.FilterTexts));
            }
            Check(grid.RecentFilters.Count == 1 && grid.RecentFilters[0].State.TextFilters.Count == 2 && !grid.IsFilterEnabled);
            Check(grid.FilterDisplayRefreshCount == display + 1 && grid.Renderer.FilterTexts["Name"] == "Alpha" && grid.Renderer.FilterTexts["Region"] == "West");
            Check(!grid.FilterPanel.IsFilterEnabled && grid.FilterPanel.Items.Count == 2);
        });
        Test("failed-batch-does-not-retain-intermediate-or-rejected-state", () => {
            bool fail = false; var table = new GridTable(new GridSchema([new("Name", typeof(string)), new("Calc", typeof(int)) { ValueGetter = _ => fail ? throw new InvalidOperationException("Expected getter failure") : 1 }]));
            table.Add(new(1, new object?[] { "A", null })); using var grid = new GridControl { Table = table }; grid.SetColumnFilter("Name", "A");
            var before = grid.RecentFilters; var query = grid.View!.Query; fail = true;
            long display = grid.FilterDisplayRefreshCount;
            Reject<InvalidOperationException>(() => { using (grid.BeginDataUpdate()) { grid.SetColumnFilter("Name", "B"); grid.SetCriteria("[Calc] > 0"); } });
            Check(ReferenceEquals(before, grid.RecentFilters) && ReferenceEquals(query, grid.View.Query) && grid.FilterPanel.Items.Count == 1 && grid.FilterTexts["Name"] == "A");
            Check(grid.FilterDisplayRefreshCount == display + 1 && grid.Renderer.FilterTexts["Name"] == "A");
            grid.ClearFilterHistory(); display = grid.FilterDisplayRefreshCount;
            Reject<InvalidOperationException>(() => { using (grid.BeginDataUpdate()) grid.SetCriteria("[Calc] > 0"); });
            Check(grid.RecentFilters.Count == 0 && grid.FilterDisplayRefreshCount == display + 1 && grid.FilterTexts["Name"] == "A");
        });
        Test("failed-replay-keeps-query-metadata-history-order-and-selection", () => {
            bool fail = false; var table = new GridTable(new GridSchema([new("Name", typeof(string)), new("Calc", typeof(int)) { ValueGetter = _ => fail ? throw new InvalidOperationException("Expected getter failure") : 1 }]));
            table.Add(new(1, new object?[] { "A", null })); using var grid = new GridControl { Table = table }; grid.SetCriteria("[Calc] > 0"); long id = grid.RecentFilters[0].Id;
            grid.ClearFilterConditions(); grid.SetColumnFilter("Name", "A"); grid.Selection.Select(grid.View!, new(1, "Name"));
            var before = grid.RecentFilters; var query = grid.View!.Query; fail = true;
            Reject<InvalidOperationException>(() => grid.ApplyRecentFilter(id));
            Check(ReferenceEquals(before, grid.RecentFilters) && ReferenceEquals(query, grid.View.Query) && grid.FilterPanel.Items.Count == 1 && grid.FilterTexts["Name"] == "A" && grid.Selection.FocusedCell == new CellAddress(1, "Name"));
        });
        Test("capacity-trimming-and-disabled-retention", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture(), FilterHistoryCapacity = 2 };
            foreach (string text in new[] { "A", "B", "C" }) grid.SetColumnFilter("Name", text);
            Check(grid.RecentFilters.Count == 2 && grid.RecentFilters[0].State.TextFilters["Name"] == "C");
            grid.FilterHistoryCapacity = 0; grid.SetColumnFilter("Name", "Alpha"); Check(grid.RecentFilters.Count == 0 && grid.View!.Rows.Count == 1);
        });
        Test("custom-predicates-remain-supported-with-explicit-history-boundary", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; var filter = new ApplicationFilter();
            grid.SetQuery(new GridQuery { Filter = filter }); Check(grid.View!.Rows.Count == 4 && grid.RecentFilters.Count == 0 && grid.FilterHistoryUnavailableReason is not null);
            grid.SetColumnFilter("Name", "Alpha"); Check(grid.View.Rows.Count == 1 && grid.FilterHistoryUnavailableReason is not null);
        });
        Test("oversized-history-does-not-fail-a-valid-published-filter", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha"); var before = grid.RecentFilters;
            grid.SetQuery(new GridQuery { Filter = new FieldFilter("Name", FilterOperator.Equal, new string('X', 70000)) });
            Check(grid.View!.Rows.Count == 0 && ReferenceEquals(before, grid.RecentFilters) && grid.FilterHistoryUnavailableReason is not null);
        });
        Test("expanding-expression-keeps-the-existing-native-token-controls", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha");
            var chip = grid.FilterPanel.Actions["filter-token-edit-column:Name"]; long builds = grid.FilterPanel.ContentBuildCount;
            for (int i = 0; i < 30; i++) grid.FilterPanel.IsExpanded = !grid.FilterPanel.IsExpanded;
            Check(ReferenceEquals(chip, grid.FilterPanel.Actions["filter-token-edit-column:Name"]) && builds == grid.FilterPanel.ContentBuildCount && grid.FilterPanel.ExpandedExpression.Contains("Alpha"));
        });
        Test("native-history-state-is-owned-bounded-and-lazy", () => {
            var panel = new GridFilterPanel(); var items = new List<FilterPanelHistoryItem> { new(1, "One", true) }; panel.SetHistory(items); items.Clear();
            Check(panel.HistoryItems.Count == 1 && panel.HistoryBuildCount == 0);
            Reject<ArgumentException>(() => panel.SetHistory([new(1, "One"), new(1, "Duplicate")])); Check(panel.HistoryItems[0].Description == "One");
            Reject<ArgumentException>(() => panel.SetExpression(new string('X', 16385))); Check(panel.ExpandedExpression == "");
        });
        Test("workspace-replay-still-restores-only-filter-history-options", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnValues("Name", new(new object?[] { "Alpha" })); string workspace = grid.SaveLayout();
            grid.ClearFilterConditions(); grid.RestoreLayout(workspace); Check(grid.RecentFilters.Count == 1 && grid.ValueFilters.Count == 1);
            grid.ClearFilterConditions(); grid.SetSearch("Beta"); grid.ApplyRecentFilter(grid.RecentFilters[0].Id);
            Check(grid.View!.Query.Search == "Beta" && grid.View.Rows.Count == 0 && grid.ValueFilters.Count == 1);
        });
        Test("failed-immediate-filter-does-not-mutate-history", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha"); var entries = grid.RecentFilters;
            Reject<FormatException>(() => grid.SetCriteria("[Amount] > 'not a number'"));
            Check(ReferenceEquals(entries, grid.RecentFilters) && grid.View!.Rows.Count == 1 && grid.FilterTexts["Name"] == "Alpha");
        });
        return new(results.AsReadOnly(), timer.Elapsed.TotalMilliseconds);
    }
    private sealed record ApplicationFilter : GridFilter
    {
        public override Func<GridRecord, bool> Compile(GridSchema schema) => _ => true;
    }
}

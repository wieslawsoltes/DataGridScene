using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace DataGridScene.Demo;

internal static class FilterPanelModeControlChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var timer = Stopwatch.StartNew();
        void Check(bool value) { if (!value) throw new InvalidOperationException("Filter panel mode contract failed."); }
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new("panel-mode." + name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("panel-mode." + name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("automatic-default-tracks-conditions-not-enabled-flag", () => {
            var panel = new GridFilterPanel(); Check(panel.DisplayMode == FilterPanelDisplayMode.Automatic && panel.Visibility == Visibility.Collapsed);
            panel.SetState([new("name", "Name", "Alpha")], true); Check(panel.Visibility == Visibility.Visible);
            panel.SetState([new("name", "Name", "Alpha")], false); Check(panel.Visibility == Visibility.Visible && !panel.IsFilterEnabled);
            panel.SetState([], false); Check(panel.Visibility == Visibility.Collapsed);
        });
        Test("invalid-and-unchanged-modes-do-not-publish-changes", () => {
            var panel = new GridFilterPanel(); int changes = 0; panel.DisplayModeChanged += (_, _) => changes++;
            panel.DisplayMode = FilterPanelDisplayMode.Always; panel.DisplayMode = FilterPanelDisplayMode.Always;
            bool rejected = false; try { panel.DisplayMode = (FilterPanelDisplayMode)99; } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && changes == 1 && panel.DisplayMode == FilterPanelDisplayMode.Always && panel.Visibility == Visibility.Visible);
        });
        Test("always-has-empty-state-with-safe-commands-and-history", () => {
            var panel = new GridFilterPanel { DisplayMode = FilterPanelDisplayMode.Always }; panel.SetState([], true);
            Check(panel.Visibility == Visibility.Visible && panel.Actions["filter-panel-empty"].Visibility == Visibility.Visible);
            Check(!((Control)panel.Actions["filter-panel-enable"]).IsEnabled && !((Control)panel.Actions["filter-panel-clear"]).IsEnabled);
            Check(!((Control)panel.Actions["filter-panel-expand"]).IsEnabled && ((Control)panel.Actions["filter-panel-edit"]).IsEnabled);
            panel.SetHistory([new(1, "Name = Alpha")]); Check(((Control)panel.Actions["filter-panel-history"]).IsEnabled);
            panel.SetState([new("name", "Name", "Alpha")], true);
            Check(panel.Actions["filter-panel-empty"].Visibility == Visibility.Collapsed && ((Control)panel.Actions["filter-panel-enable"]).IsEnabled);
            panel.SetState([], true); Check(panel.Visibility == Visibility.Visible && panel.HistoryItems.Count == 1);
        });
        Test("visibility-changes-retain-query-selection-tokens-and-display", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha");
            grid.Selection.Select(grid.View!, new(1, "Name")); var query = grid.View!.Query; var entries = grid.RecentFilters;
            var display = grid.Renderer.FilterTexts; var chip = grid.FilterPanel.Actions["filter-token-edit-column:Name"];
            long refresh = grid.View.RefreshCount, rows = grid.RowGeometryBuildCount, builds = grid.FilterPanel.ContentBuildCount;
            for (int i = 0; i < 30; i++) grid.FilterPanelMode = (FilterPanelDisplayMode)(i % 3);
            Check(ReferenceEquals(query, grid.View.Query) && ReferenceEquals(entries, grid.RecentFilters) && ReferenceEquals(display, grid.Renderer.FilterTexts));
            Check(ReferenceEquals(chip, grid.FilterPanel.Actions["filter-token-edit-column:Name"]) && grid.FilterPanel.ContentBuildCount == builds);
            Check(grid.View.RefreshCount == refresh && grid.RowGeometryBuildCount == rows && grid.Selection.FocusedCell == new CellAddress(1, "Name"));
            Check(grid.FilterPanel.Visibility == Visibility.Collapsed && grid.View.FilteredRecords.Count == 1 && grid.IsFilterEnabled);
        });
        Test("configured-before-data-and-preserved-through-table-replacement", () => {
            using var grid = new GridControl { FilterPanelMode = FilterPanelDisplayMode.Always, Table = ValidationSuite.Fixture() };
            grid.SetColumnFilter("Name", "Alpha"); long oldId = grid.RecentFilters[0].Id;
            grid.Table = ValidationSuite.Fixture();
            Check(grid.FilterPanelMode == FilterPanelDisplayMode.Always && grid.FilterPanel.Visibility == Visibility.Visible && grid.RecentFilters.Count == 0);
            grid.SetColumnFilter("Name", "Beta"); Check(grid.RecentFilters[0].Id != oldId);
        });
        Test("workspace-restoration-does-not-overwrite-host-presentation-policy", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture(), FilterPanelMode = FilterPanelDisplayMode.Always };
            grid.SetColumnFilter("Name", "Alpha"); string workspace = grid.SaveLayout();
            grid.FilterPanelMode = FilterPanelDisplayMode.Hidden; grid.ClearFilterConditions(); grid.RestoreLayout(workspace);
            Check(grid.FilterPanelMode == FilterPanelDisplayMode.Hidden && grid.FilterPanel.Visibility == Visibility.Collapsed && grid.View!.FilteredRecords.Count == 1);
            Check(grid.FilterTexts["Name"] == "Alpha");
        });
        Test("mode-only-batches-do-not-resurrect-history-or-refresh-data", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha"); grid.ClearFilterHistory();
            long revision = grid.FilterHistoryRevision, displays = grid.FilterDisplayRefreshCount, refreshes = grid.View!.RefreshCount;
            using (grid.BeginDataUpdate())
            {
                grid.FilterPanelMode = FilterPanelDisplayMode.Always;
                using (grid.BeginDataUpdate()) grid.FilterPanelMode = FilterPanelDisplayMode.Hidden;
                grid.FilterPanelMode = FilterPanelDisplayMode.Automatic;
            }
            Check(grid.RecentFilters.Count == 0 && grid.FilterHistoryRevision == revision && grid.FilterDisplayRefreshCount == displays && grid.View.RefreshCount == refreshes);
        });
        Test("history-and-enable-changes-do-not-overwrite-panel-mode", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() }; grid.SetColumnFilter("Name", "Alpha"); long id = grid.RecentFilters[0].Id;
            grid.FilterPanelMode = FilterPanelDisplayMode.Hidden; grid.ClearFilterConditions(); grid.ApplyRecentFilter(id);
            Check(grid.FilterPanelMode == FilterPanelDisplayMode.Hidden && grid.FilterPanel.Visibility == Visibility.Collapsed && grid.View!.Rows.Count == 1);
            grid.IsFilterEnabled = false; grid.FilterPanelMode = FilterPanelDisplayMode.Always;
            Check(!grid.IsFilterEnabled && grid.FilterPanel.Visibility == Visibility.Visible && grid.FilterPanel.Items.Count == 1);
        });
        return new(results.AsReadOnly(), timer.Elapsed.TotalMilliseconds);
    }
}

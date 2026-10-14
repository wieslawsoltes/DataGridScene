using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Validation;
using System.Diagnostics;

namespace DataGridScene.Demo;

internal static class ColumnChooserControlChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var timer = Stopwatch.StartNew();
        void Check(bool value) { if (!value) throw new InvalidOperationException("Column chooser contract failed."); }
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new("chooser-control." + name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("chooser-control." + name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        GridSchema Schema() => new([new("Key", typeof(int)) { Caption = "Identifier", Band = "Primary" }, new("Name", typeof(string)) { Caption = "Customer", Band = "Contact" }, new("Amount", typeof(decimal)) { Caption = "Revenue", Band = "Finance" }]);
        Test("untouched-fractional-width-is-exact-and-nonmutating", () => {
            var schema = Schema(); var layout = new GridLayout(schema); layout.SetWidth("Key", 123.456789012345);
            using var chooser = new ColumnChooser(layout, schema); long revision = layout.Revision;
            Check(double.Parse(chooser.WidthText, System.Globalization.CultureInfo.InvariantCulture) == layout.Columns[0].Width);
            Check(chooser.CommitWidth() && layout.Revision == revision && !chooser.HasPendingWidth);
            chooser.WidthText = chooser.WidthText + "0"; Check(chooser.CommitWidth() && layout.Revision == revision);
        });
        Test("invalid-width-retains-text-and-rejects-selection", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            long revision = layout.Revision; chooser.WidthText = "not a width";
            Check(!chooser.CommitWidth() && chooser.HasPendingWidth && chooser.WidthText == "not a width" && layout.Revision == revision);
            Check(!chooser.SelectColumn("Name") && chooser.SelectedField == "Key" && chooser.WidthText == "not a width");
            chooser.CancelWidth(); Check(!chooser.HasPendingWidth && chooser.ValidationMessage == "" && chooser.SelectColumn("Name"));
        });
        Test("invalid-finite-and-range-values-never-publish", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); int revision = layout.Revision;
            foreach (string text in new[] { "NaN", "Infinity", "-Infinity", "35.9", "4096.01", "", "1,000" })
            { chooser.WidthText = text; Check(!chooser.CommitWidth() && chooser.WidthText == text && layout.Revision == revision); }
            chooser.WidthText = "96.125"; Check(chooser.CommitWidth() && layout.Columns[0].Width == 96.125 && layout.Revision == revision + 1);
        });
        Test("external-width-conflict-retains-input-until-cancel", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            chooser.WidthText = "222.75"; layout.SetWidth("Key", 180.25); int revision = layout.Revision;
            Check(!chooser.CommitWidth() && chooser.WidthText == "222.75" && layout.Revision == revision && layout.Columns[0].Width == 180.25);
            chooser.CancelWidth(); Check(chooser.WidthText == "180.25" && !chooser.HasPendingWidth);
        });
        Test("same-values-skip-host-validation-and-layout-rebuild", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); int calls = 0, revision = layout.Revision;
            chooser.ValidateChange = () => calls++;
            for (int i = 0; i < 50; i++) { Check(chooser.SetColumnVisible("Key", true)); Check(chooser.SetColumnPin("Key", ColumnPin.None)); chooser.Refresh(); }
            Check(calls == 0 && layout.Revision == revision && chooser.MetadataRefreshCount == 1);
        });
        Test("last-visible-rejection-keeps-published-entry-state", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            Check(chooser.SetColumnVisible("Name", false) && chooser.SetColumnVisible("Amount", false)); int revision = layout.Revision;
            Check(!chooser.SetColumnVisible("Key", false) && chooser.Entries.Single(entry => entry.Field == "Key").Visible && layout.Revision == revision);
            Check(!string.IsNullOrEmpty(chooser.ValidationMessage));
        });
        Test("search-matches-caption-field-and-band", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            foreach (string search in new[] { "customer", "NAME", "contact" }) { chooser.SetSearch(search); Check(chooser.Results.Count == 1 && chooser.Results[0].Field == "Name"); }
            chooser.SetSearch("does not exist"); Check(chooser.Results.Count == 0 && chooser.Entries.Count == 3);
            chooser.SetSearch(""); Check(chooser.Results.Count == 3);
        });
        Test("visible-and-hidden-scopes-follow-live-changes", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            chooser.FilterScope = ColumnChooserScope.Hidden; Check(chooser.Results.Count == 0);
            layout.SetVisible("Name", false); Check(chooser.Results.Count == 1 && chooser.Results[0].Field == "Name");
            Check(chooser.SetColumnVisible("Name", true) && chooser.Results.Count == 0);
            chooser.FilterScope = ColumnChooserScope.Visible; Check(chooser.Results.Count == 3);
        });
        Test("metadata-and-result-identities-survive-width-pin-and-search", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema);
            var entries = chooser.Entries; var entry = entries[1]; var resultsBefore = chooser.Results; long publications = chooser.ListPublicationCount;
            layout.SetWidth("Name", 101.5); layout.SetPin("Name", ColumnPin.Left);
            Check(ReferenceEquals(entries, chooser.Entries) && ReferenceEquals(resultsBefore, chooser.Results) && chooser.ListPublicationCount == publications);
            chooser.SetSearch("Name"); Check(ReferenceEquals(entry, chooser.Results[0]) && entry.Width == 101.5 && entry.Pin == ColumnPin.Left);
            chooser.SetSearch(""); Check(ReferenceEquals(entry, chooser.Results[1]));
        });
        Test("search-repeats-and-unrelated-refresh-are-noop", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); chooser.SetSearch("Name");
            long searches = chooser.SearchPassCount, publications = chooser.ListPublicationCount;
            for (int i = 0; i < 40; i++) { chooser.SetSearch("Name"); chooser.Refresh(); }
            Check(searches == chooser.SearchPassCount && publications == chooser.ListPublicationCount);
            layout.SetWidth("Amount", 200); Check(searches == chooser.SearchPassCount && publications == chooser.ListPublicationCount);
        });
        Test("host-rejection-is-nonmutating-and-retains-dirty-width", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); int revision = layout.Revision;
            chooser.ValidateChange = () => throw new InvalidOperationException("Host edit rejected");
            Check(!chooser.SetColumnVisible("Name", false) && !chooser.SetColumnPin("Key", ColumnPin.Right));
            chooser.WidthText = "250.25"; Check(!chooser.CommitWidth() && chooser.HasPendingWidth && chooser.WidthText == "250.25" && layout.Revision == revision);
            chooser.ValidateChange = null; Check(chooser.CommitWidth() && layout.Columns[0].Width == 250.25);
        });
        Test("search-does-not-commit-or-discard-width-buffer", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); int revision = layout.Revision;
            chooser.WidthText = "invalid"; chooser.SetSearch("Name");
            Check(chooser.SelectedField == "Key" && chooser.WidthText == "invalid" && chooser.HasPendingWidth && layout.Revision == revision);
            chooser.CancelWidth(); Check(chooser.SelectColumn("Name") && chooser.SelectedField == "Name");
        });
        Test("reorder-and-band-metadata-stay-synchronized", () => {
            var schema = Schema(); var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); var name = chooser.Entries[1];
            layout.MoveBefore("Name", "Key"); Check(ReferenceEquals(chooser.Entries[0], name));
            layout.SetBandPath("Name", "People", "Accounts"); chooser.SetSearch("Accounts"); Check(ReferenceEquals(chooser.Results[0], name));
        });
        Test("ten-thousand-columns-do-not-create-ten-thousand-controls", () => {
            var schema = new GridSchema(Enumerable.Range(0, 10000).Select(i => new GridColumn($"C{i:D5}", typeof(int))).ToArray());
            var layout = new GridLayout(schema); using var chooser = new ColumnChooser(layout, schema); chooser.SetSearch("C09999");
            Check(chooser.Entries.Count == 10000 && chooser.Results.Count == 1 && chooser.Results[0].Field == "C09999");
            Check(chooser.CreatedRowCount < 100 && chooser.RealizedRowCount == 0);
        });
        Test("layout-writes-do-not-reshape-grid-records", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            using var chooser = new ColumnChooser(grid.ColumnLayout!, grid.Table!.Schema);
            var entry = chooser.Entries[0]; long refreshes = grid.View!.RefreshCount, rows = grid.RowGeometryBuildCount;
            Check(chooser.SetColumnPin(entry.Field, ColumnPin.Right)); chooser.WidthText = "112.625"; Check(chooser.CommitWidth());
            Check(grid.View.RefreshCount == refreshes && grid.RowGeometryBuildCount == rows);
        });
        Test("dispose-detaches-external-layout-subscription", () => {
            var schema = Schema(); var layout = new GridLayout(schema); var chooser = new ColumnChooser(layout, schema); long refreshes = chooser.MetadataRefreshCount;
            chooser.Dispose(); chooser.Dispose(); layout.SetWidth("Name", 234);
            Check(chooser.MetadataRefreshCount == refreshes && !chooser.CommitWidth());
            bool rejected = false; try { chooser.SetSearch("Name"); } catch (ObjectDisposedException) { rejected = true; } Check(rejected);
        });
        return new(results.AsReadOnly(), timer.Elapsed.TotalMilliseconds);
    }
}

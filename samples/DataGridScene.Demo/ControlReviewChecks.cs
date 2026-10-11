using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using System.Diagnostics;
using System.Text.Json;

namespace DataGridScene.Demo;

/// <summary>Runs real Uno control API contracts on the application's UI thread.</summary>
internal static class ControlReviewChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var total = Stopwatch.StartNew();
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch(Exception e) { results.Add(new(name, false, watch.Elapsed.TotalMilliseconds, e.ToString())); }
        }
        void Check(bool value) { if (!value) throw new InvalidOperationException("Filter semantics changed."); }
        GridFilter? RoundTrip(GridSchema schema, GridFilter filter) => new FilterBuilder(schema, filter).BuildFilter();
        Test("filter-ui.case-insensitive-field-roundtrip", () => {
            var table = ValidationSuite.Fixture();
            var filter = RoundTrip(table.Schema, new FieldFilter("name", FilterOperator.Equal, "Alpha"));
            using var view = new GridView(table, new() { Filter = filter }); Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("filter-ui.null-string-is-not-empty-string", () => {
            var table = new GridTable(new GridSchema([new("Name", typeof(string))]));
            table.Add(new(1, new object?[] { null })); table.Add(new(2, new object?[] { "" }));
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new FieldFilter("Name", FilterOperator.Equal, null)) });
            Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("filter-ui.tiny-json-number-and-document-lifetime", () => {
            var table = new GridTable(new GridSchema([new("Value", typeof(double))]));
            table.Add(new(1, new object?[] { 0d })); table.Add(new(2, new object?[] { 1e-300 }));
            FilterBuilder builder;
            using (var json = JsonDocument.Parse("1e-300")) builder = new(table.Schema, new FieldFilter("Value", FilterOperator.Equal, json.RootElement));
            using var view = new GridView(table, new() { Filter = builder.BuildFilter() }); Check(view.FilteredRecords.Single().Id == 2);
        });
        Test("filter-ui.empty-or-remains-false", () => {
            var table = ValidationSuite.Fixture();
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new FilterGroup(false, [])) }); Check(view.Rows.Count == 0);
        });
        Test("filter-ui.negated-empty-and-remains-false", () => {
            var table = ValidationSuite.Fixture();
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new NotFilter(new FilterGroup(true, []))) }); Check(view.Rows.Count == 0);
        });
        Test("filter-ui.application-filter-is-not-silently-dropped", () => {
            try { _ = new FilterBuilder(ValidationSuite.Fixture().Schema, new ApplicationFilter()); }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException("Unsupported predicate was silently dropped.");
        });
        Test("filter-ui.text-operators-use-display-text-for-numeric-fields", () => {
            var table = new GridTable(new GridSchema([new("Value", typeof(decimal))]));
            table.Add(new(1, new object?[] { 12.5m })); table.Add(new(2, new object?[] { 21.5m }));
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new FieldFilter("Value", FilterOperator.Contains, "2.")) });
            Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("filter-ui.null-operators-ignore-unused-literals", () => {
            var table = new GridTable(new GridSchema([new("Value", typeof(int))]));
            table.Add(new(1, new object?[] { null })); table.Add(new(2, new object?[] { 1 }));
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new FieldFilter("Value", FilterOperator.IsNull, "unused text", "unused upper")) });
            Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("filter-ui.null-in-list-is-not-empty-string", () => {
            var table = new GridTable(new GridSchema([new("Name", typeof(string))]));
            table.Add(new(1, new object?[] { null })); table.Add(new(2, new object?[] { "" })); table.Add(new(3, new object?[] { "A,B" }));
            using var view = new GridView(table, new() { Filter = RoundTrip(table.Schema, new FieldFilter("Name", FilterOperator.In, Values: new object?[] { null, "A,B" })) });
            Check(view.FilteredRecords.Select(row => row.Id).SequenceEqual(new long[] { 1, 3 }));
        });
        Test("control.viewport-batch-preserves-row-height-index", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            long rows = grid.RowGeometryBuildCount, resets = grid.HeightIndex.ResetCount;
            using(grid.BeginDataUpdate()) { grid.ShowBands = true; grid.ColumnLayout!.SetWidth("Name", 240); grid.ScrollTo(30, 0); }
            Check(grid.RowGeometryBuildCount == rows && grid.HeightIndex.ResetCount == resets);
            using(grid.BeginDataUpdate()) grid.SetRowHeight("row:1", 50);
            Check(grid.RowGeometryBuildCount == rows + 1 && grid.HeightIndex[0] == 50);
        });
        return new(results.AsReadOnly(), total.Elapsed.TotalMilliseconds);
    }
    private sealed record ApplicationFilter : GridFilter
    { public override Func<GridRecord,bool> Compile(GridSchema schema) => _ => false; }
}

using DataGridScene.Core;
using DataGridScene.Layout;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Validation;

internal static class NumericValidation
{
    private static void Check(bool condition, string message = "Assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static GridTable Doubles(params double[] values)
    {
        var table = new GridTable(new GridSchema([new("Value", typeof(double))]));
        table.AddRange(values.Select((value, index) => new GridRecord(index, new object?[] { value })));
        return table;
    }
    internal static ValidationReport Append(ValidationReport previous)
    {
        var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("numeric.double-adjacent-values-ordered", () => {
            Check(GridValue.Compare(1d, Math.BitIncrement(1d)) < 0);
            Check(GridValue.Compare(1e-300, 2e-300) < 0);
            Check(GridValue.Compare(double.Epsilon, 0d) > 0);
        });
        Test("numeric.single-adjacent-values-ordered", () => {
            Check(GridValue.Compare(1f, MathF.BitIncrement(1f)) < 0);
            Check(GridValue.Compare(float.Epsilon, 0f) > 0);
        });
        Test("numeric.mixed-binary-decimal-exact-order", () => {
            Check(GridValue.Compare(0.1d, 0.1m) > 0, "Binary 0.1 is slightly above exact decimal 0.1.");
            Check(GridValue.Compare(0.1f, 0.1d) > 0);
            Check(GridValue.Compare(0.5d, 0.5m) == 0);
            Check(GridValue.Compare(1e-300, 0m) > 0);
            Check(GridValue.Compare(9007199254740993L, 9007199254740992d) > 0);
            Check(GridValue.Compare(ulong.MaxValue, (double)ulong.MaxValue) < 0);
        });
        Test("numeric.nonfinite-zero-order-and-keys", () => {
            Check(GridValue.Compare(double.NaN, double.NegativeInfinity) < 0);
            Check(GridValue.Compare(float.NaN, double.NaN) == 0);
            Check(GridValue.Compare(double.PositiveInfinity, decimal.MaxValue) > 0);
            Check(GridValue.Compare(double.NegativeInfinity, decimal.MinValue) < 0);
            Check(GridValue.Key(-0d) == GridValue.Key(0m));
            Check(GridValue.Key(float.NaN) == GridValue.Key(double.NaN));
        });
        Test("numeric.keys-preserve-binary-identity", () => {
            Check(GridValue.Key(1d) != GridValue.Key(Math.BitIncrement(1d)));
            Check(GridValue.Key(double.Epsilon) != GridValue.Key(0m));
            Check(GridValue.Key(0.1d) != GridValue.Key(0.1m));
            Check(GridValue.Key(0.1f) == GridValue.Key((double)0.1f));
            Check(GridValue.Key(0.5d) == GridValue.Key(0.5m));
            Check(GridValue.Key(12.50m) == "n:12.5", "Preserve exact legacy decimal group keys.");
        });
        Test("numeric.grouping-distinct-min-max", () => {
            using var view = new GridView(Doubles(0, double.Epsilon, 1, Math.BitIncrement(1d), -double.Epsilon));
            view.SetQuery(new() { Groups = [new("Value")], Summaries = [new("Value", SummaryKind.DistinctCount), new("Value", SummaryKind.Minimum), new("Value", SummaryKind.Maximum)] });
            Check(view.Groups.Count == 5 && Equals(view.Summaries["Value:DistinctCount"], 5));
            Check(Equals(view.Summaries["Value:Minimum"], -double.Epsilon));
            Check(Equals(view.Summaries["Value:Maximum"], Math.BitIncrement(1d)));
        });
        Test("numeric.filter-adjacent-double", () => {
            using var view = new GridView(Doubles(1, Math.BitIncrement(1d), Math.BitDecrement(1d)));
            view.SetQuery(new() { Filter = new FieldFilter("Value", FilterOperator.Greater, 1d) });
            Check(view.FilteredRecords.Count == 1 && view.FilteredRecords[0].Id == 1);
        });
        Test("numeric.binary-group-workspace-roundtrip", () => {
            using var view = new GridView(Doubles(0, double.Epsilon, 1, Math.BitIncrement(1d)));
            view.SetQuery(new() { Groups = [new("Value")] });
            var selected = view.Groups.Single(group => Equals(group.Value, double.Epsilon)); view.ToggleGroup(selected.Key);
            var document = new GridLayoutDocument { Query = view.Query, ViewState = view.CaptureState() };
            var loaded = LayoutSerializer.Load(LayoutSerializer.Save(document), view.Table.Schema);
            view.ExpandAll(); view.RestoreState(loaded.ViewState);
            Check(!view.IsExpanded(selected.Key)); Check(view.Groups.Where(group => group.Key != selected.Key).All(group => view.IsExpanded(group.Key)));
        });
        Test("numeric.typed-json-preserves-tiny-and-adjacent", () => {
            using var json = JsonDocument.Parse("[1e-300,1.0000000000000002]");
            var column = new GridColumn("Value", typeof(double));
            Check(column.TryConvert(json.RootElement[0], CultureInfo.GetCultureInfo("pl-PL"), out var tiny, out _));
            Check(Equals(tiny, 1e-300));
            Check(column.TryConvert(json.RootElement[1], CultureInfo.InvariantCulture, out var adjacent, out _));
            Check(Equals(adjacent, Math.BitIncrement(1d)));
        });
        Test("numeric.typed-json-single-does-not-double-round", () => {
            using var json = JsonDocument.Parse("1.0000000596046447753906250001");
            var column = new GridColumn("Value", typeof(float));
            Check(column.TryConvert(json.RootElement, CultureInfo.InvariantCulture, out var value, out _));
            Check(Equals(value, MathF.BitIncrement(1f)));
        });
        Test("numeric.query-retains-json-before-schema-conversion", () => {
            using var view = new GridView(Doubles(0, 1e-300, 2e-300));
            GridQuery retained;
            using (var json = JsonDocument.Parse("1e-300")) retained = QuerySnapshot.Capture(new() { Filter = new FieldFilter("Value", FilterOperator.Equal, json.RootElement) }, view.Table.Schema);
            view.SetQuery(retained);
            Check(view.FilteredRecords.Count == 1 && view.FilteredRecords[0].Id == 1);
            var saved = LayoutSerializer.Save(new() { Query = retained });
            view.SetQuery(LayoutSerializer.Load(saved, view.Table.Schema).Query);
            Check(view.FilteredRecords.Count == 1 && view.FilteredRecords[0].Id == 1);
        });
        Test("numeric.json-decimal-retains-precision", () => {
            using var json = JsonDocument.Parse("12345678901234567890.12345678");
            var column = new GridColumn("Value", typeof(decimal));
            Check(column.TryConvert(json.RootElement, CultureInfo.GetCultureInfo("de-DE"), out var value, out _));
            Check(Equals(value, 12345678901234567890.12345678m));
        });
        Test("filter.bounds-do-not-run-edit-validation", () => {
            int validations = 0;
            var table = new GridTable(new GridSchema([new("Quantity", typeof(int)) { AllowNull = false, Validator = value => { validations++; return value is int n && n >= 1 ? null : "Must be positive."; } }]));
            table.Add(new(1, new object?[] { 2 })); validations = 0;
            using var view = new GridView(table); view.SetQuery(new() { Filter = new FieldFilter("Quantity", FilterOperator.Greater, 0) });
            Check(view.FilteredRecords.Count == 1 && validations == 0);
            try { table.Apply([new(1, "Quantity", 0)]); throw new InvalidOperationException("Edit validation was bypassed."); }
            catch (GridValidationException) { Check(validations == 1); }
        });
        Test("filter.required-string-can-compare-with-empty", () => {
            var table = new GridTable(new GridSchema([new("Name", typeof(string)) { AllowNull = false }]));
            table.Add(new(1, new object?[] { "Alpha" })); using var view = new GridView(table);
            view.SetQuery(new() { Filter = new FieldFilter("Name", FilterOperator.NotEqual, "") }); Check(view.FilteredRecords.Count == 1);
            view.SetQuery(new() { Filter = new FieldFilter("Name", FilterOperator.Equal, null) }); Check(view.FilteredRecords.Count == 0);
        });
        Test("numeric.key-equality-matches-cross-type-comparison", () => {
            object[] values = [0, -0d, 0m, 1, 1f, 1d, 1m, 0.5m, 0.5d, 0.1m, 0.1d, 0.1f, (double)0.1f, 1e-300, double.Epsilon, Math.BitIncrement(1d), decimal.MaxValue, ulong.MaxValue, (double)ulong.MaxValue, double.NaN, float.NaN, double.PositiveInfinity, float.PositiveInfinity];
            foreach (var a in values) foreach (var b in values) {
                int comparison = Math.Sign(GridValue.Compare(a, b));
                Check(comparison == -Math.Sign(GridValue.Compare(b, a)), "Comparison must be antisymmetric.");
                Check((comparison == 0) == (GridValue.Key(a) == GridValue.Key(b)), "Grouping and ordering must use the same equality.");
            }
        });
        return new(results.AsReadOnly(), previous.Milliseconds + clock.Elapsed.TotalMilliseconds);
    }
}

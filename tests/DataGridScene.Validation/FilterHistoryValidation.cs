using DataGridScene.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Validation;

public static class FilterHistoryValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new("filter-history." + name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("filter-history." + name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        void Check(bool value) { if (!value) throw new InvalidOperationException("Filter history contract failed."); }
        void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        GridSchema Schema() => new([new("Name", typeof(string)), new("Amount", typeof(decimal)), new("Key", typeof(long)), new("Stamp", typeof(DateTimeOffset))], CultureInfo.GetCultureInfo("pl-PL"));
        Test("bounded-mru-retains-identity-and-immutable-lists", () => {
            var schema = Schema(); var history = new GridFilterHistory(schema, 2);
            GridFilterState State(string name) => GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, name));
            var first = history.Remember(State("A"))!; var saved = history.Entries;
            history.Remember(State("B")); long revision = history.Revision;
            Check(history.Remember(State("A"))!.Id == first.Id && history.Revision == revision + 1);
            revision = history.Revision; var same = history.Entries; history.Remember(State("A"));
            Check(history.Revision == revision && ReferenceEquals(same, history.Entries));
            history.Remember(State("C")); Check(history.Entries.Count == 2 && history.Entries[1].Id == first.Id && saved.Count == 1);
        });
        Test("capacity-zero-removal-clear-and-range-checks", () => {
            var schema = Schema(); var history = new GridFilterHistory(schema);
            var state = GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, "A"));
            long id = history.Remember(state)!.Id; Check(!history.Remove(id + 1)); Check(history.Remove(id));
            history.Remember(state); history.Capacity = 0; Check(history.Entries.Count == 0 && history.Remember(state) is null);
            Reject<ArgumentOutOfRangeException>(() => history.Capacity = 101); Check(history.Capacity == 0);
            Reject<ArgumentOutOfRangeException>(() => history.Capacity = -1);
            long revision = history.Revision; history.Clear(); Check(history.Revision == revision);
        });
        Test("empty-filter-does-not-consume-history", () => {
            var schema = Schema(); var history = new GridFilterHistory(schema);
            Check(history.Remember(GridFilterState.Capture(schema)) is null && history.Revision == 0);
            Check(history.Remember(GridFilterState.Capture(schema, textFilters: new Dictionary<string, string> { ["Name"] = "" })) is null);
        });
        Test("schemas-and-replacement-history-identities-are-isolated", () => {
            var schema = Schema(); var state = GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, "A"));
            var a = new GridFilterHistory(schema); var b = new GridFilterHistory(schema);
            Check(a.Remember(state)!.Id != b.Remember(state)!.Id);
            Reject<ArgumentException>(() => new GridFilterHistory(Schema()).Remember(state));
        });
        Test("owned-criteria-and-value-collections", () => {
            var schema = Schema(); var children = new List<GridFilter> { new FieldFilter("Amount", FilterOperator.Greater, 10m) };
            var values = new List<object?> { "A", null, "" }; var texts = new Dictionary<string, string> { ["Key"] = ">5" };
            var checkedValues = new Dictionary<string, ColumnValueFilter> { ["Name"] = new(values, true) };
            var state = GridFilterState.Capture(schema, new FilterGroup(false, children), texts, checkedValues);
            children.Clear(); values.Clear(); texts.Clear(); checkedValues.Clear();
            Check(((FilterGroup)state.Criteria!).Children.Count == 1 && state.TextFilters["Key"] == ">5" && state.ValueFilters["Name"].Values.Count == 3);
            Reject<NotSupportedException>(() => ((IList<object?>)state.ValueFilters["Name"].Values).Clear());
        });
        Test("structural-duplicates-ignore-map-order-and-field-case", () => {
            var schema = Schema();
            var a = GridFilterState.Capture(schema, textFilters: new Dictionary<string, string> { ["name"] = "A", ["Key"] = "=7" });
            var b = GridFilterState.Capture(schema, textFilters: new Dictionary<string, string> { ["key"] = "=7", ["Name"] = "A" });
            Check(a.HasSameConditions(b));
            var history = new GridFilterHistory(schema); history.Remember(a); history.Remember(b); Check(history.Entries.Count == 1);
        });
        Test("exact-decimal-int64-and-offset-replay", () => {
            var schema = Schema(); var stamp = DateTimeOffset.Parse("2026-10-13T10:20:30.1234567+02:00", CultureInfo.InvariantCulture);
            var table = new GridTable(schema); table.Add(new(1, new object?[] { "A", 1.2345678901234567890123456789m, 9007199254740993L, stamp }));
            table.Add(new(2, new object?[] { "A", 1.2345678901234567890123456788m, 9007199254740992L, stamp }));
            var state = GridFilterState.Capture(schema, GridFilter.And(new FieldFilter("Amount", FilterOperator.Equal, 1.2345678901234567890123456789m), new FieldFilter("Key", FilterOperator.Equal, 9007199254740993L)),
                valueFilters: new Dictionary<string, ColumnValueFilter> { ["Stamp"] = new(new object?[] { stamp }) });
            using var view = new GridView(table, new GridQuery { Filter = state.EffectiveFilter });
            Check(view.FilteredRecords.Count == 1 && view.FilteredRecords[0].Id == 1);
            Check(((DateTimeOffset)state.ValueFilters["Stamp"].Values[0]!).Offset == TimeSpan.FromHours(2));
        });
        Test("json-literals-survive-document-disposal", () => {
            var schema = Schema(); GridFilterState state;
            using (var document = JsonDocument.Parse("[9007199254740993,1.2345678901234567890123456789]"))
                state = GridFilterState.Capture(schema, GridFilter.And(new FieldFilter("Key", FilterOperator.Equal, document.RootElement[0]), new FieldFilter("Amount", FilterOperator.Equal, document.RootElement[1])));
            var owned = (FilterGroup)state.Criteria!;
            Check(((JsonElement)((FieldFilter)owned.Children[0]).Value!).GetInt64() == 9007199254740993L);
            _ = state.EffectiveFilter!.Compile(schema);
        });
        Test("null-empty-exclude-and-empty-boolean-groups-remain-distinct", () => {
            var schema = Schema(); var history = new GridFilterHistory(schema);
            foreach (object? value in new object?[] { null, "" }) history.Remember(GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, value)));
            history.Remember(GridFilterState.Capture(schema, new FilterGroup(false, [])));
            history.Remember(GridFilterState.Capture(schema, new NotFilter(new FilterGroup(true, []))));
            Check(history.Entries.Count == 4 && !history.Entries[0].State.EffectiveFilter!.Compile(schema)(new GridRecord(1, new object?[4])));
        });
        Test("floating-point-bits-decimal-scale-and-offsets-are-not-conflated", () => {
            var schema = new GridSchema([new("Value", typeof(object))]); var history = new GridFilterHistory(schema);
            foreach (object value in new object[] { 0d, -0d, double.Epsilon, 1.0m, 1.00m, 1L, 1d,
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), DateTimeOffset.Parse("2026-01-01T02:00:00+02:00") })
                history.Remember(GridFilterState.Capture(schema, new FieldFilter("Value", FilterOperator.Equal, value)));
            Check(history.Entries.Count == 9);
        });
        Test("capture-does-not-read-records-compile-predicates-or-compare-values", () => {
            int callbacks = 0;
            var schema = new GridSchema([new("Value", typeof(int)) { ValueGetter = _ => { callbacks++; return 1; }, Comparer = Comparer<object?>.Create((_, _) => { callbacks++; return 0; }), Validator = _ => { callbacks++; return null; } }]);
            var state = GridFilterState.Capture(schema, new FieldFilter("Value", FilterOperator.In, Values: new object?[] { 2, 1 }));
            var history = new GridFilterHistory(schema); history.Remember(state); history.Remember(state); Check(callbacks == 0);
        });
        Test("custom-predicates-and-mutable-literals-are-explicitly-rejected", () => {
            var schema = Schema(); Reject<NotSupportedException>(() => GridFilterState.Capture(schema, new ApplicationFilter()));
            Reject<NotSupportedException>(() => GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, new object())));
        });
        Test("snapshot-size-limits-fail-before-history-changes", () => {
            var schema = Schema(); var history = new GridFilterHistory(schema); var state = GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, "A")); history.Remember(state);
            Reject<ArgumentException>(() => history.Remember(GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Equal, new string('X', GridFilterState.MaximumTextLength)))));
            GridFilter filter = new FieldFilter("Name", FilterOperator.Equal, "A"); for (int i = 0; i < 34; i++) filter = new NotFilter(filter);
            Reject<ArgumentException>(() => GridFilterState.Capture(schema, filter));
            Check(history.Entries.Count == 1 && history.Entries[0].State.HasSameConditions(state));
        });
        Test("unknown-duplicate-and-overlapping-fields-are-rejected", () => {
            var schema = Schema(); Reject<ArgumentException>(() => GridFilterState.Capture(schema, new FieldFilter("Missing", FilterOperator.Equal, 1)));
            Reject<ArgumentException>(() => GridFilterState.Capture(schema, textFilters: new Dictionary<string, string> { ["Name"] = "A", ["name"] = "B" }));
            Reject<ArgumentException>(() => GridFilterState.Capture(schema, textFilters: new Dictionary<string, string> { ["Name"] = "A" }, valueFilters: new Dictionary<string, ColumnValueFilter> { ["Name"] = new(new object?[] { "A" }) }));
        });
        Test("filter-comparison-retains-boolean-structure-and-precision", () => {
            var a = new FieldFilter("Amount", FilterOperator.Equal, 1.0000000000000000000000000001m);
            Check(GridFilterState.FiltersEqual(a, GridFilter.And(a with { Field = "amount" })));
            Check(!GridFilterState.FiltersEqual(a, a with { Value = 1m }));
            Check(!GridFilterState.FiltersEqual(new FilterGroup(true, []), new FilterGroup(false, [])));
            Check(!GridFilterState.FiltersEqual(a, new NotFilter(a)));
        });
        Test("filter-only-snapshot-never-owns-records-or-query-options", () => {
            var schema = Schema(); var state = GridFilterState.Capture(schema, new FieldFilter("Name", FilterOperator.Contains, "A"));
            var query = new GridQuery { Search = "independent", Sorts = [new("Key", SortDirection.Descending)], Groups = [new("Name")] };
            var next = query with { Filter = state.EffectiveFilter, IsFilterEnabled = true };
            Check(next.Search == query.Search && ReferenceEquals(next.Sorts, query.Sorts) && ReferenceEquals(next.Groups, query.Groups));
        });
        return results.AsReadOnly();
    }
    private sealed record ApplicationFilter : GridFilter
    {
        public override Func<GridRecord, bool> Compile(GridSchema schema) => throw new InvalidOperationException("Capture must not compile application predicates.");
    }
}

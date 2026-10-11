using DataGridScene.Core;
using DataGridScene.Layout;
using System.Diagnostics;
using System.Text.Json;

namespace DataGridScene.Validation;

public static class ValueFilterValidation
{
    private static void Check(bool test, string message = "Assertion failed.") { if (!test) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var previous = await UpdateValidationSuite.RunAsync(stress); var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        GridTable Strings() { var table = new GridTable(new GridSchema([new("Name", typeof(string))]));
            table.AddRange(new object?[] { null, "", "Alpha", "alpha", "A,B", "Beta" }.Select((v,i) => new GridRecord(i, new[] { v }))); return table; }
        Test("value-filter.include-and-exclude", () => {
            using var view = new GridView(Strings());
            view.SetQuery(new() { Filter = new ColumnValueFilter(["Alpha", "Beta"]).ToFilter("Name") }); Check(view.FilteredRecords.Count == 3);
            view.SetQuery(new() { Filter = new ColumnValueFilter(["Alpha", "Beta"], true).ToFilter("Name") }); Check(view.FilteredRecords.Count == 3 && view.FilteredRecords[0].Id == 0);
        });
        Test("value-filter.all-none-boolean-identities", () => {
            using var view = new GridView(Strings()); view.SetQuery(new() { Filter = ColumnValueFilter.None.ToFilter("Name") }); Check(view.Rows.Count == 0);
            view.SetQuery(new() { Filter = ColumnValueFilter.All.ToFilter("Name") }); Check(view.Rows.Count == 6);
        });
        Test("value-filter.null-and-empty-remain-distinct", () => {
            using var view = new GridView(Strings()); view.SetQuery(new() { Filter = new ColumnValueFilter([null]).ToFilter("Name") }); Check(view.FilteredRecords.Single().Id == 0);
            view.SetQuery(new() { Filter = new ColumnValueFilter([""]).ToFilter("Name") }); Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("value-filter.capture-canonicalizes-and-owns-values", () => {
            var table = Strings(); var list = new List<object?> { "Alpha", "alpha" }; var owned = new ColumnValueFilter(list).Capture(table.Schema, "name");
            list.Clear(); Check(owned.Values.Count == 1); Reject<NotSupportedException>(() => ((IList<object?>)owned.Values).Clear());
        });
        Test("value-filter.custom-comparer-used-by-selection-and-predicate", () => {
            var schema = new GridSchema([new("Name", typeof(string)) { Comparer = Comparer<object?>.Create((a,b) => StringComparer.Ordinal.Compare((string?)a,(string?)b)) }]);
            var selection = new ColumnValueSelection(schema, "Name"); selection.SelectAll(false); selection.SetSelected(["A"],true);
            Check(selection.IsSelected("A") && !selection.IsSelected("a"));
            var table = new GridTable(schema); table.Add(new(1,["A"]));table.Add(new(2,["a"]));
            using var view = new GridView(table,new() { Filter = selection.Capture().ToFilter("Name") });Check(view.FilteredRecords.Single().Id==1);
        });
        Test("value-filter.staged-selection-does-not-mutate-initial", () => {
            var table = Strings(); var initial = new ColumnValueFilter(new List<object?> { "Alpha" });
            var selected = new ColumnValueSelection(table.Schema,"Name",initial); selected.SetSelected(["Beta"],true);Check(initial.Values.Count==1 && selected.Capture().Values.Count==2);
        });
        Test("value-filter.exclusion-preserves-unseen-values", () => {
            var selected = new ColumnValueSelection(Strings().Schema,"Name"); selected.SetSelected(["Alpha"],false);
            Check(!selected.IsSelected("alpha") && selected.IsSelected("Newly added later")); selected.SetSelected(["alpha"],true);Check(selected.Capture().ToFilter("Name") is null);
        });
        Test("value-filter.limits-reject-atomically", () => {
            var selected = new ColumnValueSelection(Strings().Schema,"Name");selected.SelectAll(false);selected.SetSelected(["Original"],true);
            Reject<InvalidOperationException>(()=>selected.SetSelected(Enumerable.Range(0,1025).Select(i=>(object?)i.ToString()),true));Check(selected.ExceptionCount==1 && selected.IsSelected("Original"));
        });
        Test("value-filter.convert-json-number-before-disposal", () => {
            var schema = new GridSchema([new("Value",typeof(double))]);ColumnValueFilter owned;
            using(var json=JsonDocument.Parse("1e-300")) owned = new ColumnValueFilter([json.RootElement]).Capture(schema,"Value");
            Check(Equals(owned.Values.Single(),1e-300));
        });
        Test("value-filter.layout-roundtrip-all-filter-layers", () => {
            var table=ValidationSuite.Fixture();var document=new GridLayoutDocument { Query=new(){Filter=new FieldFilter("Amount",FilterOperator.Greater,5)},
                FilterTexts=new Dictionary<string,string>{{"Region","West"}}, ValueFilters=new Dictionary<string,ColumnValueFilter>{{"Name",new(["Alpha","Beta"])}} };
            var restored=LayoutSerializer.Load(LayoutSerializer.Save(document),table.Schema);using var view=new GridView(table,restored.EffectiveQuery());Check(view.FilteredRecords.Single().Id==1);
            Check(restored.ValueFilters["Name"].Values.Count==2 && restored.FilterTexts["Region"]=="West");
        });
        Test("value-filter.layout-preserves-none-and-exclusion", () => {
            var table=Strings(); foreach(var filter in new[]{ColumnValueFilter.None,new ColumnValueFilter([null,""],true)}) {
                var restored=LayoutSerializer.Load(LayoutSerializer.Save(new(){ValueFilters=new Dictionary<string,ColumnValueFilter>{{"Name",filter}}}),table.Schema);
                using var view=new GridView(table,restored.EffectiveQuery());Check(view.Rows.Count==(filter.Exclude?4:0)); }
        });
        Test("value-filter.layout-rejects-invalid-fields-and-collections", () => {
            var schema=Strings().Schema;
            foreach(var json in new[]{"{\"valueFilters\":null}","{\"valueFilters\":{\"Missing\":{\"values\":[]}}}","{\"valueFilters\":{\"Name\":null}}","{\"valueFilters\":{\"Name\":{\"values\":[]},\"name\":{\"values\":[]}}}"})
                Reject<FormatException>(()=>LayoutSerializer.Load(json,schema));
        });
        Test("distinct.null-empty-and-punctuation-labels", () => {
            var values=GridDistinctValues.Collect(Strings(),"Name");Check(values.Items.Count==5 && !values.HasMore);
            Check(values.Items[0].Label=="(Blanks)" && values.Items[1].Label=="(Empty string)" && values.Items.Any(v=>v.Label=="A,B"));
        });
        Test("distinct.search-beyond-initial-limit", () => {
            var table=new GridTable(new GridSchema([new("Value",typeof(int))]));table.AddRange(Enumerable.Range(0,10000).Select(i=>new GridRecord(i,[i])));
            var first=GridDistinctValues.Collect(table,"Value",limit:10);Check(first.HasMore && first.RecordsExamined==11);
            var last=GridDistinctValues.Collect(table,"Value","9999",10);Check(last.Items.Single().Value!.Equals(9999) && !last.HasMore);
        });
        Test("distinct.exact-limit-is-not-truncation", () => {
            var table=Strings();var values=GridDistinctValues.Collect(table,"Name",limit:5);Check(!values.HasMore && values.RecordsExamined==6);
        });
        Test("distinct.bounds-and-cancellation", () => {
            var table=Strings();Reject<ArgumentOutOfRangeException>(()=>GridDistinctValues.Collect(table,"Name",limit:0));
            using var cancel=new CancellationTokenSource();cancel.Cancel();Reject<OperationCanceledException>(()=>GridDistinctValues.Collect(table,"Name",cancellationToken:cancel.Token));
        });
        Test("distinct.custom-equality-and-order", () => {
            var table=new GridTable(new GridSchema([new("Value",typeof(int)){Comparer=Comparer<object?>.Create((a,b)=>((int)a!%10).CompareTo((int)b!%10))}]));
            table.AddRange(new[]{12,11,22,21}.Select(i=>new GridRecord(i,[i])));var result=GridDistinctValues.Collect(table,"Value");Check(result.Items.Select(v=>v.Value).SequenceEqual(new object?[]{11,12}));
        });
        Test("distinct.bounded-getter-calls-and-retained-items", () => {
            int calls=0;var table=new GridTable(new GridSchema([new("Value",typeof(long)){ValueGetter=row=>{calls++;return row.Id;}}]));
            table.AddRange(Enumerable.Range(0,100000).Select(i=>new GridRecord(i,[null])));var result=GridDistinctValues.Collect(table,"Value",limit:100);
            Check(calls==101 && result.Items.Count==100 && result.HasMore,"A small popup must not evaluate the entire unique domain.");
        });
        Test("membership.getter-once-per-record", () => {
            int calls=0;var table=new GridTable(new GridSchema([new("Value",typeof(int)){ValueGetter=row=>{calls++;return (int)row.Id;}}]));
            table.AddRange(Enumerable.Range(0,1000).Select(i=>new GridRecord(i,[null])));
            var predicate=new FieldFilter("Value",FilterOperator.In,Values:Enumerable.Range(0,100).Select(i=>(object?)i).ToArray()).Compile(table.Schema);
            Check(table.Records.Count(row=>predicate(row))==100 && calls==1000);
        });
        Test("membership.empty-list-never-evaluates-getter", () => {
            var schema=new GridSchema([new("Value",typeof(int)){ValueGetter=_=>throw new Exception("No membership candidates")}]);
            Check(!new FieldFilter("Value",FilterOperator.In).Compile(schema)(new(0,[null])));
        });
        Test("membership.comparisons-are-logarithmic", () => {
            int comparisons=0;var schema=new GridSchema([new("Value",typeof(int)){Comparer=Comparer<object?>.Create((a,b)=>{comparisons++;return GridValue.Compare(a,b);})}]);
            var table=new GridTable(schema);table.AddRange(Enumerable.Range(0,1000).Select(i=>new GridRecord(i,[i])));
            var predicate=new FieldFilter("Value",FilterOperator.In,Values:Enumerable.Range(0,1024).Select(i=>(object?)(i*2)).ToArray()).Compile(schema);
            comparisons=0;Check(table.Records.Count(row=>predicate(row))==500);Check(comparisons<20000,$"Too many comparisons: {comparisons}");
        });
        return new(results.AsReadOnly(),previous.Milliseconds+clock.Elapsed.TotalMilliseconds);
    }
}

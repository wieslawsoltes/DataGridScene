using DataGridScene.Core;
using DataGridScene.Data;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Validation;

public static class FilterPresentationValidationSuite
{
    private static void Check(bool value, string message = "Assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var previous = await ValueFilterValidation.RunAsync(stress); var watch = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var start = Stopwatch.GetTimestamp();
            try { action(); results.Add(new(name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        Test("filter-panel.suspension-retains-expression-and-search", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            var filter = new FieldFilter("Name", FilterOperator.Equal, "Alpha");
            view.SetQuery(new() { Filter = filter, Search = "Beta", IsFilterEnabled = false });
            Check(view.FilteredRecords.Single().Id == 2 && view.Query.Filter is FieldFilter);
            view.SetQuery(view.Query with { IsFilterEnabled = true }); Check(view.Rows.Count == 0);
            view.SetQuery(view.Query with { Search = "" }); Check(view.FilteredRecords.Single().Id == 1);
        });
        Test("filter-panel.suspension-recalculates-groups-and-totals", () => {
            using var view = new GridView(ValidationSuite.Fixture(), new() { Filter = new FieldFilter("Name", FilterOperator.Equal, "Alpha"), Groups = [new("Region")], Summaries = [new("Amount", SummaryKind.Sum)] });
            Check(view.FilteredRecords.Count == 1 && Equals(view.Summaries["Amount:Sum"], 10m));
            view.SetQuery(view.Query with { IsFilterEnabled = false });
            Check(view.FilteredRecords.Count == 4 && Equals(view.Summaries["Amount:Sum"], 60m) && view.Groups.Count == 3);
        });
        Test("filter-panel.suspended-filter-has-no-value-dependency", () => {
            using var view = new GridView(ValidationSuite.Fixture(), new() { Filter = new FieldFilter("Name", FilterOperator.Equal, "Alpha"), IsFilterEnabled = false });
            var rows = view.Rows; long refresh = view.RefreshCount;
            view.Table.Apply([new(1,"Name","Changed")]);
            Check(ReferenceEquals(rows,view.Rows) && refresh == view.RefreshCount);
            view.SetQuery(view.Query with { IsFilterEnabled = true }); Check(view.FilteredRecords.Count == 0 && view.RefreshCount == refresh + 1);
        });
        Test("filter-panel.disabled-expression-is-still-validated", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var rows = view.Rows;
            Reject<ArgumentException>(() => view.SetQuery(new() { Filter = new FieldFilter("Missing",FilterOperator.Equal,0), IsFilterEnabled = false }));
            Check(ReferenceEquals(rows,view.Rows));
        });
        Test("filter-panel.disabled-filter-roundtrip-requires-v4", () => {
            var schema=ValidationSuite.Fixture().Schema;
            string json=LayoutSerializer.Save(new() { Query=new() { Filter=new FieldFilter("Name",FilterOperator.Equal,"Alpha"), IsFilterEnabled=false } });
            var loaded=LayoutSerializer.Load(json,schema); Check(loaded.Version==4 && !loaded.Query.IsFilterEnabled && loaded.Query.Filter is not null);
            Reject<FormatException>(()=>LayoutSerializer.Load(json.Replace("\"version\": 4","\"version\": 2"),schema));
        });
        Test("filter-panel.enabled-layout-retains-compatible-version", () => {
            var schema=ValidationSuite.Fixture().Schema;
            Check(LayoutSerializer.Load(LayoutSerializer.Save(new()),schema).Version==2);
            foreach(int version in new[]{1,2,3}) Check(LayoutSerializer.Load($"{{\"version\":{version}}}",schema).Query.IsFilterEnabled);
        });
        Test("filter-panel.checked-values-and-text-survive-disabled-layout", () => {
            var table=ValidationSuite.Fixture();
            var saved=new GridLayoutDocument { Query=new() { IsFilterEnabled=false }, FilterTexts=new Dictionary<string,string> { ["Region"]="West" },
                ValueFilters=new Dictionary<string,ColumnValueFilter> { ["Name"]=new(new object?[]{"Alpha"},false) } };
            var loaded=LayoutSerializer.Load(LayoutSerializer.Save(saved),table.Schema);
            using var view=new GridView(table,loaded.EffectiveQuery()); Check(view.Rows.Count==4);
            view.SetQuery(view.Query with { IsFilterEnabled=true });Check(view.FilteredRecords.Single().Id==1);
        });
        Test("filter-panel.suspended-page-source-still-honors-search", () => {
            var source=new LocalPageSource(ValidationSuite.Fixture());
            var page=source.FetchAsync(new() { Filter=new FieldFilter("Name",FilterOperator.Equal,"Alpha"),IsFilterEnabled=false,Search="Beta" },0,10,CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Records.Single().Id==2);
        });
        Test("filter-panel.description-is-bounded", () => {
            var schema=ValidationSuite.Fixture().Schema;
            var filter=new FieldFilter("Name",FilterOperator.In,Values:Enumerable.Repeat<object?>(new string('x',10000),1024).ToArray());
            string text=GridFilterText.Describe(filter,schema,80);Check(text.Length==80 && text.EndsWith('…'));
            Reject<ArgumentOutOfRangeException>(()=>GridFilterText.Describe(filter,schema,10));
        });
        Test("filter-panel.description-does-not-evaluate-getters", () => {
            int calls=0;var schema=new GridSchema([new("Name",typeof(string)){ValueGetter=_=>{calls++;throw new Exception();}}]);
            Check(GridFilterText.Describe(new FieldFilter("Name",FilterOperator.Equal,"Alpha"),schema)=="Name = \"Alpha\"" && calls==0);
        });
        Test("filter-panel.description-null-empty-and-control-characters", () => {
            var schema=ValidationSuite.Fixture().Schema;
            Check(GridFilterText.Describe(new FieldFilter("Name",FilterOperator.Equal,null),schema).Contains("(Blanks)"));
            Check(GridFilterText.Describe(new FieldFilter("Name",FilterOperator.Equal,""),schema).EndsWith("\"\""));
            string text=GridFilterText.Describe(new FieldFilter("Name",FilterOperator.Equal,"A\nB\tC\""),schema);
            Check(!text.Contains('\n') && !text.Contains('\t') && text.Contains("\\n") && text.Contains("\\\""));
        });
        Test("filter-panel.description-boolean-groups-are-not-flattened", () => {
            var schema=ValidationSuite.Fixture().Schema;
            var filter=new NotFilter(GridFilter.Or(new FieldFilter("Name",FilterOperator.Equal,"Alpha"),new FieldFilter("Amount",FilterOperator.Greater,20)));
            string text=GridFilterText.Describe(filter,schema); Check(text.StartsWith("NOT ((") && text.Contains(" OR ") && text.EndsWith("))"));
            Check(GridFilterText.Describe(new FilterGroup(false,[]),schema)=="Always false");
            Check(GridFilterText.Describe(new FilterGroup(true,[]),schema)=="Always true");
        });
        Test("filter-panel.description-json-values-remain-typed", () => {
            using var json=JsonDocument.Parse("1e-300");var schema=new GridSchema([new("Value",typeof(double))]);
            string text=GridFilterText.Describe(new FieldFilter("Value",FilterOperator.Equal,json.RootElement),schema);
            Check(text.Contains("1E-300",StringComparison.OrdinalIgnoreCase));
        });
        Test("filter-panel.description-culture-and-caption", () => {
            var schema=new GridSchema([new("Amount",typeof(decimal),"Net amount")],CultureInfo.GetCultureInfo("pl-PL"));
            Check(GridFilterText.Describe(new FieldFilter("amount",FilterOperator.Between,1.25m,2.5m),schema)=="Net amount between 1,25 and 2,5");
        });
        Test("filter-panel.description-custom-predicate-is-explicit", () => {
            Check(GridFilterText.Describe(new CustomFilter(),ValidationSuite.Fixture().Schema)=="Application-defined condition");
        });
        Test("filter-panel.suspension-batch-publishes-once", () => {
            using var view=new GridView(ValidationSuite.Fixture(),new(){Filter=new FieldFilter("Name",FilterOperator.Equal,"Alpha")});
            int events=0;view.Changed+=(_,_)=>events++;long builds=view.RefreshCount;
            using(view.DeferRefresh()) {view.SetQuery(view.RequestedQuery with {IsFilterEnabled=false});view.SetQuery(view.RequestedQuery with {Search="Beta"});}
            Check(events==1 && view.RefreshCount==builds+1 && view.FilteredRecords.Single().Id==2);
        });
        Test("chrome.sort-glyph-direction-and-retained-paths", () => {
            var table=new GridTable(new GridSchema([new("Value",typeof(int))]));table.Add(new(1,new object?[]{1}));using var view=new GridView(table);
            var layout=new GridLayout(table.Schema);var heights=new RowHeightIndex(1);var selection=new GridSelection();using var renderer=new GridCanvasRenderer();
            using var surface=SKSurface.Create(new SKImageInfo(300,220));
            byte[] Render(SortDirection direction) {view.SetQuery(new(){Sorts=[new("Value",direction)]});renderer.Draw(surface.Canvas,new(view,layout,selection,heights,new(),300,220,0,0));using var image=surface.Snapshot();using var bytes=image.Encode();return bytes.ToArray();}
            byte[] asc=Render(SortDirection.Ascending),desc=Render(SortDirection.Descending);Check(!asc.SequenceEqual(desc));
            Check(renderer.RetainedGlyphPaths==4);for(int i=0;i<50;i++)Render(SortDirection.Ascending);
            Check(renderer.RetainedGlyphPaths==4 && asc.SequenceEqual(Render(SortDirection.Ascending)));
        });
        Test("chrome.hover-repaints-without-mutating-view", () => {
            using var view=new GridView(ValidationSuite.Fixture());var rows=view.Rows;long builds=view.RefreshCount;
            var layout=new GridLayout(view.Table.Schema);var heights=new RowHeightIndex(4);var frame=new GridFrame(view,layout,new(),heights,new(),700,400,0,0);
            using var renderer=new GridCanvasRenderer();using var surface=SKSurface.Create(new SKImageInfo(700,400));
            renderer.HoveredColumn="Name";renderer.HoveredRecordId=1;renderer.Draw(surface.Canvas,frame);
            Check(ReferenceEquals(rows,view.Rows)&&builds==view.RefreshCount&&renderer.CellsDrawn>0);
            renderer.HoveredColumn=null;renderer.HoveredRecordId=null;renderer.Draw(surface.Canvas,frame);Check(renderer.RetainedGlyphPaths==4);
        });
        return new(results.AsReadOnly(),previous.Milliseconds+watch.Elapsed.TotalMilliseconds);
    }
    private sealed record CustomFilter : GridFilter { public override Func<GridRecord,bool> Compile(GridSchema schema)=>_=>true; }
}

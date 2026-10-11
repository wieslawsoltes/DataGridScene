using DataGridScene.Core;
using DataGridScene.Data;
using DataGridScene.Layout;
using DataGridScene.Export;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace DataGridScene.Validation;

public sealed record ValidationResult(string Name, bool Passed, double Milliseconds, string? Error);
public sealed record ValidationReport(IReadOnlyList<ValidationResult> Results, double Milliseconds)
{
    public bool Passed => Results.All(result => result.Passed);
    public int PassedCount => Results.Count(result => result.Passed);
}

/// <summary>The identical non-UI suite runs on .NET and inside the browser's actual WASM runtime.</summary>
public static class ValidationSuite
{
    public static GridTable Fixture()
    {
        var schema = new GridSchema([
            new("Code", typeof(int)) { ReadOnly = true, AllowNull = false, Width = 80 },
            new("Name", typeof(string)) { AllowNull = false, Width = 180 },
            new("Region", typeof(string)),
            new("Amount", typeof(decimal)) { Validator = value => value is decimal amount && amount < 0 ? "Amount must be nonnegative." : null, Format = "N2" },
            new("Active", typeof(bool)),
            new("Date", typeof(DateTime)) { Format = "yyyy-MM-dd" }
        ]);
        var table = new GridTable(schema);
        table.AddRange([
            new(1, new object?[] { 1, "Alpha", "West", 10m, true, new DateTime(2026, 1, 10) }),
            new(2, new object?[] { 2, "Beta", "East", 20m, false, new DateTime(2026, 2, 10) }),
            new(3, new object?[] { 3, "Alpine", "West", 30m, true, new DateTime(2025, 1, 10) }),
            new(4, new object?[] { 4, "Delta", null, null, false, null })
        ]);
        return table;
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, received {actual}."); }
    private static void True(bool value, string message = "Assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance = 0.00001) { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected}, received {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static string Ids(GridView view) => string.Join(",", view.FilteredRecords.Select(record => record.Id));

    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var results = new List<ValidationResult>(); var total = Stopwatch.StartNew();
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception ex) { results.Add(new(name, false, watch.Elapsed.TotalMilliseconds, ex.ToString())); }
        }
        async Task TestAsync(string name, Func<Task> action)
        {
            var watch = Stopwatch.StartNew();
            try { await action(); results.Add(new(name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception ex) { results.Add(new(name, false, watch.Elapsed.TotalMilliseconds, ex.ToString())); }
        }
        Test("schema.duplicate-fields", () => Throws<ArgumentException>(() => new GridSchema([new("X", typeof(int)), new("x", typeof(int))])));
        Test("schema.column-ownership", () => { var column = new GridColumn("X", typeof(int)); _ = new GridSchema([column]); Throws<ArgumentException>(() => new GridSchema([column])); });
        Test("schema.nonfinite-width", () => Throws<ArgumentException>(() => new GridSchema([new("X", typeof(int)) { Width = double.NaN }])));
        Test("schema.culture-aware-decimal", () => { var column = new GridColumn("X", typeof(decimal)); True(column.TryConvert("12,50", CultureInfo.GetCultureInfo("pl-PL"), out var value, out _)); Equal(12.5m, value); });
        Test("schema.reject-fractional-integer", () => { var column = new GridColumn("X", typeof(int)); True(!column.TryConvert(1.5m, CultureInfo.InvariantCulture, out _, out _)); });
        Test("schema.boolean-conversion", () => { var column = new GridColumn("X", typeof(bool)); True(column.TryConvert("1", CultureInfo.InvariantCulture, out var value, out _)); Equal(true, value); });
        Test("schema.computed-column", () => { var schema = new GridSchema([new("X", typeof(int)), new("Double", typeof(int)) { ValueGetter = row => (int)row[0]! * 2 }]); var table = new GridTable(schema); table.Add(new(1, new object?[] { 7, null })); Equal(14, schema["Double"].GetValue(table[1])); Throws<GridValidationException>(() => table.Apply([new(1, "Double", 9)])); });
        Test("table.atomic-add-range", () => { var table = Fixture(); Throws<ArgumentException>(() => table.AddRange([new(8, new object?[] { 8, "New", null, 1m, true, null }), new(1, new object?[] { 1, "Duplicate", null, 1m, true, null })])); Equal(4, table.Records.Count); });
        Test("table.required-field", () => { var table = Fixture(); Throws<GridValidationException>(() => table.Apply([new(1, "Name", "  ")])); Equal("Alpha", table[1][1]); });
        Test("table.batch-notifications", () => { var table = Fixture(); int count = 0; table.Changed += (_, _) => count++; using (table.BeginUpdate()) { table.Apply([new(1, "Name", "A")]); table.Apply([new(2, "Name", "B")]); } Equal(1, count); });
        Test("table.remove-missing-is-atomic", () => { var table = Fixture(); Throws<KeyNotFoundException>(() => table.Remove([1, 99])); Equal(4, table.Records.Count); });
        Test("query.multisort-null-stable", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Sorts = [new("Region"), new("Amount", SortDirection.Descending)] }); Equal("4,2,3,1", Ids(view)); });
        Test("query.stable-equal-values", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Sorts = [new("Region")] }); Equal("4,2,1,3", Ids(view)); });
        Test("query.search-across-fields", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Search = "west alp" }); Equal("1,3", Ids(view)); });
        Test("query.search-quoted-phrase", () => { var table = Fixture(); table.Apply([new(1, "Name", "Alpha Beta")]); using var view = new GridView(table); view.SetQuery(new() { Search = "\"Alpha Beta\"" }); Equal("1", Ids(view)); });
        Test("query.invalid-is-atomic", () => { using var view = new GridView(Fixture()); Throws<ArgumentException>(() => view.SetQuery(new() { Sorts = [new("Missing")] })); Equal("1,2,3,4", Ids(view)); Equal(0, view.Query.Sorts.Count); });
        Test("query.duplicate-sort-rejected", () => { using var view = new GridView(Fixture()); Throws<ArgumentException>(() => view.SetQuery(new() { Sorts = [new("Name"), new("name")] })); });
        Test("filter.precedence", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Name] = 'Beta' OR [Amount] >= 20 AND [Active] = true") }); Equal("2,3", Ids(view)); });
        Test("filter.parentheses-not", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("NOT ([Region] = 'West' OR [Region] IS NULL)") }); Equal("2", Ids(view)); });
        Test("filter.in-between", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Amount] BETWEEN 10 AND 30 AND [Name] IN ('Alpha','Alpine')") }); Equal("1,3", Ids(view)); });
        Test("filter.not-in", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Code] NOT IN (1, 3)") }); Equal("2,4", Ids(view)); });
        Test("filter.contains-functions", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("Contains([Name], 'ALP') AND EndsWith([Region], 'est')") }); Equal("1,3", Ids(view)); });
        Test("filter.escaped-string", () => { var table = Fixture(); table.Apply([new(1, "Name", "O'Brien")]); using var view = new GridView(table); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Name] = 'O''Brien'") }); Equal("1", Ids(view)); });
        Test("filter.null-not-ordered", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Amount] < 20") }); Equal("1", Ids(view)); });
        Test("filter.null-function", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("IsNull([Amount])") }); Equal("4", Ids(view)); });
        Test("filter.auto-numeric", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.AutoFilter("Amount", ">=20") }); Equal("2,3", Ids(view)); });
        Test("filter.invalid-literal", () => { using var view = new GridView(Fixture()); Throws<FormatException>(() => view.SetQuery(new() { Filter = CriteriaParser.Parse("[Amount] > 'oops'") })); });
        Test("filter.reject-trailing-code", () => Throws<FormatException>(() => CriteriaParser.Parse("[Code] = 1; delete all")));
        Test("filter.reject-unclosed-quote", () => Throws<FormatException>(() => CriteriaParser.Parse("[Name] = 'bad")));
        Test("filter.depth-limit", () => Throws<FormatException>(() => CriteriaParser.Parse(new string('(', 40) + "[Code] = 1" + new string(')', 40))));
        Test("filter.length-limit", () => Throws<FormatException>(() => CriteriaParser.Parse(new string('x', 16385))));
        Test("group.nested-counts", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Groups = [new("Region"), new("Active")] }); Equal(3, view.Groups.Count); Equal(2, view.Groups.Single(group => Equals(group.Value, "West")).Records.Count); Equal(10, view.Rows.Count); });
        Test("group.collapse-preserves-totals", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Groups = [new("Region")], Summaries = [new("Amount", SummaryKind.Sum)] }); view.CollapseAll(); Equal(3, view.Rows.Count); Equal(60m, view.Summaries["Amount:Sum"]); view.ExpandAll(); Equal(7, view.Rows.Count); });
        Test("group.date-interval", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Groups = [new("Date", Interval: GroupInterval.Year)] }); Equal(3, view.Groups.Count); });
        Test("group.path-escaping", () => { var table = Fixture(); table.Apply([new(1, "Region", "a/b:c"), new(2, "Region", "a%2Fb:c")]); using var view = new GridView(table); view.SetQuery(new() { Groups = [new("Region")] }); Equal(4, view.Groups.Select(group => group.Key).Distinct().Count()); });
        Test("group.case-insensitive-buckets", () => { var table = Fixture(); table.Apply([new(3, "Region", "west")]); using var view = new GridView(table); view.SetQuery(new() { Groups = [new("Region")] }); Equal(3, view.Groups.Count); });
        Test("summary.all-builtins", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Summaries = Enum.GetValues<SummaryKind>().Where(kind => kind != SummaryKind.Custom).Select(kind => new SummaryDescriptor("Amount", kind)).ToArray() }); Equal(4, view.Summaries["Amount:Count"]); Equal(60m, view.Summaries["Amount:Sum"]); Equal(20m, view.Summaries["Amount:Average"]); Equal(10m, view.Summaries["Amount:Minimum"]); Equal(30m, view.Summaries["Amount:Maximum"]); Equal(3, view.Summaries["Amount:DistinctCount"]); });
        Test("summary.empty-average", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Search = "no-match", Summaries = [new("Amount", SummaryKind.Average)] }); Equal<object?>(null, view.Summaries["Amount:Average"]); });
        Test("summary.reject-text-sum", () => { using var view = new GridView(Fixture()); Throws<ArgumentException>(() => view.SetQuery(new() { Summaries = [new("Name", SummaryKind.Sum)] })); });
        Test("detail.expand-collapse", () => { using var view = new GridView(Fixture()); view.ToggleDetail(1); Equal(5, view.Rows.Count); Equal(GridRowKind.Detail, view.Rows[1].Kind); view.ToggleDetail(1); Equal(4, view.Rows.Count); });
        Test("selection.range-skips-groups", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Groups = [new("Region")] }); var selection = new GridSelection(); selection.Select(view, new(2, "Name")); selection.Select(view, new(3, "Name"), extend: true); Equal(3, selection.Count); });
        Test("selection.stable-after-sort", () => { using var view = new GridView(Fixture()); var selection = new GridSelection(); selection.Select(view, new(1, "Name")); view.SetQuery(new() { Sorts = [new("Name", SortDirection.Descending)] }); True(selection.Contains(1)); Equal(1L, selection.FocusedCell!.Value.RecordId); });
        Test("selection.prune-deleted", () => { using var view = new GridView(Fixture()); var selection = new GridSelection(); selection.SelectAll(view); view.Table.Remove([1, 2]); selection.Prune(view.Table); Equal(2, selection.Count); });
        Test("edit.atomic-validation", () => { var table = Fixture(); var history = new EditHistory(table); Throws<GridValidationException>(() => history.Execute([new(1, "Name", "Changed"), new(2, "Amount", -1m)])); Equal("Alpha", table[1][1]); Equal(20m, table[2][3]); True(!history.CanUndo); });
        Test("edit.readonly", () => { var table = Fixture(); Throws<GridValidationException>(() => table.Apply([new(1, "Code", 99)])); Equal(1, table[1][0]); });
        Test("edit.duplicate-cell", () => { var table = Fixture(); Throws<ArgumentException>(() => table.Apply([new(1, "Name", "A"), new(1, "Name", "B")])); Equal("Alpha", table[1][1]); });
        Test("edit.undo-redo-multiple", () => { var table = Fixture(); var history = new EditHistory(table); history.Execute([new(1, "Name", "A")]); history.Execute([new(1, "Name", "B")]); True(history.Undo()); Equal("A", table[1][1]); True(history.Undo()); Equal("Alpha", table[1][1]); True(history.Redo()); True(history.Redo()); Equal("B", table[1][1]); });
        Test("edit.reentrant-command-query", () => { var table = Fixture(); var history = new EditHistory(table); table.Changed += (_, _) => { _ = history.CanUndo; _ = history.CanRedo; }; history.Execute([new(1, "Name", "A")]); history.Execute([new(1, "Name", "B")]); True(history.Undo()); True(history.Undo()); Equal("Alpha", table[1][1]); });
        Test("edit.optimistic-conflict", () => { var table = Fixture(); var history = new EditHistory(table); var edit = history.Begin(1); edit.Set("Name", "Conflict"); table.Apply([new(1, "Amount", 11m)]); Throws<GridConcurrencyException>(() => edit.Commit()); Equal("Alpha", table[1][1]); });
        Test("edit.removed-reused-identity", () => { var table = Fixture(); var history = new EditHistory(table); var edit = history.Begin(1); edit.Set("Name", "Conflict"); var values = table[1].CopyValues(); table.Remove([1]); table.Add(new(1, values)); Throws<GridConcurrencyException>(() => edit.Commit()); });
        Test("edit.external-write-invalidates-history", () => { var table = Fixture(); var history = new EditHistory(table); history.Execute([new(1, "Name", "A")]); table.Apply([new(2, "Name", "External")]); True(!history.CanUndo); });
        Test("edit.cancel", () => { var table = Fixture(); var history = new EditHistory(table); var edit = history.Begin(1); edit.Set("Name", "Uncommitted"); edit.Cancel(); Equal("Alpha", table[1][1]); Throws<ObjectDisposedException>(() => edit.Commit()); });
        Test("edit.cross-field-validation", () => { var schema = new GridSchema([new("Start", typeof(int)), new("End", typeof(int))]) { RowValidator = values => (int)values[0]! > (int)values[1]! ? "Start must precede End." : null }; var table = new GridTable(schema); table.Add(new(1, new object?[] { 1, 5 })); Throws<GridValidationException>(() => table.Apply([new(1, "Start", 10)])); table.Apply([new(1, "Start", 10), new(1, "End", 20)]); Equal(10, table[1][0]); });
        Test("layout.row-boundaries", () => { var heights = new RowHeightIndex(4, 20); Equal(0, heights.RowAt(0)); Equal(1, heights.RowAt(20)); Equal(3, heights.RowAt(80)); Near(80, heights.TotalHeight); Equal(new VisibleRange(1, 2), heights.Visible(20, 40, 0)); });
        Test("layout.empty-and-invalid", () => { var heights = new RowHeightIndex(); Equal(-1, heights.RowAt(0)); Equal(0, heights.Visible(0, 100).Count); Throws<ArgumentOutOfRangeException>(() => heights.Reset(2, 0)); });
        Test("layout.variable-height-differential", () => { var random = new Random(7321); var heights = new RowHeightIndex(1000, 28); var reference = Enumerable.Repeat(28d, 1000).ToArray(); for (int i = 0; i < 3000; i++) { int index = random.Next(reference.Length); reference[index] = random.Next(18, 150); heights.SetHeight(index, reference[index]); } double prefix = 0; for (int i = 0; i < reference.Length; i++) { Near(prefix, heights.OffsetOf(i)); Equal(i, heights.RowAt(prefix + reference[i] / 2)); prefix += reference[i]; } Near(prefix, heights.TotalHeight); });
        Test("layout.frozen-columns-clipped", () => { var layout = new GridLayout(Fixture().Schema); layout.SetPin("Code", ColumnPin.Left); layout.SetPin("Date", ColumnPin.Right); var viewport = layout.Compute(600, 100); Equal(38d, viewport.Slots.Single(slot => slot.Field == "Code").X); True(viewport.Slots.All(slot => slot.ClipLeft >= 38 && slot.ClipRight <= 600)); True(viewport.MaximumOffset > 0); });
        Test("layout.prevent-hiding-last-column", () => { var schema = new GridSchema([new("X", typeof(int))]); var layout = new GridLayout(schema); Throws<InvalidOperationException>(() => layout.SetVisible("X", false)); });
        Test("layout.reorder", () => { var layout = new GridLayout(Fixture().Schema); layout.MoveBefore("Amount", "Name"); Equal("Code,Amount,Name,Region,Active,Date", string.Join(",", layout.DisplayColumns().Select(column => column.Field))); });
        Test("layout.serialization-roundtrip", () => { var table = Fixture(); var layout = new GridLayout(table.Schema); layout.SetWidth("Name", 240); layout.SetPin("Code", ColumnPin.Left); var document = new GridLayoutDocument { Columns = layout.Columns, Query = new() { Filter = CriteriaParser.Parse("[Amount] >= 20"), Sorts = [new("Name")] } }; var restored = LayoutSerializer.Load(LayoutSerializer.Save(document), table.Schema); var output = new GridLayout(table.Schema); output.RestoreColumns(restored.Columns); Equal(240d, output.Columns.Single(column => column.Field == "Name").Width); using var view = new GridView(table); view.SetQuery(restored.Query); Equal("3,2", Ids(view)); });
        Test("layout.schema-evolution", () => { var table = Fixture(); var layout = new GridLayout(table.Schema); layout.RestoreColumns([new("Removed", 100, 0), new("Name", 200, 1)]); Equal(6, layout.Columns.Count); Equal("Name", layout.Columns[0].Field); });
        Test("layout.reject-future-version", () => Throws<FormatException>(() => LayoutSerializer.Load("{\"version\":999}", Fixture().Schema)));
        Test("layout.reject-invalid-geometry", () => { var layout = new GridLayout(Fixture().Schema); Throws<FormatException>(() => layout.RestoreColumns([new("Name", -20, 0)])); Throws<ArgumentOutOfRangeException>(() => layout.SetWidth("Name", double.NaN)); });
        Test("csv.rfc4180-roundtrip", () => { var value = "a,\"b\"\r\nc"; var parsed = DelimitedText.Parse(DelimitedText.Escape(value) + ",last\r\n"); Equal(value, parsed[0][0]); Equal("last", parsed[0][1]); Equal(1, parsed.Count); });
        Test("csv.empty-and-trailing-field", () => { Equal(0, DelimitedText.Parse("").Count); Equal(2, DelimitedText.Parse("a,")[0].Count); Equal("", DelimitedText.Parse("\"\"")[0][0]); });
        Test("csv.reject-malformed-quote", () => { Throws<FormatException>(() => DelimitedText.Parse("\"unterminated")); Throws<FormatException>(() => DelimitedText.Parse("\"a\"junk")); });
        Test("csv.formula-injection-protection", () => { var table = Fixture(); table.Apply([new(1, "Name", "=1+1")]); using var view = new GridView(table); var csv = GridExporter.ToCsv(view, new() { Fields = ["Name", "Amount"] }); True(csv.Contains("'=1+1")); True(csv.Contains("10")); });
        Test("csv.filtered-selection", () => { using var view = new GridView(Fixture()); view.SetQuery(new() { Filter = CriteriaParser.Parse("[Amount] >= 20") }); var rows = DelimitedText.Parse(GridExporter.ToCsv(view, new() { SelectedIds = new HashSet<long> { 1, 3 } })); Equal(2, rows.Count); Equal("Alpine", rows[1][1]); });
        Test("html.escaping", () => { var table = Fixture(); table.Apply([new(1, "Name", "<script>alert(1)</script>")]); using var view = new GridView(table); var html = GridExporter.ToHtml(view); True(!html.Contains("<script>")); True(html.Contains("&lt;script&gt;")); });
        Test("xlsx.valid-package-and-cell-types", () => { using var view = new GridView(Fixture()); using var zip = new ZipArchive(new MemoryStream(GridExporter.ToXlsx(view))); True(zip.GetEntry("[Content_Types].xml") is not null); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml = XDocument.Load(sheet); Equal(5, xml.Descendants(ns + "row").Count()); Equal("b", (string?)xml.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "E2").Attribute("t")); });
        Test("xlsx.no-formula-execution", () => { var table = Fixture(); table.Apply([new(1, "Name", "=HYPERLINK(\"x\")")]); using var view = new GridView(table); using var zip = new ZipArchive(new MemoryStream(GridExporter.ToXlsx(view))); using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); var xml = reader.ReadToEnd(); True(!xml.Contains("<f>")); True(xml.Contains("inlineStr")); });
        Test("xlsx.precision-preservation", () => { var table = Fixture(); table.Apply([new(1, "Amount", 12345678901234567890.12m)]); using var view = new GridView(table); using var zip = new ZipArchive(new MemoryStream(GridExporter.ToXlsx(view))); using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); True(reader.ReadToEnd().Contains("12345678901234567890.12")); });
        Test("xlsx.column-names", () => { Equal("A", GridExporter.ColumnName(0)); Equal("Z", GridExporter.ColumnName(25)); Equal("AA", GridExporter.ColumnName(26)); Equal("XFD", GridExporter.ColumnName(16383)); });
        await TestAsync("data.local-page-order-filter", async () => { var source = new LocalPageSource(Fixture()); var page = await source.FetchAsync(new() { Sorts = [new("Amount", SortDirection.Descending)], Filter = CriteriaParser.Parse("[Active] = true") }, 1, 1, default); Equal(1L, page.Records[0].Id); Equal(2, page.TotalCount); });
        await TestAsync("data.capability-rejection", async () => { var source = new LocalPageSource(Fixture()); await ThrowsAsync<NotSupportedException>(async () => await source.FetchAsync(new() { Groups = [new("Region")] }, 0, 10, default)); });
        await TestAsync("data.request-coalescing", async () => { var source = new ControlledSource(); using var cache = new GridPageCache(source, 2); var first = cache.GetPageAsync(0); var second = cache.GetPageAsync(0); Equal(1, source.Calls); source.Release(); await Task.WhenAll(first, second); Equal(1, cache.CachedPageCount); });
        await TestAsync("data.independent-caller-cancellation", async () => { var source = new ControlledSource(); using var cache = new GridPageCache(source, 2); using var cancellation = new CancellationTokenSource(); var first = cache.GetPageAsync(0, cancellation.Token); var second = cache.GetPageAsync(0); cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(async () => await first); source.Release(); Equal(2, (await second).Records.Count); });
        await TestAsync("data.stale-query-cancelled", async () => { var source = new ControlledSource(); using var cache = new GridPageCache(source, 2); var first = cache.GetPageAsync(0); cache.SetQuery(new() { Search = "next" }); var second = cache.GetPageAsync(0); await ThrowsAsync<OperationCanceledException>(async () => await first); source.Release(); await second; Equal(2, source.Calls); });
        await TestAsync("data.lru-capacity", async () => { var source = new LocalPageSource(Fixture()); using var cache = new GridPageCache(source, 1, 2); await cache.GetPageAsync(0); await cache.GetPageAsync(1); await cache.GetPageAsync(2); Equal(2, cache.CachedPageCount); });
        await TestAsync("data.disposal", async () => { var source = new ControlledSource(); var cache = new GridPageCache(source, 2); var page = cache.GetPageAsync(0); cache.Dispose(); await ThrowsAsync<OperationCanceledException>(async () => await page); Throws<ObjectDisposedException>(() => cache.GetPageAsync(0)); source.Release(); });
        if (stress)
        {
            Test("stress.100000-record-sort-filter-group", () => { var schema = new GridSchema([new("Id", typeof(int)), new("Group", typeof(string)), new("Amount", typeof(decimal))]); var table = new GridTable(schema); table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i, "Group " + i % 20, (decimal)i }))); using var view = new GridView(table); view.SetQuery(new() { Sorts = [new("Amount", SortDirection.Descending)], Filter = new FieldFilter("Id", FilterOperator.GreaterOrEqual, 50000), Groups = [new("Group")], Summaries = [new("Amount", SummaryKind.Count)] }); Equal(50000, view.FilteredRecords.Count); Equal(20, view.Groups.Count); Equal(99999L, view.FilteredRecords[0].Id); Equal(50000, view.Summaries["Amount:Count"]); });
            Test("stress.million-row-virtualization", () => { var index = new RowHeightIndex(1_000_000, 28); var visible = index.Visible(14_000_000, 600); True(visible.Count <= 27); Equal(499998, visible.Start); Near(28_000_000, index.TotalHeight); });
            Test("stress.1000-column-virtualization", () => { var schema = new GridSchema(Enumerable.Range(0, 1000).Select(i => new GridColumn("C" + i, typeof(int)) { Width = 100 })); var layout = new GridLayout(schema); var viewport = layout.Compute(1000, 50000); True(viewport.Slots.Count <= 11); True(viewport.Slots.All(slot => slot.SchemaIndex >= 500)); });
        }
        return new(results.AsReadOnly(), total.Elapsed.TotalMilliseconds);
    }
    private sealed class ControlledSource : IGridPageSource
    {
        private readonly GridTable _table = Fixture();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public GridSchema Schema => _table.Schema;
        public DataCapabilities Capabilities => DataCapabilities.Search;
        public void Release() => _gate.TrySetResult();
        public async ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken)
        {
            Calls++; await _gate.Task; // Intentionally ignores cancellation: cache must still suppress stale data.
            return new(offset, _table.Records.Skip(offset).Take(count).ToArray(), _table.Records.Count);
        }
    }
}

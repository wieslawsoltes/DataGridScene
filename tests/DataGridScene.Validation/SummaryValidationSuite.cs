using DataGridScene.Core;
using DataGridScene.Data;
using DataGridScene.Export;
using DataGridScene.Layout;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Validation;

public static class SummaryValidationSuite
{
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal(object? expected, object? actual) => Check(Equals(expected, actual), $"Expected {expected}, received {actual}");
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
    private static GridQuery Query(GroupFooterDisplayMode footers = GroupFooterDisplayMode.ExpandedGroups) => new()
    {
        Groups = [new("Region")], Summaries = [new("Amount", SummaryKind.Sum, "total")],
        GroupSummaries = [new("Amount", SummaryKind.Sum, "amount") { ShowInColumn = "Name", ShowInGroupFooter = true }, new("Code", SummaryKind.Count, "count")],
        GroupFooterDisplay = footers
    };
    private sealed class Weighted : IGridSummaryAccumulator, IDisposable
    {
        private decimal _sum, _weight;
        public static int Disposed;
        public void Add(GridRecord record, object? value) { if (value is decimal amount) { decimal weight = Convert.ToDecimal(record[0]); _sum += amount * weight; _weight += weight; } }
        public object? Complete() => _weight == 0 ? null : _sum / _weight;
        public void Dispose() => Disposed++;
    }
    private sealed class Throwing : IGridSummaryAccumulator, IDisposable
    {
        public static int Disposed;
        public void Add(GridRecord record, object? value) => throw new InvalidOperationException("Rejected custom summary");
        public object? Complete() => null;
        public void Dispose() => Disposed++;
    }
    private static GridTable CustomTable()
    {
        var schema = new GridSchema([new("Weight", typeof(int)), new("Region", typeof(string)), new("Amount", typeof(decimal))])
        { CustomSummaries = new Dictionary<string, Func<IGridSummaryAccumulator>> { ["weighted"] = () => new Weighted(), ["throw"] = () => new Throwing() } };
        var table = new GridTable(schema);
        table.AddRange([new(1, new object?[] { 1, "A", 10m }), new(2, new object?[] { 3, "A", 30m }), new(3, new object?[] { 2, "B", 60m })]);
        return table;
    }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var clock = Stopwatch.StartNew(); var baseline = await WorkspaceValidationSuite.RunAsync(stress); var results = baseline.Results.ToList();
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("group-summary.independent-from-total", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query());
            Equal(60m, view.Summaries["total"]); Equal(40m, view.Groups.Single(g => Equals(g.Value, "West")).Summaries["amount"]);
            Check(!view.Summaries.ContainsKey("amount") && !view.Groups[0].Summaries.ContainsKey("total"));
        });
        Test("group-summary.legacy-inheritance-and-explicit-empty", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { GroupSummaries = null, GroupFooterDisplay = GroupFooterDisplayMode.Hidden });
            Check(view.Groups.All(g => g.Summaries.ContainsKey("total")));
            view.SetQuery(view.Query with { GroupSummaries = [] }); Check(view.Groups.All(g => g.Summaries.Count == 0));
        });
        Test("group-summary.expanded-footers-postorder", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query());
            Equal(3, view.Rows.Count(row => row.Kind == GridRowKind.GroupFooter));
            foreach (var group in view.Groups) {
                int footer = view.Rows.ToList().FindIndex(row => row.Key == "footer:" + group.Key);
                Check(footer > 0 && view.Rows[footer - 1].Kind == GridRowKind.Data);
                Check(view.Rows[footer].Record is null && view.Rows[footer].Group == group);
            }
        });
        Test("group-summary.nested-footers-on-each-level", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { Groups = [new("Region"), new("Active")] });
            Equal(6, view.Rows.Count(row => row.Kind == GridRowKind.GroupFooter));
            var west = view.Groups.Single(g => Equals(g.Value, "West")); Equal(40m, west.Summaries["amount"]);
            Check(view.Rows[^1].Kind == GridRowKind.GroupFooter && view.Rows[^2].Kind == GridRowKind.GroupFooter);
        });
        Test("group-summary.collapse-hides-expanded-footer", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query());
            var west = view.Groups.Single(g => Equals(g.Value, "West")); view.ToggleGroup(west.Key);
            Equal(2, view.Rows.Count(row => row.Kind == GridRowKind.GroupFooter)); Equal(-1, view.IndexOfRecord(1));
            Equal(40m, west.Summaries["amount"]);
        });
        Test("group-summary.always-footers-under-collapsed-groups", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query(GroupFooterDisplayMode.Always)); view.CollapseAll();
            Equal(6, view.Rows.Count); Equal(3, view.Rows.Count(row => row.Kind == GridRowKind.GroupFooter));
        });
        Test("group-summary.hidden-footers-and-unplaced-summaries", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query(GroupFooterDisplayMode.Hidden));
            Check(view.Rows.All(row => row.Kind != GridRowKind.GroupFooter));
            view.SetQuery(Query() with { GroupSummaries = [new("Code", SummaryKind.Count)] });
            Check(view.Rows.All(row => row.Kind != GridRowKind.GroupFooter));
        });
        Test("group-summary.sort-descending-and-ascending", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { GroupSummarySorts = [new("Region", "amount", SortDirection.Descending)] });
            Equal("West", view.Groups[0].Value); Equal("East", view.Groups[1].Value);
            view.SetQuery(view.Query with { GroupSummarySorts = [new("Region", "amount")] }); Equal(null, view.Groups[0].Value);
        });
        Test("group-summary.sort-independent-nested-levels", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            view.SetQuery(Query() with { Groups = [new("Active"), new("Region")], GroupSummarySorts = [new("Region", "amount", SortDirection.Descending)] });
            Equal(false, view.Groups[0].Value); Equal("East", view.Groups[0].Children[0].Value);
        });
        Test("group-summary.multi-sort-tie-fallback", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            view.SetQuery(Query() with { GroupSummarySorts = [new("Region", "count", SortDirection.Descending), new("Region", "amount", SortDirection.Descending)] });
            Equal("West", view.Groups[0].Value); Equal("East", view.Groups[1].Value);
        });
        Test("group-summary.recompute-on-edit-undo", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { GroupSummarySorts = [new("Region", "amount", SortDirection.Descending)] });
            var history = new EditHistory(view.Table); history.Execute([new(2, "Amount", 90m)]);
            Equal("East", view.Groups[0].Value); Equal(130m, view.Summaries["total"]);
            Check(history.Undo()); Equal("West", view.Groups[0].Value); Equal(60m, view.Summaries["total"]);
        });
        Test("group-summary.filters-affect-totals-and-groups", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { Filter = new FieldFilter("Amount", FilterOperator.Greater, 15) });
            Equal(50m, view.Summaries["total"]); Equal(30m, view.Groups.Single(g => Equals(g.Value, "West")).Summaries["amount"]);
        });
        Test("group-summary.detail-rows-never-counted", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.ToggleDetail(1); view.SetQuery(Query());
            Equal(2, view.Groups.Single(g => Equals(g.Value, "West")).Summaries["count"]);
            var index = view.IndexOfRecord(1); Check(view.Rows[index + 1].Kind == GridRowKind.Detail);
        });
        Test("group-summary.selection-navigation-skips-footers", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query());
            var selection = new GridSelection(); selection.Select(view, new(4, "Name")); selection.Select(view, new(3, "Name"), extend: true); Equal(4, selection.Count);
            Check(view.Rows[view.IndexOfRecord(2)].Record!.Id == 2);
        });
        Test("group-summary.clipboard-does-not-copy-footer-rows", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query());
            var selection = new GridSelection(); selection.SelectAll(view);
            Equal(4, DelimitedText.Parse(GridClipboard.Copy(view, selection, ["Name"]), '\t').Count);
        });
        Test("group-summary.export-retains-group-order-when-collapsed", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(Query() with { GroupSummarySorts = [new("Region", "amount", SortDirection.Descending)] }); view.CollapseAll();
            var data = DelimitedText.Parse(GridExporter.ToCsv(view, new() { Fields = ["Name"], IncludeHeaders = false }));
            Equal("Alpha,Alpine,Beta,Delta", string.Join(",", data.Select(row => row[0])));
        });
        Test("group-summary.display-target-format-and-culture", () => {
            var table = ValidationSuite.Fixture(); var summary = new SummaryDescriptor("Amount", SummaryKind.Sum) { ShowInColumn = "Name", DisplayFormat = "{1} {2}: {0:F2}" };
            Equal("Name", summary.TargetField); Equal("Amount Sum: 12.50", summary.Display(12.5m, table.Schema));
        });
        Test("group-summary.layout-roundtrip-and-collapse", () => {
            var table = ValidationSuite.Fixture(); using var view = new GridView(table); view.SetQuery(Query() with { GroupSummarySorts = [new("Region", "amount", SortDirection.Descending)], GroupSummaryDisplay = GroupSummaryDisplayMode.AlignByColumns });
            view.ToggleGroup(view.Groups[0].Key); var layout = new GridLayout(table.Schema);
            var restored = LayoutSerializer.Load(LayoutSerializer.Save(new() { Query = view.Query, Columns = layout.Columns, ViewState = view.CaptureState() }), table.Schema);
            view.SetQuery(restored.EffectiveQuery()); view.RestoreState(restored.ViewState);
            Equal(1, view.Query.GroupSummarySorts.Count); Equal(GroupSummaryDisplayMode.AlignByColumns, view.Query.GroupSummaryDisplay);
            Equal("Name", view.Query.GroupSummaries![0].TargetField); Check(!view.IsExpanded(view.Groups[0].Key));
        });
        foreach (var (name, query) in new (string, GridQuery)[] {
            ("unknown-group", Query() with { GroupSummarySorts = [new("Active", "amount")] }),
            ("unknown-summary", Query() with { GroupSummarySorts = [new("Region", "missing")] }),
            ("duplicate-summary", Query() with { GroupSummaries = [new("Amount", SummaryKind.Sum, "x"), new("Code", SummaryKind.Count, "x")] }),
            ("unknown-target", Query() with { GroupSummaries = [new("Amount", SummaryKind.Sum) { ShowInColumn = "Missing" }] }),
            ("bad-kind", Query() with { GroupSummaries = [new("Amount", (SummaryKind)999)] }),
            ("bad-direction", Query() with { GroupSummarySorts = [new("Region", "amount", (SortDirection)999)] }),
            ("bad-display", Query() with { GroupSummaryDisplay = (GroupSummaryDisplayMode)999 }),
            ("bad-footer", Query() with { GroupFooterDisplay = (GroupFooterDisplayMode)999 }),
            ("null-sorts", Query() with { GroupSummarySorts = null! }),
            ("text-sum", Query() with { GroupSummaries = [new("Name", SummaryKind.Sum)] }),
            ("duplicate-sort", Query() with { GroupSummarySorts = [new("Region", "amount"), new("region", "amount")] })
        }) Test("group-summary.reject-" + name + "-atomically", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var rows = view.Rows; int revision = view.Revision;
            Reject<ArgumentException>(() => view.SetQuery(query)); Check(ReferenceEquals(rows, view.Rows)); Equal(revision, view.Revision);
        });
        foreach (string format in new[] { "{", "{3}", "{0,10000000}", "{0:invalid-numeric-format}" })
            Test("group-summary.reject-format-" + format, () => {
                using var view = new GridView(ValidationSuite.Fixture()); var before = view.Rows;
                // Arbitrary numeric literal formats are legal .NET formats, so only syntax/alignment are required to reject.
                if (format.Contains("invalid-numeric")) { view.SetQuery(Query() with { GroupSummaries = [new("Amount", SummaryKind.Sum) { DisplayFormat = format }] }); return; }
                Reject<FormatException>(() => view.SetQuery(Query() with { GroupSummaries = [new("Amount", SummaryKind.Sum) { DisplayFormat = format }] })); Check(ReferenceEquals(before, view.Rows));
            });
        Test("query.snapshot-owns-summary-and-sort-lists", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            var items = new List<SummaryDescriptor> { new("Amount", SummaryKind.Sum, "amount") }; var sorts = new List<GroupSummarySortDescriptor> { new("Region", "amount") };
            view.SetQuery(Query() with { GroupSummaries = items, GroupSummarySorts = sorts }); items.Clear(); sorts.Clear(); view.Refresh();
            Equal(1, view.Query.GroupSummaries!.Count); Equal(1, view.Query.GroupSummarySorts.Count);
        });
        Test("query.snapshot-deep-filter-ownership", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var fields = new List<GridFilter> { new FieldFilter("Name", FilterOperator.Contains, "Alpha") };
            view.SetQuery(new() { Filter = new FilterGroup(true, fields) }); fields.Clear(); view.Refresh(); Equal(1, view.FilteredRecords.Count);
        });
        Test("query.snapshot-invalid-date-interval-on-empty", () => {
            var table = ValidationSuite.Fixture(); table.Clear(); using var view = new GridView(table);
            Reject<ArgumentException>(() => view.SetQuery(new() { Groups = [new("Name", Interval: GroupInterval.Month)] }));
        });
        Test("summary.double-compensated-sum", () => {
            var table = new GridTable(new GridSchema([new("A", typeof(double))])); table.AddRange(new[] { 1e16, 1d, -1e16 }.Select((v, i) => new GridRecord(i, new object?[] { v })));
            var result = SummaryCalculator.Calculate(table.Records, table.Schema, [new("A", SummaryKind.Sum), new("A", SummaryKind.Average)]);
            Equal(1d, result["A:Sum"]); Equal(1d/3, result["A:Average"]);
        });
        Test("summary.empty-and-null-semantics", () => {
            var table = new GridTable(new GridSchema([new("A", typeof(decimal))])); table.Add(new(1, new object?[] { null }));
            var result = SummaryCalculator.Calculate(table.Records, table.Schema, Enum.GetValues<SummaryKind>().Where(k => k != SummaryKind.Custom).Select(k => new SummaryDescriptor("A", k)).ToArray());
            Equal(1, result["A:Count"]); Equal(0m, result["A:Sum"]); Equal(null, result["A:Average"]); Equal(0, result["A:DistinctCount"]);
        });
        Test("summary.custom-weighted-per-group-disposed", () => {
            Weighted.Disposed = 0; using var view = new GridView(CustomTable());
            view.SetQuery(new() { Groups = [new("Region")], GroupSummaries = [new("Amount", SummaryKind.Custom, "weighted") { CustomAggregate = "weighted" }] });
            Equal(25m, view.Groups[0].Summaries["weighted"]); Equal(60m, view.Groups[1].Summaries["weighted"]); Equal(2, Weighted.Disposed);
        });
        Test("summary.custom-sort-and-layout-registry", () => {
            using var view = new GridView(CustomTable()); view.SetQuery(new() { Groups = [new("Region")], GroupSummaries = [new("Amount", SummaryKind.Custom, "w") { CustomAggregate = "weighted" }], GroupSummarySorts = [new("Region", "w", SortDirection.Descending)] });
            Equal("B", view.Groups[0].Value);
            var json = LayoutSerializer.Save(new() { Query = view.Query }); var state = LayoutSerializer.Load(json, view.Table.Schema);
            Equal("weighted", state.Query.GroupSummaries![0].CustomAggregate);
        });
        Test("summary.custom-failure-disposed-and-atomic", () => {
            Throwing.Disposed = 0; using var view = new GridView(CustomTable()); var rows = view.Rows;
            Reject<InvalidOperationException>(() => view.SetQuery(new() { Summaries = [new("Amount", SummaryKind.Custom) { CustomAggregate = "throw" }] }));
            Equal(1, Throwing.Disposed); Check(ReferenceEquals(rows, view.Rows));
        });
        Test("summary.custom-unknown-id-rejected", () => {
            using var view = new GridView(CustomTable()); Reject<ArgumentException>(() => view.SetQuery(new() { Summaries = [new("Amount", SummaryKind.Custom) { CustomAggregate = "missing" }] }));
        });
        Test("summary.result-dictionary-is-readonly", () => {
            var table = ValidationSuite.Fixture(); var result = SummaryCalculator.Calculate(table.Records, table.Schema, [new("Amount", SummaryKind.Sum)]);
            Reject<NotSupportedException>(() => ((IDictionary<string, object?>)result)["Amount:Sum"] = 1m);
        });
        Test("summary.comparer-controls-distinct-and-group-equality", () => {
            var comparer = Comparer<object?>.Create((a, b) => StringComparer.Ordinal.Compare(a, b));
            var table = new GridTable(new GridSchema([new("A", typeof(string)) { Comparer = comparer }])); table.AddRange([new(1, new object?[] { "a" }), new(2, new object?[] { "A" })]);
            using var view = new GridView(table); view.SetQuery(new() { Groups = [new("A")], Summaries = [new("A", SummaryKind.DistinctCount)] });
            Equal(2, view.Groups.Count); Equal(2, view.Summaries["A:DistinctCount"]); Check(view.Groups[0].Key != view.Groups[1].Key);
        });
        Test("data.record-source-explicitly-rejects-group-summaries", () => {
            var source = new LocalPageSource(ValidationSuite.Fixture());
            Reject<NotSupportedException>(() => source.FetchAsync(new() { GroupSummaries = [new("Amount", SummaryKind.Sum)] }, 0, 10, default).GetAwaiter().GetResult());
        });
        Test("query.page-cache-retains-owned-query", () => {
            var source = new LocalPageSource(ValidationSuite.Fixture()); using var cache = new GridPageCache(source, 10);
            var sorts = new List<SortDescriptor> { new("Name", SortDirection.Descending) }; cache.SetQuery(new() { Sorts = sorts }); sorts.Clear();
            Equal("Delta", cache.GetPageAsync(0).GetAwaiter().GetResult().Records[0][1]);
        });
        if (stress) Test("stress.summary-sort-and-footers-100000-records", () => {
            var table = new GridTable(new GridSchema([new("Group", typeof(int)), new("Amount", typeof(decimal))]));
            table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i % 100, (decimal)i })));
            using var view = new GridView(table); view.SetQuery(new() { Groups = [new("Group")], GroupSummaries = [new("Amount", SummaryKind.Sum, "sum") { ShowInGroupFooter = true }], GroupFooterDisplay = GroupFooterDisplayMode.ExpandedGroups, GroupSummarySorts = [new("Group", "sum", SortDirection.Descending)] });
            Equal(100200, view.Rows.Count); Equal(99, view.Groups[0].Value); Check(view.IndexOfRecord(99999) >= 0); Equal(100000, view.OrderedRecords.Count);
        });
        return new(results.AsReadOnly(), clock.Elapsed.TotalMilliseconds);
    }
}

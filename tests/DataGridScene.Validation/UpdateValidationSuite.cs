using DataGridScene.Core;
using DataGridScene.Layout;
using System.Collections.Immutable;
using System.Diagnostics;

namespace DataGridScene.Validation;

/// <summary>Operation-count and identity contracts, not machine-dependent timing thresholds.</summary>
public static class UpdateValidationSuite
{
    private static void Check(bool ok, string message = "Assertion failed.") { if (!ok) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-7, $"Expected {expected}, got {actual}");
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static GridTable Table(int count = 100)
    {
        var table = new GridTable(new GridSchema([new("Group", typeof(int)), new("Amount", typeof(int)), new("Note", typeof(string))]));
        table.AddRange(Enumerable.Range(0, count).Select(i => new GridRecord(i, new object?[] { i % 5, i, "Note " + i })));
        return table;
    }
    private static GridQuery Grouped() => new()
    {
        Sorts = [new("Amount", SortDirection.Descending)], Groups = [new("Group")],
        Summaries = [new("Amount", SummaryKind.Sum)], GroupSummaries = [new("Amount", SummaryKind.Sum)],
        GroupSummarySorts = [new("Group", "Amount:Sum", SortDirection.Descending)]
    };
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var report = await SummaryValidationSuite.RunAsync(stress);
        var watch = Stopwatch.StartNew(); var results = report.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("updates.display-edit-retains-all-shaped-identities", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(Grouped());
            var rows = view.Rows; var groups = view.Groups; var totals = view.Summaries; var ordered = view.OrderedRecords;
            long builds = view.RefreshCount, revision = view.RowsRevision; int events = 0;
            view.Changed += (_, _) => events++;
            table.Apply([new(1, "Note", "Updated")]);
            Check(ReferenceEquals(rows, view.Rows) && ReferenceEquals(groups, view.Groups) && ReferenceEquals(totals, view.Summaries) && ReferenceEquals(ordered, view.OrderedRecords));
            Check(view.RefreshCount == builds && view.RowsRevision == revision && view.ValueUpdateCount == 1 && events == 1);
            Check(view.LastChangeKind == GridViewChangeKind.Values && Equals(view.Rows[view.IndexOfRecord(1)].Record![2], "Updated"));
        });
        Test("updates.shape-edit-rebuilds-aggregates-order", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(Grouped());
            long builds = view.RefreshCount; var totals = view.Summaries;
            table.Apply([new(0, "Amount", 999)]);
            Check(view.RefreshCount == builds + 1 && view.LastChangeKind == GridViewChangeKind.Reset);
            Check(!Equals(view.Summaries["Amount:Sum"], totals["Amount:Sum"]));
            Check(view.OrderedRecords[0].Id == 0);
        });
        Test("updates.filter-and-search-dependencies", () => {
            var table = Table(); using var view = new GridView(table);
            view.SetQuery(new() { Filter = GridFilter.And(new FieldFilter("Amount", FilterOperator.Greater, 50), new NotFilter(new FieldFilter("Group", FilterOperator.Equal, 2))) });
            var rows = view.Rows; table.Apply([new(51, "Note", "Changed")]); Check(ReferenceEquals(rows, view.Rows));
            table.Apply([new(51, "Amount", 1)]); Check(view.IndexOfRecord(51) < 0);
            view.SetQuery(new() { Search = "Changed" }); table.Apply([new(52, "Note", "Changed")]); Check(view.FilteredRecords.Count == 2);
        });
        Test("updates.filtered-out-display-edit-stays-fast", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(new() { Filter = new FieldFilter("Amount", FilterOperator.Greater, 80) });
            long builds = view.RefreshCount; table.Apply([new(0, "Note", "Hidden update")]);
            Check(view.RefreshCount == builds && view.IndexOfRecord(0) == -1);
        });
        Test("updates.custom-filter-is-conservative", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(new() { Filter = new ApplicationFilter() });
            long builds = view.RefreshCount; table.Apply([new(0, "Note", "hide")]);
            Check(view.RefreshCount == builds + 1 && view.IndexOfRecord(0) == -1);
        });
        Test("updates.custom-summary-is-conservative", () => {
            var schema = new GridSchema([new("Amount", typeof(int)), new("Note", typeof(string))])
            { CustomSummaries = new Dictionary<string, Func<IGridSummaryAccumulator>> { ["length"] = () => new NoteLength() } };
            var table = new GridTable(schema); table.Add(new(1, new object?[] { 1, "a" })); using var view = new GridView(table);
            view.SetQuery(new() { Summaries = [new("Amount", SummaryKind.Custom) { CustomAggregate = "length" }] });
            table.Apply([new(1, "Note", "abc")]); Check(Equals(view.Summaries["Amount:Custom"], 3));
        });
        Test("updates.computed-dependencies-transitive-and-owned", () => {
            string[] dependencies = ["Amount"]; int evaluations = 0;
            var table = new GridTable(new GridSchema([
                new("Amount", typeof(int)), new("Note", typeof(string)),
                new("Double", typeof(int)) { ValueGetter = row => { evaluations++; return (int)row[0]! * 2; }, ValueDependencies = dependencies },
                new("Quad", typeof(int)) { ValueGetter = row => (int)row[0]! * 4, ValueDependencies = ["Double"] }
            ]));
            dependencies[0] = "Note";
            table.Add(new(1, new object?[] { 2, "old", null, null })); using var view = new GridView(table);
            view.SetQuery(new() { Sorts = [new("Double")], Summaries = [new("Quad", SummaryKind.Sum)] });
            evaluations = 0; var rows = view.Rows; table.Apply([new(1, "Note", "new")]);
            Check(ReferenceEquals(rows, view.Rows) && evaluations == 0);
            table.Apply([new(1, "Amount", 3)]); Check(Equals(view.Summaries["Quad:Sum"], 12m));
        });
        Test("updates.unknown-getter-dependency-rebuilds", () => {
            var table = new GridTable(new GridSchema([new("Note", typeof(string)), new("Length", typeof(int)) { ValueGetter = row => ((string)row[0]!).Length }]));
            table.Add(new(1, new object?[] { "a", null })); using var view = new GridView(table);
            view.SetQuery(new() { Summaries = [new("Length", SummaryKind.Sum)] });
            table.Apply([new(1, "Note", "abc")]); Check(Equals(view.Summaries["Length:Sum"], 3m));
        });
        Test("updates.dependency-validation-is-atomic", () => {
            var a = new GridColumn("A", typeof(int)) { ValueGetter = _ => 1, ValueDependencies = ["B"] };
            var b = new GridColumn("B", typeof(int)) { ValueGetter = _ => 1, ValueDependencies = ["A"] };
            Reject<ArgumentException>(() => new GridSchema([a,b])); Check(a.Index == -1 && b.Index == -1);
            Reject<ArgumentException>(() => new GridSchema([new("A", typeof(int)) { ValueGetter = _ => 1, ValueDependencies = ["missing"] }]));
        });
        Test("updates.count-summary-does-not-read-field", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(new() { Summaries = [new("Note", SummaryKind.Count)] });
            long builds = view.RefreshCount; table.Apply([new(0, "Note", "Changed")]); Check(view.RefreshCount == builds);
            table.Remove([0]); Check(view.RefreshCount == builds + 1 && Equals(view.Summaries["Note:Count"], 99));
        });
        Test("updates.edit-notification-details-owned", () => {
            var table = Table(); IReadOnlyList<CellChange>? changes = null;
            table.Changed += (_, e) => changes = e.CellChanges;
            var result = table.Apply([new(0, "Note", "New")]);
            Check(ReferenceEquals(changes, result.Changes));
            Reject<NotSupportedException>(() => ((IList<CellChange>)changes!).Clear());
        });
        Test("updates.defer-nested-query-composition", () => {
            using var view = new GridView(Table()); var original = view.Query; long builds = view.RefreshCount; int events = 0;
            view.Changed += (_, _) => events++;
            using (view.DeferRefresh())
            {
                view.SetQuery(view.RequestedQuery with { Search = "Note" });
                using (view.DeferRefresh()) view.SetQuery(view.RequestedQuery with { Sorts = [new("Amount", SortDirection.Descending)] });
                Check(ReferenceEquals(original, view.Query) && view.RefreshCount == builds && view.IsRefreshDeferred);
            }
            Check(view.Query.Search == "Note" && view.Query.Sorts.Count == 1 && view.FilteredRecords[0].Id == 99);
            Check(view.RefreshCount == builds + 1 && events == 1 && !view.IsRefreshDeferred);
        });
        Test("updates.defer-many-shaping-edits-one-refresh", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(Grouped()); long builds = view.RefreshCount;
            using (view.DeferRefresh()) for (int i = 0; i < 50; i++) table.Apply([new(i, "Amount", i + 100)]);
            Check(view.RefreshCount == builds + 1 && Equals(view.Summaries["Amount:Sum"], 9950m));
        });
        Test("updates.defer-display-edits-one-notification", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(Grouped()); long builds = view.RefreshCount; int events = 0;
            view.Changed += (_, _) => events++;
            using (view.DeferRefresh()) for (int i = 0; i < 50; i++) table.Apply([new(i, "Note", "new")]);
            Check(view.RefreshCount == builds && view.ValueUpdateCount == 1 && events == 1);
        });
        Test("updates.defer-empty-and-idempotent-disposal", () => {
            using var view = new GridView(Table()); int events = 0; view.Changed += (_, _) => events++;
            var scope = view.DeferRefresh(); scope.Dispose(); scope.Dispose(); Check(events == 0 && !view.IsRefreshDeferred);
        });
        Test("updates.defer-disposal-after-view-disposed", () => {
            var view = new GridView(Table()); var scope = view.DeferRefresh(); view.Refresh(); view.Dispose(); scope.Dispose();
            Reject<ObjectDisposedException>(() => view.DeferRefresh());
        });
        Test("updates.defer-shaping-failure-keeps-published-query", () => {
            var schema = new GridSchema([new("Value", typeof(int)) { ValueGetter = _ => throw new InvalidOperationException("getter failure") }]);
            var table = new GridTable(schema); table.Add(new(1, new object?[] { null })); using var view = new GridView(table);
            var rows = view.Rows; var query = view.Query; var scope = view.DeferRefresh();
            view.SetQuery(new() { Summaries = [new("Value", SummaryKind.Sum)] });
            Reject<InvalidOperationException>(scope.Dispose); Check(ReferenceEquals(rows, view.Rows) && ReferenceEquals(query, view.Query) && !view.IsRefreshDeferred);
            view.SetQuery(new()); Check(view.Rows.Count == 1);
        });
        Test("updates.table-batch-remains-single-reset", () => {
            var table = Table(); using var view = new GridView(table); long builds = view.RefreshCount;
            using (table.BeginUpdate()) { table.Add(new(100, new object?[] { 0, 100, "new" })); table.Remove([0]); }
            Check(view.RefreshCount == builds + 1 && view.IndexOfRecord(0) == -1 && view.IndexOfRecord(100) >= 0);
        });
        Test("updates.defer-expansion-does-not-recalculate", () => {
            using var view = new GridView(Table()); view.SetQuery(Grouped()); long builds = view.RefreshCount; long rows = view.RowsRevision;
            using (view.DeferRefresh()) { view.CollapseAll(); view.ToggleDetail(2); }
            Check(view.RefreshCount == builds && view.RowsRevision == rows + 1 && view.Rows.Count == 5);
        });
        Test("updates.row-key-index-detail-footer", () => {
            using var view = new GridView(Table()); view.SetQuery(Grouped() with { GroupFooterDisplay = GroupFooterDisplayMode.Always, GroupSummaries = [new("Amount", SummaryKind.Sum) { ShowInGroupFooter = true }] });
            view.ToggleDetail(1);
            for (int i = 0; i < view.Rows.Count; i++) Check(view.IndexOfRow(view.Rows[i].Key) == i);
            Check(view.IndexOfRow("row:0001") == -1 && view.IndexOfRow("missing") == -1);
        });
        Test("updates.edit-history-keeps-fast-path", () => {
            var table = Table(); using var view = new GridView(table); view.SetQuery(Grouped()); var history = new EditHistory(table); long builds = view.RefreshCount;
            history.Execute([new(1, "Note", "changed")]); history.Undo(); history.Redo();
            Check(view.RefreshCount == builds && view.ValueUpdateCount == 3 && Equals(table[1][2], "changed"));
        });
        Test("updates.randomized-differential-vs-full-refresh", () => {
            var table = Table(); using var fast = new GridView(table); fast.SetQuery(Grouped());
            using var full = new GridView(table); full.SetQuery(Grouped()); var random = new Random(7751);
            for (int i = 0; i < 200; i++)
            {
                int id = random.Next(100); string field = new[] { "Group", "Amount", "Note" }[random.Next(3)];
                object value = field == "Note" ? "N" + i : random.Next(100);
                table.Apply([new(id, field, value)]); full.Refresh();
                Check(fast.Rows.Select(row => row.Key).SequenceEqual(full.Rows.Select(row => row.Key)));
                Check(Equals(fast.Summaries["Amount:Sum"], full.Summaries["Amount:Sum"]));
                Check(fast.Groups.Select(g => g.Summaries["Amount:Sum"]).SequenceEqual(full.Groups.Select(g => g.Summaries["Amount:Sum"])));
            }
        });
        Test("heights.uniform-large-index-no-arrays", () => {
            var index = new RowHeightIndex(int.MaxValue, 28);
            Check(index.StorageMode == RowHeightStorageMode.Uniform && index.StorageEntryCount == 0);
            Near(28d * int.MaxValue, index.TotalHeight); Check(index.RowAt(28d * (int.MaxValue - 10)) == int.MaxValue - 10);
            Check(index.Visible(0, 100, int.MaxValue).Count == int.MaxValue);
        });
        Test("heights.sparse-single-override-is-bounded", () => {
            var index = new RowHeightIndex(1000000, 28); index.SetHeight(500000, 60);
            Check(index.StorageMode == RowHeightStorageMode.Sparse && index.StorageEntryCount <= 22);
            Near(28000032, index.TotalHeight); Near(14000000, index.OffsetOf(500000)); Check(index.RowAt(14000040) == 500000);
            index.SetHeight(500000, 28); Check(index.StorageMode == RowHeightStorageMode.Uniform && index.StorageEntryCount == 0);
        });
        Test("heights.sparse-to-dense-matches-reference", () => {
            const int n = 1024; var index = new RowHeightIndex(n, 28); double[] expected = Enumerable.Repeat(28d,n).ToArray();
            for (int i = 0; i < 100; i++) { expected[i * 7] = 18 + i; index.SetHeight(i * 7, expected[i * 7]); }
            Check(index.StorageMode == RowHeightStorageMode.Dense);
            double offset = 0;
            for (int i = 0; i < n; i++) { Near(offset,index.OffsetOf(i)); Check(index.RowAt(offset) == i); Near(expected[i], index[i]); offset += expected[i]; }
            Near(offset,index.TotalHeight);
            for (int i = 0; i < 100; i++) index.SetHeight(i * 7, 28);
            Check(index.StorageMode == RowHeightStorageMode.Uniform);
        });
        Test("heights.fractional-boundaries-exact-uniform", () => {
            foreach (double height in new[] { 0.1, 0.7, 28.3, 123.4567 })
            {
                var index = new RowHeightIndex(10000, height);
                for (int i = 1; i < 10000; i += 7)
                {
                    Check(index.RowAt(index.OffsetOf(i)) == i);
                    Check(index.RowAt(Math.BitDecrement(index.OffsetOf(i))) == i - 1);
                }
            }
        });
        Test("heights.override-value-preserved-exactly", () => {
            var index = new RowHeightIndex(1000, 1000000); index.SetHeight(0, 1e-10);
            Check(index[0] == 1e-10); long revision = index.Revision; index.SetHeight(0, 1e-10); Check(revision == index.Revision);
        });
        Test("heights.invalid-update-atomic", () => {
            var index = new RowHeightIndex(100); long revision = index.Revision;
            Reject<ArgumentOutOfRangeException>(() => index.SetHeight(100,30)); Reject<ArgumentOutOfRangeException>(() => index.SetHeight(1, double.NaN));
            Check(index.Revision == revision && index.OverrideCount == 0 && index.StorageEntryCount == 0);
        });
        Test("selection.cached-count-randomized-differential", () => {
            var table = Table(); using var view = new GridView(table); var selection = new GridSelection { Mode = GridSelectionMode.Cells }; var random = new Random(8128);
            for (int i = 0; i < 500; i++)
            {
                if (i % 31 == 0) selection.SelectAll(view);
                else if (i % 47 == 0) selection.RestoreState(view, selection.CaptureState());
                else if (i % 67 == 0) selection.Clear();
                else selection.Select(view, new(random.Next(100), new[] { "Group","Amount","Note" }[random.Next(3)]), extend: i % 5 == 0, toggle: i % 3 == 0);
                Check(selection.SelectedCellCount == selection.EnumerateCells().LongCount());
                Check(selection.EnumerateSelectedIds().Order().SequenceEqual(selection.SelectedIds.Order()));
            }
            table.Remove([1,2,3]); selection.Prune(table); Check(selection.SelectedCellCount == selection.EnumerateCells().LongCount());
            selection.Mode = GridSelectionMode.MultipleRows; Check(selection.SelectedCellCount == 0);
        });
        Test("sorting.computed-key-evaluated-once-per-record", () => {
            int evaluations = 0;
            var table = new GridTable(new GridSchema([new("Input", typeof(int)),
                new("Key", typeof(int)) { ValueGetter = row => { evaluations++; return -(int)row[0]!; } }]));
            table.AddRange(Enumerable.Range(0, 10000).Select(i => new GridRecord(i, new object?[] { i, null })));
            using var view = new GridView(table);
            view.SetQuery(new() { Sorts = [new("Key")] });
            Check(evaluations == 10000, $"Expected 10000 key evaluations, got {evaluations}");
            Check(view.FilteredRecords.Select(row => row.Id).SequenceEqual(Enumerable.Range(0, 10000).Reverse().Select(i => (long)i)));
        });
        Test("sorting.cached-keys-stable-multisort-and-comparer", () => {
            var table = new GridTable(new GridSchema([new("Input", typeof(int)),
                new("Key", typeof(int)) { ValueGetter = row => (int)row[0]! % 7 },
                new("Text", typeof(string)) { Comparer = Comparer<object?>.Create((a,b) => StringComparer.Ordinal.Compare((string?)a, (string?)b)) }]));
            table.AddRange(Enumerable.Range(0, 500).Select(i => new GridRecord(1000-i, new object?[] { i, null, i % 3 == 0 ? null : i % 2 == 0 ? "a" : "B" })));
            using var view = new GridView(table);
            foreach (var direction in new[] { SortDirection.Ascending, SortDirection.Descending })
            {
                view.SetQuery(new() { Sorts = [new("Key", direction), new("Text")] });
                var expected = direction == SortDirection.Ascending ? table.Records.OrderBy(row => (int)row[0]! % 7) : table.Records.OrderByDescending(row => (int)row[0]! % 7);
                Check(view.FilteredRecords.Select(row => row.Id).SequenceEqual(expected.ThenBy(row => (string?)row[2], StringComparer.Ordinal).Select(row => row.Id)));
            }
        });
        Test("sorting.computed-key-failure-does-not-publish", () => {
            bool fail = false;
            var table = new GridTable(new GridSchema([new("Input", typeof(int)), new("Key", typeof(int))
                { ValueGetter = row => fail && row.Id == 2 ? throw new InvalidOperationException("invalid key") : row[0] }]));
            table.AddRange(Enumerable.Range(0, 10).Select(i => new GridRecord(i, new object?[] { i, null })));
            using var view = new GridView(table); var rows = view.Rows; var query = view.Query; long builds = view.RefreshCount;
            fail = true; Reject<InvalidOperationException>(() => view.SetQuery(new() { Sorts = [new("Key")] }));
            Check(ReferenceEquals(rows, view.Rows) && ReferenceEquals(query, view.Query) && view.RefreshCount == builds);
            fail = false; view.SetQuery(new() { Sorts = [new("Key")] }); Check(view.Rows.Count == 10);
        });
        if (stress) Test("updates.stress-display-edits-100000-rows", () => {
            var table = Table(100000); using var view = new GridView(table); view.SetQuery(Grouped()); var rows = view.Rows; long builds = view.RefreshCount;
            for (int i = 0; i < 1000; i++) table.Apply([new(i, "Note", "tick" + i)]);
            Check(ReferenceEquals(rows,view.Rows) && view.RefreshCount == builds && view.ValueUpdateCount == 1000);
        });
        return ReviewValidationSuite.Append(new(results.AsReadOnly(), report.Milliseconds + watch.Elapsed.TotalMilliseconds), stress);
    }
    private sealed record ApplicationFilter : GridFilter
    { public override Func<GridRecord, bool> Compile(GridSchema schema) => row => !Equals(row[2], "hide"); }
    private sealed class NoteLength : IGridSummaryAccumulator
    {
        private int _length;
        public void Add(GridRecord row, object? value) => _length += ((string)row[1]!).Length;
        public object Complete() => _length;
    }
}

using DataGridScene.Core;
using System.Diagnostics;

namespace DataGridScene.Validation;

public static class FilterExecutionValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        void Check(bool value) { if (!value) throw new InvalidOperationException("Filter execution contract failed."); }
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new("filter-execution." + name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("filter-execution." + name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        var schema = new GridSchema([new("N", typeof(int))]);
        GridRecord Record(int n) => new(n + 1, new object?[] { n });
        Test("flat-and-is-shallow-and-replayable", () => {
            var filter = (FilterGroup)CriteriaParser.Parse(string.Join(" AND ", Enumerable.Range(0, 128).Select(i => $"[N] >= {i}")))!;
            Check(filter.All && filter.Children.Count == 128 && filter.Children.All(child => child is FieldFilter));
            var state = GridFilterState.Capture(schema, filter); var match = state.EffectiveFilter!.Compile(schema);
            Check(match(Record(127)) && !match(Record(126)));
        });
        Test("flat-or-is-shallow-and-replayable", () => {
            var filter = (FilterGroup)CriteriaParser.Parse(string.Join(" OR ", Enumerable.Range(0, 256).Select(i => $"[N] = {i}")))!;
            Check(!filter.All && filter.Children.Count == 256 && filter.Children.All(child => child is FieldFilter));
            using var view = new GridView(new GridTable(schema), new GridQuery { Filter = filter });
            var match = GridFilterState.Capture(schema, filter).EffectiveFilter!.Compile(schema);
            Check(match(Record(255)) && !match(Record(256)));
        });
        Test("mixed-precedence-and-not-retain-results", () => {
            var match = CriteriaParser.Parse("[N] = 1 OR [N] >= 3 AND NOT [N] > 5 OR [N] = 8")!.Compile(schema);
            Check(Enumerable.Range(0, 10).Where(i => match(Record(i))).SequenceEqual(new[] { 1, 3, 4, 5, 8 }));
        });
        Test("explicit-parentheses-keep-boolean-boundaries", () => {
            var filter = (FilterGroup)CriteriaParser.Parse("([N] = 1 OR [N] = 2) AND ([N] = 2 OR [N] = 3)")!;
            Check(filter.All && filter.Children.Count == 2 && filter.Children.All(child => child is FilterGroup { All: false }));
            Check(!filter.Compile(schema)(Record(1)) && filter.Compile(schema)(Record(2)));
        });
        Test("and-short-circuits-left-to-right", () => {
            var seen = new List<int>();
            var match = GridFilter.And(new Probe(_ => { seen.Add(1); return true; }), new Probe(_ => { seen.Add(2); return false; }), new Probe(_ => throw new Exception("Must not evaluate"))).Compile(schema);
            Check(!match(Record(0)) && seen.SequenceEqual(new[] { 1, 2 }));
        });
        Test("or-short-circuits-left-to-right", () => {
            var seen = new List<int>();
            var match = GridFilter.Or(new Probe(_ => { seen.Add(1); return false; }), new Probe(_ => { seen.Add(2); return true; }), new Probe(_ => throw new Exception("Must not evaluate"))).Compile(schema);
            Check(match(Record(0)) && seen.SequenceEqual(new[] { 1, 2 }));
        });
        Test("empty-and-single-child-semantics", () => {
            var record = Record(0); Check(GridFilter.And().Compile(schema)(record) && !GridFilter.Or().Compile(schema)(record));
            Func<GridRecord, bool> predicate = row => row.Id == record.Id;
            Check(ReferenceEquals(GridFilter.And(new Probe(predicate)).Compile(schema), predicate));
            Check(ReferenceEquals(GridFilter.Or(new Probe(predicate)).Compile(schema), predicate));
            Check(CriteriaParser.Parse("[N] = 1") is FieldFilter);
        });
        Test("compile-all-children-once-before-evaluation", () => {
            int compiled = 0; var a = new Probe(_ => false, () => compiled++); var b = new Probe(_ => true, () => compiled++);
            var match = GridFilter.And(a, b).Compile(schema); Check(compiled == 2);
            for (int i = 0; i < 100; i++) Check(!match(Record(i)));
            Check(compiled == 2);
        });
        Test("compiled-delegates-own-original-child-sequence", () => {
            GridFilter[] children = [new Probe(_ => true), new Probe(_ => true)]; var match = new FilterGroup(true, children).Compile(schema);
            children[0] = new Probe(_ => false); Check(match(Record(0)));
            var parsed = (FilterGroup)CriteriaParser.Parse("[N] = 1 OR [N] = 2")!;
            bool rejected = false; try { ((IList<GridFilter>)parsed.Children)[0] = children[0]; } catch (NotSupportedException) { rejected = true; }
            Check(rejected);
        });
        Test("group-evaluation-has-no-per-row-capturing-delegate", () => {
            var yes = new Probe(_ => true); var no = new Probe(_ => false); var record = Record(0);
            var all = GridFilter.And(yes, yes, yes).Compile(schema); var any = GridFilter.Or(no, no, yes).Compile(schema);
            for (int i = 0; i < 1000; i++) { all(record); any(record); }
            long before = GC.GetAllocatedBytesForCurrentThread(); int matches = 0;
            for (int i = 0; i < 10000; i++) { if (all(record)) matches++; if (any(record)) matches++; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (matches != 20000 || allocated > 4096) throw new InvalidOperationException($"Expected allocation-flat group evaluation, observed {allocated} bytes for 20000 calls.");
        });
        Test("complexity-and-input-size-limits-remain-enforced", () => {
            Check(!CriteriaParser.TryParse(new string('(', 40) + "[N]=1" + new string(')', 40), out _, out _));
            Check(!CriteriaParser.TryParse(string.Join(" OR ", Enumerable.Repeat("N=1", 1025)), out _, out _));
            Check(!CriteriaParser.TryParse(new string('x', 16385), out _, out _));
            Check(!CriteriaParser.TryParse("N=1 OR", out _, out _));
        });
        Test("flat-conditions-survive-history-and-text-description", () => {
            var filter = CriteriaParser.Parse(string.Join(" OR ", Enumerable.Range(0, 100).Select(i => $"[N] = {i}")));
            var history = new GridFilterHistory(schema); var entry = history.Remember(GridFilterState.Capture(schema, filter))!;
            Check(GridFilterText.Describe(entry.State.EffectiveFilter, schema, 16384).Contains("99"));
            var repeated = history.Remember(GridFilterState.Capture(schema, filter)); Check(repeated!.Id == entry.Id && history.Entries.Count == 1);
            var match = repeated.State.EffectiveFilter!.Compile(schema); Check(match(Record(99)) && !match(Record(100)));
        });
        return results.AsReadOnly();
    }
    private sealed record Probe(Func<GridRecord, bool> Predicate, Action? OnCompile = null) : GridFilter
    {
        public override Func<GridRecord, bool> Compile(GridSchema schema) { OnCompile?.Invoke(); return Predicate; }
    }
}

using DataGridScene.Core;
using DataGridScene.Export;
using DataGridScene.Layout;
using System.Diagnostics;

namespace DataGridScene.Validation;

/// <summary>Non-gating microbenchmarks. Same source can run against the baseline package DLLs.</summary>
public static class PerformanceBenchmarks
{
    public sealed record Sample(string Name, int Operations, double MedianMilliseconds, long MedianAllocatedBytes,
        IReadOnlyList<double> Milliseconds, IReadOnlyList<long> AllocatedBytes);
    private static double _sink;
    public static IReadOnlyList<Sample> Run()
    {
        var samples = new List<Sample>();
        void Measure(string name, int operations, Action action)
        {
            action(); // Warm the actual operation, not only fixture construction.
            var times = new double[5]; var allocations = new long[5];
            for (int round = 0; round < times.Length; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long before = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < operations; i++) action();
                times[round] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocations[round] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            samples.Add(new(name, operations, times.Order().ElementAt(2), allocations.Order().ElementAt(2), times, allocations));
        }
        var table = new GridTable(new GridSchema([new("Group", typeof(int)), new("Amount", typeof(int)), new("Note", typeof(string))]));
        table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i % 5, i, "Note " + i })));
        using var view = new GridView(table);
        view.SetQuery(new() { Groups = [new("Group")], Sorts = [new("Amount")], Summaries = [new("Amount", SummaryKind.Sum)] });
        int tick = 0;
        Measure("display-edits-100000-grouped-rows", 20, () => { table.Apply([new(1, "Note", "Tick " + ++tick)]); _sink = view.Rows.Count; });
        var heights = new RowHeightIndex();
        Measure("reset-1000000-uniform-row-heights", 10, () => { heights.Reset(1000000,28); _sink = heights.OffsetOf(999999) + heights.RowAt(12345); });
        var selection = new GridSelection { Mode = GridSelectionMode.Cells }; selection.SelectAll(view);
        Measure("read-count-300000-selected-cells", 1000, () => _sink = selection.SelectedCellCount);
        var computed = new GridTable(new GridSchema([
            new("Quantity", typeof(int)), new("Price", typeof(decimal)),
            new("Total", typeof(decimal)) { ValueGetter = row => Math.Round((int)row[0]! * (decimal)row[1]!, 2) }
        ]));
        computed.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i % 80 + 1, i % 1000 + 0.125m, null })));
        using var computedView = new GridView(computed); computedView.SetQuery(new() { Sorts = [new("Total", SortDirection.Descending)] });
        int quantity = 1;
        Measure("reshape-100000-computed-sorted-rows", 3, () => { computed.Apply([new(0, "Quantity", ++quantity)]); _sink = computedView.Rows.Count; });
        var copied = new GridSelection(); copied.Select(view, new(99999, "Note"));
        string[] fields = ["Note", "Amount"];
        Measure("copy-one-selected-row-from-100000", 50, () => _sink = GridClipboard.Copy(view, copied, fields).Length);
        copied.Mode = GridSelectionMode.Cells; copied.Select(view, new(99999, "Note"));
        Measure("copy-one-selected-cell-from-100000", 50, () => _sink = GridClipboard.Copy(view, copied, fields).Length);
        return samples.AsReadOnly();
    }
}

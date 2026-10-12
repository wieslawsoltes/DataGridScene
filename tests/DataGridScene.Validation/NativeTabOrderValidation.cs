using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using System.Diagnostics;

namespace DataGridScene.Validation;

/// <summary>Platform-independent focus geometry contracts, also run in actual WASM.</summary>
public static class NativeTabOrderValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Native tab-order contract failed."); }
        void Test(string name, Action test)
        {
            long start = Stopwatch.GetTimestamp();
            try { test(); results.Add(new(name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table(int count = 4)
        {
            var table = new GridTable(new GridSchema([new("A", typeof(int)), new("B", typeof(int)), new("C", typeof(int))]));
            table.AddRange(Enumerable.Range(0, count).Select(i => new GridRecord(1000 + i, new object?[] { i, i % 2, -i })));
            return table;
        }
        static GridFrame Frame(GridView view, GridLayout layout, double x = 0, double y = 0, double width = 650, double height = 280) =>
            new(view, layout, new(), new RowHeightIndex(view.Rows.Count), new(), width, height, x, y);
        static IReadOnlyList<NativeCellPlacement> Collect(GridFrame frame) =>
            NativeCellGeometry.Collect(frame, new HashSet<string> { "A", "B", "C" });
        static void Ranked(IReadOnlyList<NativeCellPlacement> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                Check(cells[i].TabIndex == i);
                Check(cells[i].ClipBounds.Width > 0 && cells[i].ClipBounds.Height > 0);
                if (i > 0) Check(cells[i].Bounds.Y > cells[i - 1].Bounds.Y ||
                    cells[i].Bounds.Y == cells[i - 1].Bounds.Y && cells[i].ClipBounds.X > cells[i - 1].ClipBounds.X);
            }
        }
        Test("native-tab.row-major-ranks-ignore-painter-order", () => {
            using var view = new GridView(Table()); var layout = new GridLayout(view.Table.Schema);
            layout.SetPin("C", ColumnPin.Left); layout.SetPin("A", ColumnPin.Right);
            var cells = Collect(Frame(view, layout)); Ranked(cells);
            Check(cells.Take(3).Select(c => c.Address.Field).SequenceEqual(new[] { "C", "B", "A" }));
            Check(cells[0].Address.RecordId == 1000 && cells[3].Address.RecordId == 1001);
        });
        Test("native-tab.reorder-follows-visible-columns", () => {
            using var view = new GridView(Table()); var layout = new GridLayout(view.Table.Schema);
            layout.MoveBefore("C", "A"); var cells = Collect(Frame(view, layout)); Ranked(cells);
            Check(cells.Take(3).Select(c => c.Address.Field).SequenceEqual(new[] { "C", "A", "B" }));
        });
        Test("native-tab.sorted-rows-do-not-use-record-ids", () => {
            using var view = new GridView(Table(), new() { Sorts = [new("A", SortDirection.Descending)] });
            var cells = Collect(Frame(view, new(view.Table.Schema))); Ranked(cells);
            Check(cells[0].Address.RecordId == 1003 && cells[3].Address.RecordId == 1002);
        });
        Test("native-tab.group-and-detail-rows-have-no-cell-ranks", () => {
            using var view = new GridView(Table(), new() { Groups = [new("B")] }); view.ToggleDetail(1000);
            var frame = Frame(view, new(view.Table.Schema), height: 700); var cells = Collect(frame); Ranked(cells);
            Check(cells.Count == 12 && cells.All(c => view.Rows[view.IndexOfRecord(c.Address.RecordId)].Kind == GridRowKind.Data));
        });
        Test("native-tab.clipped-subrow-scroll-keeps-ranks", () => {
            using var view = new GridView(Table(100)); var layout = new GridLayout(view.Table.Schema); layout.SetPin("C", ColumnPin.Right);
            var before = Collect(Frame(view, layout, x: 40, y: .1, width: 330));
            var after = Collect(Frame(view, layout, x: 41, y: .2, width: 330)); Ranked(before); Ranked(after);
            Check(before.Select(c => (c.Address, c.TabIndex)).SequenceEqual(after.Select(c => (c.Address, c.TabIndex))));
        });
        Test("native-tab.only-requested-visible-columns-are-ranked", () => {
            using var view = new GridView(Table()); var layout = new GridLayout(view.Table.Schema); layout.SetVisible("B", false);
            var cells = NativeCellGeometry.Collect(Frame(view, layout), new HashSet<string> { "B", "C" }); Ranked(cells);
            Check(cells.Count == 4 && cells.All(c => c.Address.Field == "C"));
            Check(NativeCellGeometry.Collect(Frame(view, layout), new HashSet<string> { "NotAColumn" }).Count == 0);
        });
        Test("native-tab.deep-scroll-ranks-are-viewport-bounded", () => {
            using var view = new GridView(Table(100000)); var cells = Collect(Frame(view, new(view.Table.Schema), y: 1400000)); Ranked(cells);
            Check(cells.Count is > 0 and < 30 && cells[0].TabIndex == 0 && cells[0].Address.RecordId > 40000);
        });
        Test("native-tab.limit-rejects-instead-of-reusing-ranks", () => {
            using var view = new GridView(Table()); var frame = Frame(view, new(view.Table.Schema));
            try { NativeCellGeometry.Collect(frame, new HashSet<string> { "A", "B", "C" }, 2); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Native cell limit was not enforced.");
        });
        return results.AsReadOnly();
    }
}

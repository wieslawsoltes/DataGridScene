using DataGridScene.Layout;
using System.Diagnostics;

namespace DataGridScene.Validation;

public static class RowEditLayoutValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        void Test(string name, Action run)
        {
            var start = Stopwatch.GetTimestamp();
            try { run(); results.Add(new(name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Row layout contract failed."); }
        static void Reject<T>(Action run) where T : Exception
        { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        Test("row-layout.default-keeps-single-column-source-order", () => {
            var plan = new RowEditLayout(["A", "B", "C"]).Arrange(1000);
            Check(plan.ColumnCount == 1 && plan.RowCount == 3 && plan.Fields.Select(p => p.Row).SequenceEqual(new[] { 0, 1, 2 }));
        });
        Test("row-layout.width-thresholds-and-extreme-width", () => {
            var layout = new RowEditLayout(["A"], maximumColumnCount: 3, minimumColumnWidth: 200, columnSpacing: 10);
            Check(layout.GetColumnCount(0) == 1 && layout.GetColumnCount(409.99) == 1 && layout.GetColumnCount(410) == 2);
            Check(layout.GetColumnCount(620) == 3 && layout.GetColumnCount(double.MaxValue) == 3);
            foreach (double width in new[] { -1d, double.NaN, double.PositiveInfinity }) Reject<ArgumentOutOfRangeException>(() => layout.Arrange(width));
        });
        Test("row-layout.configuration-is-owned-and-canonical", () => {
            var names = new List<string> { "Name", "Amount" }; var rules = new List<RowEditFieldLayout> { new("amount") { Order = -1, Caption = "Net total" } };
            var layout = new RowEditLayout(names, rules); names.Clear(); rules.Clear();
            Check(layout.Fields[0].Field == "Amount" && layout.Fields[0].Caption == "Net total" && layout.Fields[1].Field == "Name");
        });
        Test("row-layout.equal-orders-remain-stable", () => {
            var layout = new RowEditLayout(["A", "B", "C"], [new("C") { Order = -1 }]);
            Check(layout.Fields.Select(f => f.Field).SequenceEqual(new[] { "C", "A", "B" }));
        });
        Test("row-layout.hidden-fields-and-start-new-row", () => {
            var plan = new RowEditLayout(["A", "B", "C", "D"], [new("B") { IsVisible = false }, new("C") { StartNewRow = true }], 3, 200).Arrange(1000);
            Check(plan.Fields.Select(f => f.Field).SequenceEqual(new[] { "A", "C", "D" }));
            Check(plan.Fields[1].Row == 1 && plan.Fields[1].Column == 0 && plan.Fields[2].Column == 1);
        });
        Test("row-layout.spans-reserve-rows-and-collapse-on-narrow-screen", () => {
            var layout = new RowEditLayout(["A", "B", "C", "D"], [new("A") { ColumnSpan = 2, RowSpan = 2 }], 3, 200);
            var wide = layout.Arrange(1000); Check(wide.Fields[0].ColumnSpan == 2 && wide.Fields[1].Column == 2);
            Check(wide.Fields[2].Row == 1 && wide.Fields[2].Column == 2 && wide.Fields[3].Row == 2);
            var narrow = layout.Arrange(180); Check(narrow.Fields[0].ColumnSpan == 1 && narrow.Fields[1].Row == 2);
        });
        Test("row-layout.rejects-invalid-fields-and-overrides", () => {
            Reject<ArgumentException>(() => new RowEditLayout([]));
            Reject<ArgumentException>(() => new RowEditLayout(["A", "a"]));
            Reject<ArgumentException>(() => new RowEditLayout(["A"], [new("Missing")]));
            Reject<ArgumentException>(() => new RowEditLayout(["A"], [new("A"), new("a")]));
            Reject<ArgumentOutOfRangeException>(() => new RowEditLayout(["A"], [new("A") { RowSpan = 0 }]));
            Reject<ArgumentOutOfRangeException>(() => new RowEditLayout(["A"], [new("A") { ColumnSpan = 5 }]));
        });
        Test("row-layout.rejects-invalid-options", () => {
            Reject<ArgumentOutOfRangeException>(() => new RowEditLayout(["A"], maximumColumnCount: 0));
            Reject<ArgumentOutOfRangeException>(() => new RowEditLayout(["A"], minimumColumnWidth: double.NaN));
            Reject<ArgumentOutOfRangeException>(() => new RowEditLayout(["A"], columnSpacing: -1));
        });
        Test("row-layout.all-hidden-produces-empty-plan", () => {
            var plan = new RowEditLayout(["A"], [new("A") { IsVisible = false }]).Arrange(300);
            Check(plan.RowCount == 0 && plan.Fields.Count == 0);
        });
        Test("row-layout.randomized-packing-is-bounded-and-nonoverlapping", () => {
            var random = new Random(971);
            var names = Enumerable.Range(0, 256).Select(i => "F" + i).ToArray();
            var rules = names.Select(f => new RowEditFieldLayout(f) { ColumnSpan = random.Next(1, 5), RowSpan = random.Next(1, 9),
                StartNewRow = random.Next(4) == 0, IsVisible = random.Next(5) != 0, Order = random.Next(-5, 6) }).ToArray();
            var layout = new RowEditLayout(names, rules, 4, 180);
            foreach (double width in new[] { 0d, 360, 570, 1024, 8192 })
            {
                var plan = layout.Arrange(width); var cells = new HashSet<(int, int)>();
                Check(plan.RowCount <= 2048 && plan.Fields.Count == rules.Count(r => r.IsVisible));
                Check(plan.Fields.Select(f => f.Field).SequenceEqual(layout.Fields.Where(f => f.IsVisible).Select(f => f.Field)));
                foreach (var field in plan.Fields)
                    for (int row = field.Row; row < field.Row + field.RowSpan; row++)
                        for (int col = field.Column; col < field.Column + field.ColumnSpan; col++)
                            Check(row < plan.RowCount && col < plan.ColumnCount && cells.Add((row, col)));
                Check(plan.Fields.SequenceEqual(layout.Arrange(width).Fields));
            }
            Reject<ArgumentException>(() => new RowEditLayout(names.Append("overflow")));
        });
        return results.AsReadOnly();
    }
}

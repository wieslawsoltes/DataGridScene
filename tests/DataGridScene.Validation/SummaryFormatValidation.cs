using DataGridScene.Core;
using System.Diagnostics;

namespace DataGridScene.Validation;

internal static class SummaryFormatValidation
{
    internal static ValidationReport Append(ValidationReport previous)
    {
        var clock = Stopwatch.StartNew();
        var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("summary-format.reject-excessive-precision-before-formatting", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            foreach (string format in new[] { "{0:F1025}", "{0:E999999999}", "{0:D2147483647}", "{0:G999999999}", "{0:X1000000}" })
            {
                var before = view.Rows; int revision = view.Revision;
                try { view.SetQuery(new() { Summaries = [new("Amount", SummaryKind.Sum) { DisplayFormat = format }] }); }
                catch (FormatException)
                {
                    if (!ReferenceEquals(before, view.Rows) || view.Revision != revision) throw new Exception("Rejected format mutated the view.");
                    continue;
                }
                throw new Exception("Excessive precision was accepted: " + format);
            }
        });
        Test("summary-format.literal-commas-and-escaped-braces-are-not-alignment", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var summary = new SummaryDescriptor("Amount", SummaryKind.Sum) { DisplayFormat = "Budget ,999999999: {{0:F999999999}} {0:N2}" };
            view.SetQuery(new() { Summaries = [summary] });
            if (summary.Display(view.Summaries[summary.Key], view.Table.Schema) != "Budget ,999999999: {0:F999999999} 60.00")
                throw new Exception("Literal formatting changed.");
        });
        Test("summary-format.bounded-precision-and-alignment-are-supported", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var summary = new SummaryDescriptor("Amount", SummaryKind.Sum) { DisplayFormat = "{0,4096:F1024}" };
            view.SetQuery(new() { Summaries = [summary] });
            string text = summary.Display(view.Summaries[summary.Key], view.Table.Schema);
            if (text.Length != 4096 || !text.TrimStart().StartsWith("60.000", StringComparison.Ordinal))
                throw new Exception("Bounded format was not preserved.");
        });
        return NumericValidation.Append(new(results.AsReadOnly(), previous.Milliseconds + clock.Elapsed.TotalMilliseconds));
    }
}

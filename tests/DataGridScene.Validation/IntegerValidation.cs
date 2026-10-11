using DataGridScene.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Validation;

internal static class IntegerValidation
{
    private static void Check(bool condition, string message = "Assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    internal static ValidationReport Append(ValidationReport previous)
    {
        var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, timer.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("numeric.integer-conversion-rejects-near-integer-floats", () => {
            var column = new GridColumn("Value", typeof(long));
            foreach (double value in new[] { Math.BitIncrement(1d), Math.BitDecrement(1d), double.Epsilon, -double.Epsilon })
                Check(!column.TryConvert(value, CultureInfo.InvariantCulture, out _, out _), "Fractional binary input was silently rounded.");
            Check(column.TryConvert(10000000000000002d, CultureInfo.InvariantCulture, out var exact, out _));
            Check(Equals(exact, 10000000000000002L));
        });
        Test("numeric.integer-json-rejects-rounded-fraction", () => {
            var column = new GridColumn("Value", typeof(int));
            foreach (string text in new[] { "1.0000000000000000000000000000001", "1e-300", "-1e-300", "0.9999999999999999999999999999999" }) {
                using var json = JsonDocument.Parse(text);
                Check(!column.TryConvert(json.RootElement, CultureInfo.InvariantCulture, out _, out _), "Fractional JSON input was silently rounded: " + text);
            }
        });
        Test("numeric.integer-json-exponent-and-zero-semantics", () => {
            var column = new GridColumn("Value", typeof(int));
            foreach (var (text, expected) in new[] { ("1.000e1", 10), ("1000e-3", 1), ("0.000e-30", 0), ("-12.00", -12) }) {
                using var json = JsonDocument.Parse(text);
                Check(column.TryConvert(json.RootElement, CultureInfo.InvariantCulture, out var actual, out var error), error ?? "Conversion failed.");
                Check(Equals(actual, expected));
            }
        });
        return new(results.AsReadOnly(), previous.Milliseconds + clock.Elapsed.TotalMilliseconds);
    }
}

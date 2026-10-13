using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateRowEditorStaging()
    {
        var results = RowBatchStagingValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(r => r.Milliseconds)), Json);
    }
    [JSExport]
    public static string ValidateRowEditorControls() => JsonSerializer.Serialize(RowEditorControlChecks.Run(), Json);
    [JSExport]
    public static string SnapshotCustomRowEditors() => JsonSerializer.Serialize(Page.CustomRowEditorSnapshot(), Json);
    [JSExport]
    public static string ConfigureCustomRowEditorFixture(bool native, int count, string culture, bool nullNotes)
    {
        try
        {
            if (count is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(count));
            Page.ResetCustomRowEditorDiagnostics();
            var grid = Page.DataGrid; grid.CancelEdit(); grid.IsReadOnly = false; grid.ClearCellTemplates();
            var schema = new GridSchema([
                new("Company", typeof(string)) { AllowNull = false, Width = 240 },
                new("Quantity", typeof(int)) { AllowNull = false }, new("Limit", typeof(int)) { AllowNull = false },
                new("Notes", typeof(string)), new("Locked", typeof(string)) { ReadOnly = true },
                new("Amount", typeof(decimal)) { AllowNull = false }, new("Key", typeof(long)) { AllowNull = false },
                new("Stamp", typeof(DateTimeOffset)) { AllowNull = false }, new("Status", typeof(string)) { AllowNull = false, Choices = ["New", "Closed"] }
            ], CultureInfo.GetCultureInfo(culture)) { RowValidator = row => (int)row[1]! > (int)row[2]! ? "Quantity must not exceed Limit." : null };
            var table = new GridTable(schema);
            table.AddRange(Enumerable.Range(0, count).Select(i => new GridRecord(10001 + i, new object?[] {
                i == 0 ? "Original" : "Row " + i, 2, 5, nullNotes ? null : "First line\nSecond line", "Fixed",
                1.2345678901234567890123456789m, 9007199254740993L,
                DateTimeOffset.Parse("2026-10-13T10:20:30.1234567+02:00", CultureInfo.InvariantCulture), "New"
            })));
            grid.Table = table; grid.DetailProvider = null;
            grid.CellRenderingMode = native ? GridCellRenderingMode.NativeControls : GridCellRenderingMode.Skia;
            grid.Selection.Select(grid.View!, new(10001, "Company"));
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
    [JSExport]
    public static string ChangeCustomRowEditorFixture(string action)
    {
        try
        {
            if (action == "reader-fails") Page.SetCustomRowReaderFault(true);
            else if (action == "reader-recovers") Page.SetCustomRowReaderFault(false);
            else throw new ArgumentException("Unknown custom editor fixture mutation.");
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
}

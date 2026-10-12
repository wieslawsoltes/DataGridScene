using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateRowEditing()
    {
        var results = RowEditValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(result => result.Milliseconds)), Json);
    }
    [JSExport]
    public static string ValidateRowEditControls() => JsonSerializer.Serialize(RowEditControlChecks.Run(), Json);
    [JSExport]
    public static string ConfigureRowEditFixture(bool native, int count, string culture)
    {
        try
        {
            if (count < 1 || count > 100000) throw new ArgumentOutOfRangeException(nameof(count));
            var grid = Page.DataGrid;
            if (grid.ActiveRowEditForm is not null) throw new InvalidOperationException("Close the form first.");
            grid.CancelEdit(); grid.IsReadOnly = false; grid.ClearCellTemplates();
            var schema = new GridSchema([
                new("Company", typeof(string)) { AllowNull = false, Width = 240 },
                new("Quantity", typeof(int)) { AllowNull = false }, new("Limit", typeof(int)) { AllowNull = false },
                new("Verified", typeof(bool)), new("Status", typeof(string)) { AllowNull = false, Choices = ["New", "Closed"] },
                new("Amount", typeof(decimal)) { AllowNull = false }, new("Key", typeof(long)) { AllowNull = false },
                new("Stamp", typeof(DateTimeOffset)) { AllowNull = false }, new("Optional", typeof(string)),
                new("Locked", typeof(string)) { ReadOnly = true }
            ], CultureInfo.GetCultureInfo(culture)) { RowValidator = values => (int)values[1]! > (int)values[2]! ? "Quantity must not exceed Limit." : null };
            var table = new GridTable(schema);
            table.AddRange(Enumerable.Range(0, count).Select(i => new GridRecord(10001 + i, new object?[] {
                i == 0 ? "Original" : "Row " + i, 2, 5, false, "New", 1.2345678901234567890123456789m, 9007199254740993L,
                DateTimeOffset.Parse("2026-10-12T10:20:30.1234567+02:00", CultureInfo.InvariantCulture), null, "Fixed"
            })));
            grid.Table = table; grid.DetailProvider = null;
            grid.CellRenderingMode = native ? GridCellRenderingMode.NativeControls : GridCellRenderingMode.Skia;
            grid.Selection.Select(grid.View!, new(10001, "Company"));
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
    [JSExport]
    public static string SnapshotRowEditing()
    {
        var grid = Page.DataGrid; var form = grid.ActiveRowEditForm;
        static string? Exact(object? value) => value switch {
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture), DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
        return JsonSerializer.Serialize(new {
            open = form is not null, message = form?.ValidationMessage, errors = form?.Buffer.Errors,
            offset = form?.FieldsViewport.VerticalOffset, actions = grid.RowEditActionBounds(Page),
            fields = form?.Editors.Select(pair => new { field = pair.Key, type = pair.Value.GetType().Name,
                text = (pair.Value as TextBox)?.Text, check = (pair.Value as CheckBox)?.IsChecked,
                choice = (pair.Value as ComboBox)?.SelectedItem, readOnly = (pair.Value as TextBox)?.IsReadOnly }).ToArray(),
            values = grid.Table?.Records.Take(2).Select(row => new { row.Id, row.Version,
                values = grid.Table.Schema.Columns.ToDictionary(column => column.Field, column => column.GetValue(row) is { } value ? Exact(value) : null) }).ToArray(),
            revision = grid.Table?.Revision, canUndo = grid.History?.CanUndo, canRedo = grid.History?.CanRedo,
            grid.CreatedCellControlCount, grid.CellControlTreeAttachCount, grid.CellControlTreeDetachCount,
            grid.RealizedCellControlCount, grid.FrameBuildCount, grid.RowGeometryBuildCount
        }, Json);
    }
    [JSExport]
    public static string ChangeRowEditFixture(string action)
    {
        try
        {
            var grid = Page.DataGrid; var table = grid.Table!;
            switch (action)
            {
                case "conflict": table.Apply([new(10001, "Limit", 10)]); break;
                case "replace": var values = table[10001].CopyValues(); table.Remove([10001]); table.Add(new(10001, values)); break;
                case "replace-table": var replacement = new GridTable(table.Schema); replacement.AddRange(table.Records); grid.Table = replacement; break;
                case "readonly": grid.IsReadOnly = true; break;
                case "editable": grid.IsReadOnly = false; break;
                default: throw new ArgumentException("Unknown fixture mutation.");
            }
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
}

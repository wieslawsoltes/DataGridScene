using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using DataGridScene.Validation;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ColumnChooserSnapshot() => JsonSerializer.Serialize(Page.ColumnChooserSnapshot(), Json);
    [JSExport]
    public static string ValidateColumnChooser() => JsonSerializer.Serialize(ColumnChooserControlChecks.Run(), Json);
    [JSExport]
    public static string ValidateFilterExecution()
    {
        var results = FilterExecutionValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(result => result.Milliseconds)), Json);
    }
    [JSExport]
    public static void ConfigureColumnChooserFixture(int columns) => Page.ConfigureColumnChooserFixture(columns);
    [JSExport]
    public static void SetExternalColumnWidth(string field, double width) => Page.DataGrid.ColumnLayout!.SetWidth(field, width);
}

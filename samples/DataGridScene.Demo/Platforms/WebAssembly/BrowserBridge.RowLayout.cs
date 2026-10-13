using DataGridScene.Validation;
using Microsoft.UI.Xaml.Automation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateRowLayout()
    {
        var results = RowEditLayoutValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(r => r.Milliseconds)), Json);
    }
    [JSExport]
    public static string ValidateRowLayoutControls() => JsonSerializer.Serialize(RowEditLayoutControlChecks.Run(), Json);
    [JSExport]
    public static string SnapshotRowLayout()
    {
        var form = Page.DataGrid.ActiveRowEditForm;
        // Only read existing instances and the last applied layout. Do not force
        // layout, run the planner, materialize editors or alter focus in snapshots.
        return JsonSerializer.Serialize(form is null ? null : new {
            width = form.ActualWidth, columns = form.LayoutPlan.ColumnCount, rows = form.LayoutPlan.RowCount,
            reflows = form.LayoutReflowCount, writes = form.FieldPlacementWriteCount,
            fields = form.LayoutPlan.Fields.Select(field => new { field.Field, field.Row, field.Column, field.RowSpan, field.ColumnSpan,
                caption = AutomationProperties.GetName(form.Editors[field.Field]),
                instance = RuntimeHelpers.GetHashCode(form.Editors[field.Field]) }).ToArray(),
            actions = Page.DataGrid.RowEditActionBounds(Page)
        }, Json);
    }
}

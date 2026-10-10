using DataGridScene.Core;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

/// <summary>Diagnostics for the running C# application; tests still send real browser pointer and keyboard input.</summary>
public static partial class BrowserBridge
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static MainPage Page => App.Page ?? throw new InvalidOperationException("Application is not ready.");
    [JSExport]
    public static string Snapshot()
    {
        var page = Page; var grid = page.DataGrid; var view = grid.View; var frame = grid.Frame; var origin = grid.SurfaceOrigin();
        return JsonSerializer.Serialize(new
        {
            ready = page.Ready, page.Scenario, page.RecordCount, filteredCount = view?.FilteredRecords.Count, viewRowCount = view?.Rows.Count,
            page.Message, grid.LastError, grid.IsEditing, grid.IsEditorReady, grid.EditorText, grid.EditingField, grid.EditingRecordId,
            selectionMode = grid.SelectionMode.ToString(), selectedCellCount = grid.Selection.SelectedCellCount,
            focus = grid.Selection.FocusedCell, selectedIds = grid.Selection.SelectedIds.Take(100).ToArray(), selectedCount = grid.Selection.Count,
            canUndo = grid.History?.CanUndo, canRedo = grid.History?.CanRedo,
            grid.HorizontalOffset, grid.VerticalOffset, grid.RowHeight, grid.ShowBands, grid.DarkTheme,
            query = view?.Query, columns = grid.ColumnLayout?.Columns,
            records = view?.FilteredRecords.Take(12).Select(record => new { record.Id, record.Version, values = grid.Table!.Schema.Columns.Where(column => column.Field != "Trend").ToDictionary(column => column.Field, column => column.GetValue(record)) }).ToArray(),
            origin = new { origin.X, origin.Y },
            frame = frame is null ? null : new
            {
                frame.Width, frame.Height, frame.HeaderTop, frame.FilterTop, frame.BodyTop, frame.FooterTop,
                columns = frame.Columns.Slots, groups = frame.GroupChips,
                rows = Enumerable.Range(frame.VisibleRows.Start, frame.VisibleRows.Count).Select(index => new
                {
                    index, kind = view!.Rows[index].Kind.ToString(), id = view.Rows[index].Record?.Id, key = view.Rows[index].Key,
                    bounds = frame.RowBounds(index), cells = frame.Columns.Slots.ToDictionary(column => column.Field, column => frame.CellBounds(index, column.Field))
                }).ToArray()
            },
            rendering = new { grid.Renderer.CellsDrawn, grid.Renderer.RowsDrawn, grid.Renderer.DrawCount, grid.Renderer.CachedTextRuns, grid.Renderer.LastDrawMilliseconds, grid.Renderer.FontFamily },
            buttons = page.ActionBounds()
        }, Json);
    }
    [JSExport]
    public static string Command(string name, string payload)
    {
        try
        {
            using var data = JsonDocument.Parse(payload); var input = data.RootElement; var page = Page; var grid = page.DataGrid;
            string Text(string key) => input.GetProperty(key).GetString() ?? "";
            switch (name)
            {
                case "reset": page.Reload(input.TryGetProperty("count", out var count) ? count.GetInt32() : 10000); break;
                case "scenario": page.ApplyScenario(Text("name")); break;
                case "selection": grid.SelectionMode = Enum.Parse<GridSelectionMode>(Text("mode")); break;
                case "search": grid.SetSearch(Text("value")); break;
                case "filter": grid.SetColumnFilter(Text("field"), Text("value")); break;
                case "criteria": grid.SetCriteria(Text("value")); break;
                case "clear": page.ClearFilters(); break;
                case "focus":
                    var cell = new CellAddress(input.GetProperty("id").GetInt64(), Text("field")); grid.Selection.Select(grid.View!, cell); grid.EnsureVisible(cell); grid.Focus(FocusState.Programmatic); break;
                case "scroll": grid.ScrollTo(input.GetProperty("x").GetDouble(), input.GetProperty("y").GetDouble()); break;
                case "group": grid.GroupColumn(Text("field")); break;
                case "pin": grid.ColumnLayout!.SetPin(Text("field"), Enum.Parse<ColumnPin>(Text("pin"))); break;
                case "visible": grid.ColumnLayout!.SetVisible(Text("field"), input.GetProperty("value").GetBoolean()); break;
                case "theme": grid.DarkTheme = input.GetProperty("dark").GetBoolean(); break;
                case "bands": grid.ShowBands = input.GetProperty("value").GetBoolean(); break;
                case "restore": grid.RestoreLayout(Text("value")); break;
                case "layout": return JsonSerializer.Serialize(new { ok = true, layout = grid.SaveLayout() }, Json);
                case "export": page.Export(Text("format")); break;
                case "paste": grid.PasteText(Text("value")); break;
                default: throw new ArgumentException("Unknown diagnostic command: " + name);
            }
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
    [JSExport]
    public static async Task<string> Validate() => JsonSerializer.Serialize(await Validation.ExtendedValidationSuite.RunAsync(), Json);
}

using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
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
            chrome = page.ChromeSnapshot(),
            filterPanel = new { visible = grid.FilterPanel.Visibility == Visibility.Visible, grid.FilterPanel.Items, grid.FilterPanel.IsFilterEnabled, grid.FilterPanel.ContentBuildCount },
            ready = page.Ready, page.Scenario, page.RecordCount, filteredCount = view?.FilteredRecords.Count, viewRowCount = view?.Rows.Count,
            page.Message, grid.LastError, grid.IsEditing, grid.IsEditorReady, grid.EditorText, grid.EditingField, grid.EditingRecordId,
            selectionMode = grid.SelectionMode.ToString(), selectedCellCount = grid.Selection.SelectedCellCount,
            focus = grid.Selection.FocusedCell, selectedIds = grid.Selection.EnumerateSelectedIds().Take(100).ToArray(), selectedCount = grid.Selection.Count,
            canUndo = grid.History?.CanUndo, canRedo = grid.History?.CanRedo,
            grid.HorizontalOffset, grid.VerticalOffset, grid.RowHeight, grid.ShowBands, grid.DarkTheme,
            groupFooterCount = view?.Query.GroupFooterDisplay != GroupFooterDisplayMode.Hidden ? view?.Rows.Count(row => row.Kind == GridRowKind.GroupFooter) : 0,
            focusedRecord = grid.Selection.FocusedCell is { } focused && grid.Table!.Contains(focused.RecordId) ? new { id = focused.RecordId, values = grid.Table.Schema.Columns.Where(column => column.Field != "Trend").ToDictionary(column => column.Field, column => column.GetValue(grid.Table[focused.RecordId])) } : null,
            columnFilter = grid.ActiveColumnFilter is { } filterEditor ? new { filterEditor.Field, filterEditor.SearchText,
                filterEditor.ExceptionCount, filterEditor.Exclude, filterEditor.ValidationMessage, filterEditor.Results.HasMore,
                filterEditor.Results.RecordsExamined, items = filterEditor.Results.Items, selected = filterEditor.VisibleSelection(),
                actions = filterEditor.ActionBounds(page) } : null,
            valueFilters = grid.ValueFilters,
            summaryEditor = grid.ActiveSummaryEditor is { } editor ? new { editor.EditingGroups, editor.ItemCount, editor.ValidationMessage, actions = grid.SummaryActionBounds(page) } : null,
            groups = view?.Groups.Select(group => new { group.Key, group.Value, group.Summaries, children = group.Children.Select(child => new { child.Key, child.Value, child.Summaries }) }).ToArray(),
            query = view?.Query, columns = grid.ColumnLayout?.Columns, filterTexts = grid.FilterTexts, expansion = view?.CaptureState(),
            records = view?.FilteredRecords.Take(12).Select(record => new { record.Id, record.Version, values = grid.Table!.Schema.Columns.Where(column => column.Field != "Trend").ToDictionary(column => column.Field, column => column.GetValue(record)) }).ToArray(),
            origin = new { origin.X, origin.Y },
            frame = frame is null ? null : new
            {
                frame.Width, frame.Height, frame.HeaderTop, frame.FilterTop, frame.BodyTop, frame.FooterTop, frame.SummaryGeometryBuildCount,
                summaryCells = SummaryGeometry.VisibleCells(frame).ToArray(),
                columns = frame.Columns.Slots, groups = frame.GroupChips, bands = frame.Bands, bandLevels = grid.ColumnLayout!.BandLevels,
                rows = Enumerable.Range(frame.VisibleRows.Start, frame.VisibleRows.Count).Select(index => new
                {
                    index, kind = view!.Rows[index].Kind.ToString(), id = view.Rows[index].Record?.Id, key = view.Rows[index].Key,
                    bounds = frame.RowBounds(index), cells = frame.Columns.Slots.ToDictionary(column => column.Field, column => frame.CellBounds(index, column.Field))
                }).ToArray()
            },
            performance = new { view?.RefreshCount, view?.ValueUpdateCount, view?.RowsRevision, view?.LastRefreshMilliseconds,
                grid.FrameBuildCount, grid.FrameReuseCount, grid.RowGeometryBuildCount, heightRevision = grid.HeightIndex.Revision, heightResets = grid.HeightIndex.ResetCount,
                heightMode = grid.HeightIndex.StorageMode.ToString(), heightEntries = grid.HeightIndex.StorageEntryCount,
                page.LiveUpdatesRunning, page.LiveTick, page.ControlEvents },
            rendering = new { grid.Renderer.HoveredColumn, grid.Renderer.HoveredRecordId, grid.Renderer.RetainedGlyphPaths, grid.Renderer.SummaryCellsDrawn, grid.Renderer.CellsDrawn, grid.Renderer.RowsDrawn, grid.Renderer.DrawCount, grid.Renderer.CachedTextRuns, grid.Renderer.LastDrawMilliseconds, grid.Renderer.FontFamily },
            summaryActions = grid.SummaryActionBounds(page),
            buttons = page.ActionBounds(), options = page.OptionValues()
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
                case "value-filter": grid.SetColumnValues(Text("field"), JsonSerializer.Deserialize<ColumnValueFilter>(input.GetProperty("filter").GetRawText(), Json)); break;
                case "validate-chrome": return JsonSerializer.Serialize(new { ok = true, report = ChromeControlChecks.Run() }, Json);
                case "filter-enabled": grid.IsFilterEnabled = input.GetProperty("value").GetBoolean(); break;
                case "validate-controls": return JsonSerializer.Serialize(new { ok = true, report = ControlReviewChecks.Run() }, Json);
                // Fixture/configuration commands do not simulate user edits or successful assertions.
                case "fixture-value": grid.Table!.Apply([new(input.GetProperty("id").GetInt64(), Text("field"), input.GetProperty("value").Clone())]); break;
                case "readonly": grid.IsReadOnly = input.GetProperty("value").GetBoolean(); break;
                case "redraw": grid.Redraw(); break;
                case "reset": page.Reload(input.TryGetProperty("count", out var count) ? count.GetInt32() : 10000); break;
                case "scenario": page.ApplyScenario(Text("name")); break;
                case "focus-grid": grid.Focus(FocusState.Programmatic); break;
                case "clear-data": grid.CancelEdit(); grid.Table!.Clear(); break;
                case "selection": grid.SelectionMode = Enum.Parse<GridSelectionMode>(Text("mode")); break;
                case "summaries":
                    var configuration = JsonSerializer.Deserialize<GridQuery>(input.GetProperty("query").GetRawText(), Json) ?? throw new ArgumentException("Missing summary configuration");
                    grid.ApplySummaries(configuration); break;
                case "group-summary-sort": grid.SortGroupBySummary(Text("field"), Text("key"), Enum.Parse<SortDirection>(Text("direction"))); break;
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
                case "band-path": grid.ColumnLayout!.SetBandPath(Text("field"), input.GetProperty("path").EnumerateArray().Select(value => value.GetString()!).ToArray()); break;
                case "band-pin": grid.ColumnLayout!.SetBandPin(Text("key"), Enum.Parse<ColumnPin>(Text("pin"))); break;
                case "band-visible": grid.ColumnLayout!.SetBandVisible(Text("key"), input.GetProperty("value").GetBoolean()); break;
                case "row-height": grid.SetRowHeight(Text("key"), input.GetProperty("height").GetDouble()); break;
                case "clear-row-height": grid.ClearRowHeight(Text("key")); break;
                case "batch-query":
                    using (grid.BeginDataUpdate())
                    {
                        grid.SetSearch("Alpine");
                        grid.SortColumn("Quantity", SortDirection.Descending);
                        grid.GroupColumn("Region");
                    }
                    break;
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
    public static async Task<string> Validate() => JsonSerializer.Serialize(await Validation.FilterPresentationValidationSuite.RunAsync(), Json);
}

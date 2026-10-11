using DataGridScene.Core;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataGridScene.Layout;

public sealed record GridLayoutDocument
{
    public int Version { get; init; } = 2;
    public IReadOnlyList<ColumnState> Columns { get; init; } = Array.Empty<ColumnState>();
    /// <summary>Base query; in v2 the auto-filter row is stored separately in FilterTexts.</summary>
    public GridQuery Query { get; init; } = new();
    public IReadOnlyDictionary<string, string> FilterTexts { get; init; } = new Dictionary<string, string>();
    public double RowHeight { get; init; } = 28;
    public bool ShowFilterRow { get; init; } = true;
    public bool ShowGroupPanel { get; init; } = true;
    public bool ShowBands { get; init; }
    public string Theme { get; init; } = "Light";
    public GridSelectionMode SelectionMode { get; init; }
    public GridViewState ViewState { get; init; } = GridViewState.Empty;
    public GridSelectionState? Selection { get; init; }
    public double HorizontalOffset { get; init; }
    public double VerticalOffset { get; init; }
    public IReadOnlyDictionary<string, double> RowHeights { get; init; } = new Dictionary<string, double>();

    public GridQuery EffectiveQuery()
    {
        if (FilterTexts.Count == 0) return Query;
        var filters = new List<GridFilter>();
        if (Query.Filter is not null) filters.Add(Query.Filter);
        foreach (var (field, text) in FilterTexts) if (CriteriaParser.AutoFilter(field, text) is { } filter) filters.Add(filter);
        return Query with { Filter = filters.Count == 0 ? null : GridFilter.And(filters.ToArray()) };
    }
}
public static class LayoutSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 48,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Save(GridLayoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string json = JsonSerializer.Serialize(document, Options);
        if (json.Length > 1_048_576) throw new InvalidOperationException("Layout state exceeds 1 MiB. Save without selection state for very large sparse selections.");
        return json;
    }
    public static GridLayoutDocument Load(string json, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(schema);
        if (json.Length > 1_048_576) throw new FormatException("Layout exceeds the 1 MiB limit.");
        GridLayoutDocument document;
        try { document = JsonSerializer.Deserialize<GridLayoutDocument>(json, Options) ?? throw new FormatException("Empty layout."); }
        catch (JsonException error) { throw new FormatException("Invalid layout JSON.", error); }
        if (document.Version is not (1 or 2)) throw new FormatException($"Unsupported layout version: {document.Version}");
        if (!double.IsFinite(document.RowHeight) || document.RowHeight < 18 || document.RowHeight > 256) throw new FormatException("Invalid row height.");
        if (document.Theme is not ("Light" or "Dark") || !Enum.IsDefined(document.SelectionMode)) throw new FormatException("Unknown theme or selection mode.");
        if (!double.IsFinite(document.HorizontalOffset) || !double.IsFinite(document.VerticalOffset) || document.HorizontalOffset < 0 || document.VerticalOffset < 0)
            throw new FormatException("Scroll offsets must be finite and nonnegative.");
        if (document.Columns is null || document.Query is null || document.FilterTexts is null || document.RowHeights is null || document.ViewState is null)
            throw new FormatException("Missing layout data.");
        if (document.FilterTexts.Count > schema.Columns.Count || document.RowHeights.Count > 16384) throw new FormatException("Too many saved fields or row heights.");
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, text) in document.FilterTexts)
        {
            if (!schema.TryGetColumn(field, out var column) || text is null || text.Length > 16384 || !filters.TryAdd(column.Field, text))
                throw new FormatException("Invalid or duplicate auto-filter field.");
        }
        var heights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, height) in document.RowHeights)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 4096 || !double.IsFinite(height) || height < 18 || height > 4096)
                throw new FormatException("Invalid custom row height.");
            heights.Add(key, height);
        }
        ValidateQuery(document.Query);
        document = document with { Query = QuerySnapshot.Capture(document.Query, schema) };
        var check = new GridLayout(schema);
        check.RestoreColumns(document.Columns);
        document = document with
        {
            Columns = check.Columns,
            FilterTexts = new ReadOnlyDictionary<string, string>(filters),
            RowHeights = new ReadOnlyDictionary<string, double>(heights)
        };
        using var view = new GridView(new GridTable(schema));
        view.SetQuery(document.EffectiveQuery());
        view.RestoreState(document.ViewState);
        if (document.Selection is not null)
        {
            if (document.Selection.Mode != document.SelectionMode) throw new FormatException("Conflicting selection modes.");
            new GridSelection().RestoreState(view, document.Selection);
        }
        return document;
    }
    private static void ValidateQuery(GridQuery query)
    {
        if (query.Sorts is null || query.Groups is null || query.Summaries is null || query.Search is null)
            throw new FormatException("Null query data.");
        if (query.Sorts.Count > 1024 || query.Groups.Count > 16 || query.Summaries.Count > 1024 || query.Search.Length > 16384)
            throw new FormatException("Query limits exceeded.");
        if (query.Sorts.Any(sort => sort is null || string.IsNullOrEmpty(sort.Field) || !Enum.IsDefined(sort.Direction)) ||
            query.Groups.Any(group => group is null || string.IsNullOrEmpty(group.Field) || !Enum.IsDefined(group.Direction) || !Enum.IsDefined(group.Interval)) ||
            query.Summaries.Any(summary => summary is null || string.IsNullOrEmpty(summary.Field) || !Enum.IsDefined(summary.Kind)))
            throw new FormatException("Invalid query descriptor.");
        if (query.Filter is null) return;
        var pending = new Stack<(GridFilter Filter, int Depth)>();
        pending.Push((query.Filter, 0));
        int count = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 1024 || item.Depth > 32 || item.Filter is null) throw new FormatException("Invalid or excessively complex filter.");
            switch (item.Filter)
            {
                case FieldFilter field:
                    if (string.IsNullOrEmpty(field.Field) || !Enum.IsDefined(field.Operator) || field.Values?.Count > 1024) throw new FormatException("Invalid field filter.");
                    break;
                case FilterGroup group:
                    if (group.Children is null || group.Children.Count > 1024) throw new FormatException("Invalid filter group.");
                    foreach (var child in group.Children) pending.Push((child, item.Depth + 1));
                    break;
                case NotFilter not:
                    pending.Push((not.Child, item.Depth + 1));
                    break;
                default: throw new FormatException("Unknown serialized filter type.");
            }
        }
    }
}

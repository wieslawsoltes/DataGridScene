using DataGridScene.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataGridScene.Layout;

public enum ColumnPin { None, Left, Right }
public sealed record ColumnState(string Field, double Width, int Order, bool Visible = true, ColumnPin Pin = ColumnPin.None);
public sealed record ColumnSlot(string Field, int SchemaIndex, double X, double Width, double ClipLeft, double ClipRight, ColumnPin Pin)
{
    public bool Contains(double x) => x >= ClipLeft && x < ClipRight;
}
public sealed record ColumnViewport(IReadOnlyList<ColumnSlot> Slots, double MiddleLeft, double MiddleRight, double MaximumOffset, double Offset)
{
    public ColumnSlot? HitTest(double x) => Slots.FirstOrDefault(slot => slot.Contains(x));
}

public sealed class GridLayout
{
    private readonly GridSchema _schema;
    private List<ColumnState> _columns;
    public const double IndicatorWidth = 38;
    public IReadOnlyList<ColumnState> Columns => _columns.AsReadOnly();
    public event EventHandler? Changed;
    public GridLayout(GridSchema schema)
    {
        _schema = schema;
        _columns = schema.Columns.Select(column => new ColumnState(column.Field, Math.Clamp(column.Width, 36, 4096), column.Index)).ToList();
    }
    private void Replace(string field, Func<ColumnState, ColumnState> update)
    {
        int index = _columns.FindIndex(column => column.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException($"Unknown column: {field}");
        _columns[index] = update(_columns[index]); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void SetWidth(string field, double width)
    {
        if (!double.IsFinite(width) || width < 36 || width > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        Replace(field, column => column with { Width = width });
    }
    public void SetVisible(string field, bool visible)
    {
        if (!visible && _columns.Count(column => column.Visible) == 1 && _columns.Any(column => column.Field.Equals(field, StringComparison.OrdinalIgnoreCase) && column.Visible))
            throw new InvalidOperationException("At least one column must remain visible.");
        Replace(field, column => column with { Visible = visible });
    }
    public void SetPin(string field, ColumnPin pin)
    {
        if (!Enum.IsDefined(pin)) throw new ArgumentOutOfRangeException(nameof(pin));
        Replace(field, column => column with { Pin = pin });
    }
    public void MoveBefore(string field, string beforeField)
    {
        var ordered = _columns.OrderBy(column => column.Order).ToList();
        var moving = ordered.First(column => column.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (field.Equals(beforeField, StringComparison.OrdinalIgnoreCase)) return;
        ordered.Remove(moving);
        int destination = ordered.FindIndex(column => column.Field.Equals(beforeField, StringComparison.OrdinalIgnoreCase));
        if (destination < 0) throw new ArgumentException($"Unknown destination: {beforeField}");
        ordered.Insert(destination, moving);
        _columns = ordered.Select((column, index) => column with { Order = index }).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public IReadOnlyList<ColumnState> DisplayColumns() => _columns.Where(column => column.Visible)
        .OrderBy(column => column.Pin == ColumnPin.Left ? 0 : column.Pin == ColumnPin.None ? 1 : 2).ThenBy(column => column.Order).ToArray();
    public ColumnViewport Compute(double width, double horizontalOffset)
    {
        if (!double.IsFinite(width) || !double.IsFinite(horizontalOffset)) throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Max(0, width);
        var visible = _columns.Where(column => column.Visible).OrderBy(column => column.Order).ToArray();
        var left = visible.Where(column => column.Pin == ColumnPin.Left).ToArray();
        var right = visible.Where(column => column.Pin == ColumnPin.Right).ToArray();
        var middle = visible.Where(column => column.Pin == ColumnPin.None).ToArray();
        double middleLeft = Math.Min(width, IndicatorWidth + left.Sum(column => column.Width));
        double middleRight = Math.Max(middleLeft, width - right.Sum(column => column.Width));
        double maximum = Math.Max(0, middle.Sum(column => column.Width) - (middleRight - middleLeft));
        horizontalOffset = Math.Clamp(horizontalOffset, 0, maximum);
        var slots = new List<ColumnSlot>();
        void Add(IEnumerable<ColumnState> columns, double x, double clipLeft, double clipRight)
        {
            foreach (var column in columns)
            {
                double start = Math.Max(x, clipLeft), end = Math.Min(x + column.Width, clipRight);
                if (end > start) slots.Add(new(column.Field, _schema[column.Field].Index, x, column.Width, start, end, column.Pin));
                x += column.Width;
            }
        }
        Add(middle, middleLeft - horizontalOffset, middleLeft, middleRight);
        Add(left, IndicatorWidth, Math.Min(width, IndicatorWidth), middleLeft);
        Add(right, width - right.Sum(column => column.Width), middleRight, width);
        return new(slots.AsReadOnly(), middleLeft, middleRight, maximum, horizontalOffset);
    }
    public void RestoreColumns(IEnumerable<ColumnState> states)
    {
        var incoming = states.ToArray();
        if (incoming.Length > 16384) throw new FormatException("Too many columns.");
        if (incoming.Select(column => column.Field.ToUpperInvariant()).Distinct().Count() != incoming.Length) throw new FormatException("Duplicate columns in saved layout.");
        foreach (var column in incoming)
            if (!double.IsFinite(column.Width) || column.Width < 36 || column.Width > 4096 || !Enum.IsDefined(column.Pin)) throw new FormatException("Invalid column geometry.");
        var restored = incoming.Where(column => _schema.TryGetColumn(column.Field, out _)).OrderBy(column => column.Order).ToList();
        var fields = restored.Select(column => column.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        restored.AddRange(_columns.Where(column => !fields.Contains(column.Field)).OrderBy(column => column.Order));
        if (!restored.Any(column => column.Visible)) throw new FormatException("A saved layout must have a visible column.");
        _columns = restored.Select((column, index) => column with { Order = index }).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record GridLayoutDocument
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<ColumnState> Columns { get; init; } = Array.Empty<ColumnState>();
    public GridQuery Query { get; init; } = new();
    public double RowHeight { get; init; } = 28;
    public bool ShowFilterRow { get; init; } = true;
    public bool ShowGroupPanel { get; init; } = true;
    public string Theme { get; init; } = "Light";
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
    public static string Save(GridLayoutDocument document) => JsonSerializer.Serialize(document, Options);
    public static GridLayoutDocument Load(string json, GridSchema schema)
    {
        if (json.Length > 1_048_576) throw new FormatException("Layout exceeds the 1 MiB limit.");
        var document = JsonSerializer.Deserialize<GridLayoutDocument>(json, Options) ?? throw new FormatException("Empty layout.");
        if (document.Version != 1) throw new FormatException($"Unsupported layout version: {document.Version}");
        if (!double.IsFinite(document.RowHeight) || document.RowHeight < 18 || document.RowHeight > 256) throw new FormatException("Invalid row height.");
        if (document.Theme is not ("Light" or "Dark")) throw new FormatException("Unknown theme.");
        if (document.Columns is null || document.Query is null) throw new FormatException("Missing layout data.");
        var check = new GridLayout(schema); check.RestoreColumns(document.Columns);
        using var view = new GridView(new GridTable(schema)); view.SetQuery(document.Query);
        return document;
    }
}

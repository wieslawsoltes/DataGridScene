namespace DataGridScene.Core;

/// <summary>Typed, serializable inclusion/exclusion for one column. Empty exclusion means all;
/// empty inclusion means none. Values use the column comparer, including null semantics.</summary>
public sealed record ColumnValueFilter(IReadOnlyList<object?> Values, bool Exclude = false)
{
    public const int MaximumValues = 1024;
    public static ColumnValueFilter All { get; } = new(Array.Empty<object?>(), true);
    public static ColumnValueFilter None { get; } = new(Array.Empty<object?>());
    public ColumnValueFilter Capture(GridSchema schema, string field)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (Values is null || Values.Count > MaximumValues) throw new ArgumentException("A value filter accepts at most 1,024 values.");
        var column = schema[field];
        var values = new SortedSet<object?>(column.Comparer ?? Comparer<object?>.Create(GridValue.Compare));
        foreach (var value in Values)
        {
            if (!column.TryConvert(value, schema.Culture, out var typed, out var error, validate: false))
                throw new FormatException(error);
            values.Add(typed);
        }
        return new(Array.AsReadOnly(values.ToArray()), Exclude);
    }
    public GridFilter? ToFilter(string field)
    {
        if (Exclude && Values.Count == 0) return null;
        GridFilter filter = new FieldFilter(field, FilterOperator.In, Values: Values);
        return Exclude ? new NotFilter(filter) : filter;
    }
}

/// <summary>Independent staged checkbox selection. It retains choices across value-list searches
/// without materializing the complete field domain. At most 1,024 exceptions are retained.</summary>
public sealed class ColumnValueSelection
{
    private readonly GridSchema _schema;
    private readonly string _field;
    private readonly IComparer<object?> _comparer;
    private SortedSet<object?> _values;
    public bool Exclude { get; private set; }
    public int ExceptionCount => _values.Count;
    public ColumnValueSelection(GridSchema schema, string field, ColumnValueFilter? initial = null)
    {
        _schema = schema; _field = schema[field].Field;
        _comparer = schema[field].Comparer ?? Comparer<object?>.Create(GridValue.Compare);
        var owned = (initial ?? ColumnValueFilter.All).Capture(schema, field);
        _values = new(owned.Values, _comparer); Exclude = owned.Exclude;
    }
    public bool IsSelected(object? value) => Exclude != _values.Contains(value);
    public void SelectAll(bool selected) { _values.Clear(); Exclude = selected; }
    /// <summary>Atomic batch, including conversion and limit checks. Values must be scalar field values.</summary>
    public void SetSelected(IEnumerable<object?> values, bool selected)
    {
        ArgumentNullException.ThrowIfNull(values);
        var next = new SortedSet<object?>(_values, _comparer);
        foreach (var input in values)
        {
            if (!_schema[_field].TryConvert(input, _schema.Culture, out var value, out var error, validate: false)) throw new FormatException(error);
            if (selected == Exclude) next.Remove(value); else next.Add(value);
            if (next.Count > ColumnValueFilter.MaximumValues) throw new InvalidOperationException("At most 1,024 checked or excluded values can be retained. Narrow the search or reset the selection.");
        }
        _values = next;
    }
    public ColumnValueFilter Capture() => new(Array.AsReadOnly(_values.ToArray()), Exclude);
}

public sealed record GridDistinctValue(object? Value, string Label)
{
    public override string ToString() => Label;
}
public sealed record GridDistinctValuesResult(IReadOnlyList<GridDistinctValue> Items, bool HasMore, int RecordsExamined);

public static class GridDistinctValues
{
    /// <summary>One field read per examined record, O(limit) retained values. Stop after limit+1
    /// matches proves truncation; search is over the field's displayed text, not the grid's query.</summary>
    public static GridDistinctValuesResult Collect(GridTable table, string field, string search = "", int limit = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(search);
        if (limit < 1 || limit > ColumnValueFilter.MaximumValues || search.Length > 16384) throw new ArgumentOutOfRangeException(nameof(limit));
        var column = table.Schema[field];
        var unique = new SortedSet<object?>(column.Comparer ?? Comparer<object?>.Create(GridValue.Compare));
        var labels = new List<GridDistinctValue>(Math.Min(limit, 128));
        int examined = 0; bool more = false;
        foreach (var row in table.Records)
        {
            if ((examined & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            examined++;
            object? value = column.GetValue(row);
            if (unique.Contains(value)) continue;
            string label = value is null ? "(Blanks)" : value is string { Length: 0 } ? "(Empty string)" : column.Display(value, table.Schema.Culture);
            if (!label.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (unique.Count == limit) { more = true; break; }
            unique.Add(value); labels.Add(new(value, label));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var comparer = column.Comparer ?? Comparer<object?>.Create(GridValue.Compare);
        labels.Sort((a, b) => comparer.Compare(a.Value, b.Value));
        return new(labels.AsReadOnly(), more, examined);
    }
}

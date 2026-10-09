using System.Globalization;

namespace DataGridScene.Core;

public enum SortDirection { Ascending, Descending }
public enum GroupInterval { Value, Date, Month, Year, FirstLetter }
public enum SummaryKind { Count, Sum, Average, Minimum, Maximum, DistinctCount }
public sealed record SortDescriptor(string Field, SortDirection Direction = SortDirection.Ascending);
public sealed record GroupDescriptor(string Field, SortDirection Direction = SortDirection.Ascending, GroupInterval Interval = GroupInterval.Value);
public sealed record SummaryDescriptor(string Field, SummaryKind Kind, string? Name = null)
{
    public string Key => Name ?? $"{Field}:{Kind}";
}
public sealed record GridQuery
{
    public IReadOnlyList<SortDescriptor> Sorts { get; init; } = Array.Empty<SortDescriptor>();
    public IReadOnlyList<GroupDescriptor> Groups { get; init; } = Array.Empty<GroupDescriptor>();
    public IReadOnlyList<SummaryDescriptor> Summaries { get; init; } = Array.Empty<SummaryDescriptor>();
    public GridFilter? Filter { get; init; }
    public string Search { get; init; } = string.Empty;
}

public static class SummaryCalculator
{
    public static IReadOnlyDictionary<string, object?> Calculate(IReadOnlyList<GridRecord> rows, GridSchema schema, IReadOnlyList<SummaryDescriptor> descriptors)
    {
        var output = new Dictionary<string, object?>();
        foreach (var descriptor in descriptors)
        {
            var column = schema[descriptor.Field];
            if (!output.TryAdd(descriptor.Key, null)) throw new ArgumentException($"Duplicate summary key: {descriptor.Key}");
            if (descriptor.Kind == SummaryKind.Count) { output[descriptor.Key] = rows.Count; continue; }
            int count = 0; decimal sum = 0; object? min = null, max = null;
            HashSet<string>? distinct = descriptor.Kind == SummaryKind.DistinctCount ? [] : null;
            if (descriptor.Kind is SummaryKind.Sum or SummaryKind.Average && !GridValue.IsNumericType(column.DataType))
                throw new ArgumentException($"{descriptor.Kind} requires a numeric field: {column.Field}");
            foreach (var row in rows)
            {
                object? value = column.GetValue(row);
                if (value is null) continue;
                if (descriptor.Kind is SummaryKind.Sum or SummaryKind.Average) sum = checked(sum + Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                if (count == 0 || (column.Comparer?.Compare(value, min) ?? GridValue.Compare(value, min)) < 0) min = value;
                if (count == 0 || (column.Comparer?.Compare(value, max) ?? GridValue.Compare(value, max)) > 0) max = value;
                distinct?.Add(GridValue.Key(value)); count++;
            }
            output[descriptor.Key] = descriptor.Kind switch
            {
                SummaryKind.Sum => sum,
                SummaryKind.Average => count == 0 ? null : sum / count,
                SummaryKind.Minimum => min,
                SummaryKind.Maximum => max,
                SummaryKind.DistinctCount => distinct!.Count,
                _ => null
            };
        }
        return output;
    }
}

public sealed record GridGroup(string Key, string Field, object? Value, int Depth, IReadOnlyList<GridRecord> Records,
    IReadOnlyList<GridGroup> Children, IReadOnlyDictionary<string, object?> Summaries);
public enum GridRowKind { Data, Group, Detail }
public sealed record ViewRow(GridRowKind Kind, string Key, int Depth, GridRecord? Record = null, GridGroup? Group = null);

/// <summary>Materialized data view; visuals remain virtualized independently.</summary>
public sealed class GridView : IDisposable
{
    private bool _disposed;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<long> _details = [];
    public GridTable Table { get; }
    public GridQuery Query { get; private set; } = new();
    public IReadOnlyList<GridRecord> FilteredRecords { get; private set; } = Array.Empty<GridRecord>();
    public IReadOnlyList<ViewRow> Rows { get; private set; } = Array.Empty<ViewRow>();
    public IReadOnlyList<GridGroup> Groups { get; private set; } = Array.Empty<GridGroup>();
    public IReadOnlyDictionary<string, object?> Summaries { get; private set; } = new Dictionary<string, object?>();
    public int Revision { get; private set; }
    public event EventHandler? Changed;

    public GridView(GridTable table)
    {
        Table = table;
        Table.Changed += TableChanged;
        Refresh();
    }
    private void TableChanged(object? sender, GridChangedEventArgs args) => Refresh();
    public void SetQuery(GridQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Build(query);
        Query = query;
        Notify();
    }
    public void Refresh() { ObjectDisposedException.ThrowIf(_disposed, this); Build(Query); Notify(); }
    private void Notify() { Revision++; Changed?.Invoke(this, EventArgs.Empty); }

    private void Build(GridQuery query)
    {
        var schema = Table.Schema;
        if (query.Groups.Count > 16) throw new ArgumentException("At most 16 grouping levels are supported.");
        if (query.Sorts.Select(s => s.Field.ToUpperInvariant()).Distinct().Count() != query.Sorts.Count) throw new ArgumentException("Duplicate sort fields.");
        if (query.Groups.Select(s => s.Field.ToUpperInvariant()).Distinct().Count() != query.Groups.Count) throw new ArgumentException("Duplicate group fields.");
        foreach (var group in query.Groups) _ = schema[group.Field];
        foreach (var summary in query.Summaries) _ = schema[summary.Field];
        var predicate = query.Filter?.Compile(schema);
        string[] terms = SearchTerms(query.Search);
        var rows = new List<GridRecord>(Table.Records.Count);
        foreach (var record in Table.Records)
        {
            if (predicate is not null && !predicate(record)) continue;
            if (terms.Length > 0 && !terms.All(term => schema.Columns.Any(column => column.Display(column.GetValue(record), schema.Culture).Contains(term, StringComparison.OrdinalIgnoreCase)))) continue;
            rows.Add(record);
        }
        var sorts = query.Sorts.Select(sort => (Column: schema[sort.Field], sort.Direction)).ToArray();
        if (sorts.Length > 0)
        {
            var ordinal = rows.Select((row, index) => (row.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
            rows.Sort((a, b) =>
            {
                foreach (var (column, direction) in sorts)
                {
                    int comparison = column.Comparer?.Compare(column.GetValue(a), column.GetValue(b)) ?? GridValue.Compare(column.GetValue(a), column.GetValue(b));
                    if (comparison != 0) return direction == SortDirection.Ascending ? Math.Sign(comparison) : -Math.Sign(comparison);
                }
                return ordinal[a.Id].CompareTo(ordinal[b.Id]);
            });
        }
        var totals = SummaryCalculator.Calculate(rows, schema, query.Summaries);
        IReadOnlyList<GridGroup> groups = query.Groups.Count > 0 ? BuildGroups(rows, query, 0, "") : Array.Empty<GridGroup>();
        var flat = new List<ViewRow>(rows.Count + groups.Count);
        if (groups.Count > 0) Flatten(groups, flat);
        else foreach (var row in rows) AddRecord(flat, row, 0);
        FilteredRecords = rows.AsReadOnly(); Groups = groups; Rows = flat.AsReadOnly(); Summaries = totals;
    }
    private static string[] SearchTerms(string search)
    {
        var terms = new List<string>(); var buffer = new System.Text.StringBuilder(); bool quoted = false;
        foreach (char c in search)
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted) { if (buffer.Length > 0) { terms.Add(buffer.ToString()); buffer.Clear(); } }
            else buffer.Append(c);
        }
        if (buffer.Length > 0) terms.Add(buffer.ToString());
        return terms.ToArray();
    }
    private IReadOnlyList<GridGroup> BuildGroups(IReadOnlyList<GridRecord> rows, GridQuery query, int depth, string parent)
    {
        var descriptor = query.Groups[depth]; var column = Table.Schema[descriptor.Field];
        var buckets = new Dictionary<string, (object? Value, List<GridRecord> Rows)>(StringComparer.Ordinal);
        foreach (var record in rows)
        {
            object? value = Interval(column.GetValue(record), descriptor.Interval);
            string key = GridValue.Key(value);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = (value, []);
            bucket.Rows.Add(record);
        }
        var ordered = buckets.OrderBy(pair => pair.Value.Value, Comparer<object?>.Create(GridValue.Compare));
        IEnumerable<KeyValuePair<string, (object? Value, List<GridRecord> Rows)>> sequence = descriptor.Direction == SortDirection.Descending ? ordered.Reverse() : ordered;
        var output = new List<GridGroup>(buckets.Count);
        foreach (var (key, bucket) in sequence)
        {
            string path = parent + "/" + Uri.EscapeDataString(column.Field) + ":" + Uri.EscapeDataString(key);
            var children = depth + 1 < query.Groups.Count ? BuildGroups(bucket.Rows, query, depth + 1, path) : Array.Empty<GridGroup>();
            output.Add(new(path, column.Field, bucket.Value, depth, bucket.Rows.AsReadOnly(), children, SummaryCalculator.Calculate(bucket.Rows, Table.Schema, query.Summaries)));
        }
        return output.AsReadOnly();
    }
    private static object? Interval(object? value, GroupInterval interval)
    {
        if (value is null || interval == GroupInterval.Value) return value;
        if (interval == GroupInterval.FirstLetter) return value.ToString() is { Length: > 0 } text ? System.Globalization.StringInfo.GetNextTextElement(text).ToUpperInvariant() : "";
        DateTime date = value switch { DateTime dt => dt, DateTimeOffset dto => dto.DateTime, _ => throw new ArgumentException("Date grouping requires a date field.") };
        return interval switch { GroupInterval.Year => new DateTime(date.Year, 1, 1), GroupInterval.Month => new DateTime(date.Year, date.Month, 1), _ => date.Date };
    }
    private void AddRecord(List<ViewRow> flat, GridRecord record, int depth)
    {
        flat.Add(new(GridRowKind.Data, "row:" + record.Id, depth, record));
        if (_details.Contains(record.Id)) flat.Add(new(GridRowKind.Detail, "detail:" + record.Id, depth, record));
    }
    private void Flatten(IReadOnlyList<GridGroup> groups, List<ViewRow> flat)
    {
        foreach (var group in groups)
        {
            flat.Add(new(GridRowKind.Group, group.Key, group.Depth, Group: group));
            if (_collapsed.Contains(group.Key)) continue;
            if (group.Children.Count > 0) Flatten(group.Children, flat);
            else foreach (var record in group.Records) AddRecord(flat, record, group.Depth + 1);
        }
    }
    private void Reflatten()
    {
        var flat = new List<ViewRow>(FilteredRecords.Count);
        if (Groups.Count > 0) Flatten(Groups, flat);
        else foreach (var record in FilteredRecords) AddRecord(flat, record, 0);
        Rows = flat.AsReadOnly(); Notify();
    }
    public bool IsExpanded(string groupKey) => !_collapsed.Contains(groupKey);
    public void ToggleGroup(string groupKey) { if (!_collapsed.Add(groupKey)) _collapsed.Remove(groupKey); Reflatten(); }
    public void ExpandAll() { _collapsed.Clear(); Reflatten(); }
    public void CollapseAll()
    {
        void Visit(IReadOnlyList<GridGroup> groups) { foreach (var group in groups) { _collapsed.Add(group.Key); Visit(group.Children); } }
        Visit(Groups); Reflatten();
    }
    public bool IsDetailExpanded(long id) => _details.Contains(id);
    public void ToggleDetail(long id) { _ = Table[id]; if (!_details.Add(id)) _details.Remove(id); Reflatten(); }
    public int IndexOfRecord(long id) { for (int i = 0; i < Rows.Count; i++) if (Rows[i].Kind == GridRowKind.Data && Rows[i].Record!.Id == id) return i; return -1; }
    public void Dispose() { if (_disposed) return; _disposed = true; Table.Changed -= TableChanged; }
}

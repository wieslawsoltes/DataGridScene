using System.Globalization;

namespace DataGridScene.Core;

public enum SortDirection { Ascending, Descending }
public enum GroupInterval { Value, Date, Month, Year, FirstLetter }
public enum SummaryKind { Count, Sum, Average, Minimum, Maximum, DistinctCount, Custom }
public sealed record SortDescriptor(string Field, SortDirection Direction = SortDirection.Ascending);
public sealed record GroupDescriptor(string Field, SortDirection Direction = SortDirection.Ascending, GroupInterval Interval = GroupInterval.Value);
public enum GroupSummaryDisplayMode { Inline, AlignByColumns }
public enum GroupFooterDisplayMode { Hidden, ExpandedGroups, Always }
public sealed record GroupSummarySortDescriptor(string Field, string SummaryKey, SortDirection Direction = SortDirection.Ascending);
public sealed record SummaryDescriptor(string Field, SummaryKind Kind, string? Name = null)
{
    public string Key => Name ?? $"{Field}:{Kind}";
    public string? ShowInColumn { get; init; }
    public bool ShowInGroupRow { get; init; } = true;
    public bool ShowInGroupFooter { get; init; }
    public string? DisplayFormat { get; init; }
    public string? CustomAggregate { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetField => ShowInColumn ?? Field;
    public string Display(object? value, GridSchema schema)
    {
        string text = DisplayFormat is not null ? string.Format(schema.Culture, DisplayFormat, value, schema[Field].Caption, Kind)
            : Kind is SummaryKind.Count or SummaryKind.DistinctCount ? string.Format(schema.Culture, "Count: {0:N0}", value)
            : $"{(Kind == SummaryKind.Sum ? "Σ" : Kind.ToString())}: {schema[Field].Display(value, schema.Culture)}";
        if (text.Length > 16384) throw new FormatException("Summary display text exceeds 16,384 characters.");
        return text;
    }
}
public sealed record GridQuery
{
    public IReadOnlyList<SortDescriptor> Sorts { get; init; } = Array.Empty<SortDescriptor>();
    public IReadOnlyList<GroupDescriptor> Groups { get; init; } = Array.Empty<GroupDescriptor>();
    public IReadOnlyList<SummaryDescriptor> Summaries { get; init; } = Array.Empty<SummaryDescriptor>();
    /// <summary>Null retains legacy group calculations from total summaries; an empty list disables group summaries.</summary>
    public IReadOnlyList<SummaryDescriptor>? GroupSummaries { get; init; }
    public IReadOnlyList<GroupSummarySortDescriptor> GroupSummarySorts { get; init; } = Array.Empty<GroupSummarySortDescriptor>();
    public GroupSummaryDisplayMode GroupSummaryDisplay { get; init; }
    public GroupFooterDisplayMode GroupFooterDisplay { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SummaryDescriptor> EffectiveGroupSummaries => GroupSummaries ?? Summaries;
    public GridFilter? Filter { get; init; }
    public string Search { get; init; } = string.Empty;
}

public sealed record GridGroup(string Key, string Field, object? Value, int Depth, IReadOnlyList<GridRecord> Records,
    IReadOnlyList<GridGroup> Children, IReadOnlyDictionary<string, object?> Summaries);
public enum GridRowKind { Data, Group, Detail, GroupFooter }
public sealed record ViewRow(GridRowKind Kind, string Key, int Depth, GridRecord? Record = null, GridGroup? Group = null);

/// <summary>Materialized data view; visuals remain virtualized independently.</summary>
public sealed partial class GridView : IDisposable
{
    private bool _disposed;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<long> _details = [];
    public GridTable Table { get; }
    public GridQuery Query { get; private set; } = new();
    public IReadOnlyList<GridRecord> FilteredRecords { get; private set; } = Array.Empty<GridRecord>();
    public IReadOnlyList<GridRecord> OrderedRecords { get; private set; } = Array.Empty<GridRecord>();
    private Dictionary<long, int> _rowIndexes = [];
    public IReadOnlyList<ViewRow> Rows { get; private set; } = Array.Empty<ViewRow>();
    public IReadOnlyList<GridGroup> Groups { get; private set; } = Array.Empty<GridGroup>();
    public IReadOnlyDictionary<string, object?> Summaries { get; private set; } = new Dictionary<string, object?>();
    public int Revision { get; private set; }
    public event EventHandler? Changed;

    public GridView(GridTable table) : this(table, new GridQuery()) { }
    /// <summary>Shape the initial query once instead of constructing an unfiltered view first.</summary>
    public GridView(GridTable table, GridQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
        Query = QuerySnapshot.Capture(query, table.Schema);
        Refresh();
        Table.Changed += TableChanged;
    }
    private void TableChanged(object? sender, GridChangedEventArgs args) => HandleTableChange(args);
    public void SetQuery(GridQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        query = QuerySnapshot.Capture(query, Table.Schema);
        if (_deferDepth > 0) { _pendingQuery = query; _refreshPending = true; return; }
        Build(query);
        Query = query;
        Notify();
    }
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deferDepth > 0) { _refreshPending = true; return; }
        Build(Query); Notify();
    }
    private void Notify(GridViewChangeKind kind = GridViewChangeKind.Reset)
    {
        LastChangeKind = kind; Revision++; Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Build(GridQuery query)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var schema = Table.Schema;
        var dependencies = QueryDependencies.Capture(schema, query);
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
        SortRows(rows, query);
        var totals = SummaryCalculator.Calculate(rows, schema, query.Summaries);
        IReadOnlyList<GridGroup> groups = query.Groups.Count > 0 ? BuildGroups(rows, query, 0, "") : Array.Empty<GridGroup>();
        var flat = new List<ViewRow>(rows.Count + groups.Count);
        if (groups.Count > 0) Flatten(groups, flat, query);
        else foreach (var row in rows) AddRecord(flat, row, 0);
        var ordered = new List<GridRecord>(rows.Count);
        void Collect(IReadOnlyList<GridGroup> items)
        {
            foreach (var group in items)
                if (group.Children.Count == 0) ordered.AddRange(group.Records); else Collect(group.Children);
        }
        if (groups.Count == 0) ordered.AddRange(rows); else Collect(groups);
        // Formatting failures are rejected before publishing any new state, never during painting.
        foreach (var item in query.Summaries) _ = item.Display(totals[item.Key], schema);
        FilteredRecords = rows.AsReadOnly(); OrderedRecords = ordered.AsReadOnly();
        Groups = groups; PublishRows(flat); Summaries = totals;
        _shapingDependencies = dependencies; _knownTableRevision = Table.Revision;
        RefreshCount++; LastRefreshMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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
    private readonly record struct GroupValue(object? Value);
    private IReadOnlyList<GridGroup> BuildGroups(IReadOnlyList<GridRecord> rows, GridQuery query, int depth, string parent)
    {
        var descriptor = query.Groups[depth]; var column = Table.Schema[descriptor.Field];
        var buckets = new Dictionary<string, (object? Value, List<GridRecord> Rows)>(StringComparer.Ordinal);
        // Custom comparers define grouping equality as well as ordering. A wrapper
        // permits null keys while preserving the comparer contract.
        SortedDictionary<GroupValue, List<GridRecord>>? compared = column.Comparer is null ? null :
            new(Comparer<GroupValue>.Create((a, b) => column.Comparer.Compare(a.Value, b.Value)));
        foreach (var record in rows)
        {
            object? value = Interval(column.GetValue(record), descriptor.Interval);
            if (compared is not null)
            {
                var wrapped = new GroupValue(value);
                if (!compared.TryGetValue(wrapped, out var members)) compared.Add(wrapped, members = []);
                members.Add(record);
            }
            else
            {
                string key = GridValue.Key(value);
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = (value, []);
                bucket.Rows.Add(record);
            }
        }
        if (compared is not null)
            foreach (var (key, members) in compared)
            {
                // Default keys case-fold strings, which is wrong for a case-sensitive comparer.
                string identity = key.Value is string text ? "s:" + text : GridValue.Key(key.Value);
                if (!buckets.TryAdd(identity, (key.Value, members))) throw new InvalidOperationException("Custom grouping values require distinct scalar identities.");
            }
        var output = new List<GridGroup>(buckets.Count);
        foreach (var (key, bucket) in buckets)
        {
            string path = parent + "/" + Uri.EscapeDataString(column.Field) + ":" + Uri.EscapeDataString(key);
            var children = depth + 1 < query.Groups.Count ? BuildGroups(bucket.Rows, query, depth + 1, path) : Array.Empty<GridGroup>();
            var summaries = SummaryCalculator.Calculate(bucket.Rows, Table.Schema, query.EffectiveGroupSummaries);
            foreach (var item in query.EffectiveGroupSummaries) _ = item.Display(summaries[item.Key], Table.Schema);
            output.Add(new(path, column.Field, bucket.Value, depth, bucket.Rows.AsReadOnly(), children, summaries));
        }
        var summarySorts = query.GroupSummarySorts.Where(sort => sort.Field.Equals(column.Field, StringComparison.OrdinalIgnoreCase)).ToArray();
        output.Sort((a, b) =>
        {
            foreach (var sort in summarySorts)
            {
                int result = GridValue.Compare(a.Summaries[sort.SummaryKey], b.Summaries[sort.SummaryKey]);
                if (result != 0) return sort.Direction == SortDirection.Ascending ? Math.Sign(result) : -Math.Sign(result);
            }
            int fallback = column.Comparer?.Compare(a.Value, b.Value) ?? GridValue.Compare(a.Value, b.Value);
            if (fallback == 0) fallback = StringComparer.Ordinal.Compare(a.Key, b.Key);
            return descriptor.Direction == SortDirection.Ascending ? Math.Sign(fallback) : -Math.Sign(fallback);
        });
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
    private void Flatten(IReadOnlyList<GridGroup> groups, List<ViewRow> flat, GridQuery query)
    {
        foreach (var group in groups)
        {
            flat.Add(new(GridRowKind.Group, group.Key, group.Depth, Group: group));
            bool expanded = !_collapsed.Contains(group.Key);
            if (expanded)
            {
                if (group.Children.Count > 0) Flatten(group.Children, flat, query);
                else foreach (var record in group.Records) AddRecord(flat, record, group.Depth + 1);
            }
            if (query.EffectiveGroupSummaries.Any(summary => summary.ShowInGroupFooter) &&
                (query.GroupFooterDisplay == GroupFooterDisplayMode.Always || expanded && query.GroupFooterDisplay == GroupFooterDisplayMode.ExpandedGroups))
                flat.Add(new(GridRowKind.GroupFooter, "footer:" + group.Key, group.Depth, Group: group));
        }
    }
    private void Reflatten()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deferDepth > 0) { _flattenPending = true; return; }
        var flat = new List<ViewRow>(FilteredRecords.Count);
        if (Groups.Count > 0) Flatten(Groups, flat, Query);
        else foreach (var record in FilteredRecords) AddRecord(flat, record, 0);
        PublishRows(flat); Notify(GridViewChangeKind.Expansion);
    }
    private void PublishRows(List<ViewRow> rows)
    {
        var indexes = new Dictionary<long, int>(FilteredRecords.Count);
        var special = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == GridRowKind.Data) indexes.Add(rows[i].Record!.Id, i);
            else special.Add(rows[i].Key, i);
        Rows = rows.AsReadOnly(); _rowIndexes = indexes; _specialRowIndexes = special;
        RowsRevision++;
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
    public int IndexOfRecord(long id) => _rowIndexes.GetValueOrDefault(id, -1);
    public void Dispose() { if (_disposed) return; _disposed = true; Table.Changed -= TableChanged; }
}

using System.Text.Json;

namespace DataGridScene.Core;

/// <summary>Validate and retain owned query collections before local or asynchronous execution.</summary>
public static class QuerySnapshot
{
    public static GridQuery Capture(GridQuery query, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(schema);
        if (query.Sorts is null || query.Groups is null || query.Summaries is null || query.GroupSummarySorts is null || query.Search is null)
            throw new ArgumentException("Query collections and search cannot be null.");
        if (query.Sorts.Count > 1024 || query.Groups.Count > 16 || query.GroupSummarySorts.Count > 1024 || query.Search.Length > 16384)
            throw new ArgumentException("Query complexity limit exceeded.");
        if (!Enum.IsDefined(query.GroupSummaryDisplay) || !Enum.IsDefined(query.GroupFooterDisplay))
            throw new ArgumentException("Invalid group summary display mode.");
        var sorts = query.Sorts.Select(sort =>
        {
            if (sort is null || !Enum.IsDefined(sort.Direction)) throw new ArgumentException("Invalid sort descriptor.");
            _ = schema[sort.Field]; return sort;
        }).ToArray();
        var groups = query.Groups.Select(group =>
        {
            if (group is null || !Enum.IsDefined(group.Direction) || !Enum.IsDefined(group.Interval)) throw new ArgumentException("Invalid grouping descriptor.");
            var column = schema[group.Field];
            if (group.Interval is GroupInterval.Date or GroupInterval.Month or GroupInterval.Year && column.DataType != typeof(DateTime) && column.DataType != typeof(DateTimeOffset))
                throw new ArgumentException("Date grouping requires a date field.");
            return group;
        }).ToArray();
        if (sorts.Select(sort => sort.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorts.Length ||
            groups.Select(group => group.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Length)
            throw new ArgumentException("Duplicate sort or group fields.");
        SummaryCalculator.Validate(schema, query.Summaries);
        if (query.GroupSummaries is not null) SummaryCalculator.Validate(schema, query.GroupSummaries);
        var groupItems = query.EffectiveGroupSummaries;
        var groupSorts = query.GroupSummarySorts.ToArray();
        var seen = new HashSet<(string, string)>();
        foreach (var sort in groupSorts)
        {
            if (sort is null || !Enum.IsDefined(sort.Direction) || !groups.Any(group => group.Field.Equals(sort.Field, StringComparison.OrdinalIgnoreCase)) ||
                !groupItems.Any(summary => summary.Key == sort.SummaryKey) || !seen.Add((sort.Field.ToUpperInvariant(), sort.SummaryKey)))
                throw new ArgumentException("Invalid, duplicate or unresolved group summary sort.");
        }
        // Preserve number tokens until the field's type is known, and detach them from
        // a caller-owned JsonDocument that may be disposed immediately after capture.
        static object? Literal(object? value) => value is JsonElement json ? json.Clone() : value;
        int nodes = 0;
        GridFilter Copy(GridFilter filter, int depth)
        {
            if (filter is null || ++nodes > 1024 || depth > 32) throw new ArgumentException("Invalid or excessively complex filter.");
            switch (filter)
            {
                case FieldFilter field:
                    _ = schema[field.Field];
                    if (!Enum.IsDefined(field.Operator) || field.Values?.Count > 1024) throw new ArgumentException("Invalid field filter.");
                    return field with { Value = Literal(field.Value), Upper = Literal(field.Upper),
                        Values = field.Values is null ? null : Array.AsReadOnly(field.Values.Select(Literal).ToArray()) };
                case FilterGroup group:
                    if (group.Children is null || group.Children.Count > 1024) throw new ArgumentException("Invalid filter group.");
                    return group with { Children = Array.AsReadOnly(group.Children.Select(child => Copy(child, depth + 1)).ToArray()) };
                case NotFilter not: return not with { Child = Copy(not.Child, depth + 1) };
                default: return filter; // Application extension predicates must themselves be immutable.
            }
        }
        var snapshot = query with
        {
            Sorts = Array.AsReadOnly(sorts), Groups = Array.AsReadOnly(groups), Summaries = Array.AsReadOnly(query.Summaries.ToArray()),
            GroupSummaries = query.GroupSummaries is null ? null : Array.AsReadOnly(query.GroupSummaries.ToArray()),
            GroupSummarySorts = Array.AsReadOnly(groupSorts), Filter = query.Filter is null ? null : Copy(query.Filter, 0)
        };
        _ = snapshot.Filter?.Compile(schema);
        return snapshot;
    }
}

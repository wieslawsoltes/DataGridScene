namespace DataGridScene.Core;

public sealed partial class GridSchema
{
    private readonly Dictionary<string, HashSet<string>?> _valueDependencies = new(StringComparer.OrdinalIgnoreCase);
    private void CaptureValueDependencies(IReadOnlyList<GridColumn> columns)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? Visit(GridColumn column)
        {
            if (_valueDependencies.TryGetValue(column.Field, out var cached)) return cached;
            if (visiting.Count >= 128) throw new ArgumentException("Computed-field dependency depth exceeds 128.");
            if (!visiting.Add(column.Field)) throw new ArgumentException("Cyclic computed-field dependencies: " + column.Field);
            HashSet<string>? fields = new(StringComparer.OrdinalIgnoreCase);
            if (column.ValueGetter is null) fields.Add(column.Field);
            else if (column.ValueDependencies is null) fields = null;
            else
            {
                if (column.ValueDependencies.Count > columns.Count) throw new ArgumentException("Too many computed-field dependencies.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in column.ValueDependencies)
                {
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) throw new ArgumentException("Invalid or duplicate computed-field dependency.");
                    var dependency = Visit(this[name]);
                    // Validate every dependency even when an earlier one was unknown.
                    if (dependency is null) fields = null;
                    else fields?.UnionWith(dependency);
                }
            }
            visiting.Remove(column.Field); _valueDependencies.Add(column.Field, fields);
            return fields;
        }
        foreach (var column in columns) Visit(column);
    }
    internal HashSet<string>? DependenciesOf(string field) => _valueDependencies[this[field].Field];
}

/// <summary>Null represents an unbounded dependency; never guess dependencies of application code.</summary>
internal static class QueryDependencies
{
    internal static HashSet<string>? Capture(GridSchema schema, GridQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Search)) return null;
        HashSet<string>? fields = new(StringComparer.OrdinalIgnoreCase);
        void Add(string field)
        {
            var dependency = schema.DependenciesOf(field);
            if (dependency is null) fields = null;
            else fields?.UnionWith(dependency);
        }
        void Filter(GridFilter filter)
        {
            switch (filter)
            {
                case FieldFilter field: Add(field.Field); break;
                case FilterGroup group: foreach (var child in group.Children) Filter(child); break;
                case NotFilter not: Filter(not.Child); break;
                default: fields = null; break;
            }
        }
        foreach (var sort in query.Sorts) Add(sort.Field);
        foreach (var group in query.Groups) Add(group.Field);
        if (query.IsFilterEnabled && query.Filter is not null) Filter(query.Filter);
        void Summaries(IReadOnlyList<SummaryDescriptor> items)
        {
            foreach (var item in items)
                if (item.Kind == SummaryKind.Custom) fields = null;
                else if (item.Kind != SummaryKind.Count) Add(item.Field);
        }
        Summaries(query.Summaries);
        if (query.Groups.Count > 0) Summaries(query.EffectiveGroupSummaries);
        return fields;
    }
}

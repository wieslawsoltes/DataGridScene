using System.Globalization;
using System.Text;

namespace DataGridScene.Core;

/// <summary>Bounded, human-readable criteria descriptions. This is not executable criteria
/// serialization; it never reads records, invokes getters or changes boolean structure.</summary>
public static class GridFilterText
{
    public static string Describe(GridFilter? filter, GridSchema schema, int maximumLength = 512)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (maximumLength < 16 || maximumLength > 16384) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var text = new StringBuilder(); bool truncated = false; int visited = 0;
        void Add(string value)
        {
            int remaining = maximumLength - text.Length;
            if (value.Length > remaining) { text.Append(value.AsSpan(0, Math.Max(0, remaining))); truncated = true; }
            else text.Append(value);
        }
        void Literal(object? value, GridColumn? column = null)
        {
            if (column is not null)
            {
                if (!column.TryConvert(value, schema.Culture, out value, out var error, validate: false)) throw new FormatException(error);
            }
            else value = GridValue.Unwrap(value);
            if (value is null) { Add("(Blanks)"); return; }
            if (value is string str)
            {
                Add("\"");
                foreach (char c in str)
                {
                    if (text.Length >= maximumLength) { truncated = true; break; }
                    Add(c switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", '"' => "\\\"", _ => c.ToString() });
                }
                Add("\""); return;
            }
            Add(value is DateTime date ? date.ToString("g", schema.Culture)
                : value is DateTimeOffset offset ? offset.ToString("g", schema.Culture)
                : Convert.ToString(value, schema.Culture) ?? "");
        }
        void Visit(GridFilter node, int depth)
        {
            if (text.Length >= maximumLength || ++visited > 1024 || depth > 32) { truncated = true; return; }
            switch (node)
            {
                case FieldFilter field:
                    Add(schema[field.Field].Caption); Add(" ");
                    Add(field.Operator switch {
                        FilterOperator.Equal => "=", FilterOperator.NotEqual => "≠", FilterOperator.Less => "<",
                        FilterOperator.LessOrEqual => "≤", FilterOperator.Greater => ">", FilterOperator.GreaterOrEqual => "≥",
                        FilterOperator.IsNull => "is blank", FilterOperator.IsNotNull => "is not blank",
                        FilterOperator.StartsWith => "starts with", FilterOperator.EndsWith => "ends with",
                        _ => field.Operator.ToString().ToLowerInvariant()
                    });
                    if (field.Operator is FilterOperator.IsNull or FilterOperator.IsNotNull) break;
                    Add(" ");
                    if (field.Operator == FilterOperator.In)
                    {
                        Add("("); int index = 0;
                        foreach (var value in field.Values ?? Array.Empty<object?>())
                        {
                            if (text.Length >= maximumLength) { truncated = true; break; }
                            if (index++ > 0) Add(", "); Literal(value, schema[field.Field]);
                        }
                        Add(")");
                    }
                    else { Literal(field.Value, field.Operator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith ? null : schema[field.Field]);
                        if (field.Operator == FilterOperator.Between) { Add(" and "); Literal(field.Upper, schema[field.Field]); } }
                    break;
                case NotFilter not: Add("NOT ("); Visit(not.Child, depth + 1); Add(")"); break;
                case FilterGroup group:
                    if (group.Children.Count == 0) { Add(group.All ? "Always true" : "Always false"); break; }
                    Add("(");
                    for (int i = 0; i < group.Children.Count; i++)
                    {
                        if (text.Length >= maximumLength) { truncated = true; break; }
                        if (i > 0) Add(group.All ? " AND " : " OR "); Visit(group.Children[i], depth + 1);
                    }
                    Add(")"); break;
                default: Add("Application-defined condition"); break;
            }
        }
        if (filter is not null) Visit(filter, 0);
        if (truncated) { if (text.Length == maximumLength) text.Length--; text.Append('…'); }
        return text.ToString();
    }
}

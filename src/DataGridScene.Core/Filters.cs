using System.Text.Json.Serialization;

namespace DataGridScene.Core;

public enum FilterOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, Contains, StartsWith, EndsWith, IsNull, IsNotNull, In, Between }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(FieldFilter), "field")]
[JsonDerivedType(typeof(FilterGroup), "group")]
[JsonDerivedType(typeof(NotFilter), "not")]
public abstract record GridFilter
{
    public abstract Func<GridRecord, bool> Compile(GridSchema schema);
    public static GridFilter And(params GridFilter[] filters) => new FilterGroup(true, filters);
    public static GridFilter Or(params GridFilter[] filters) => new FilterGroup(false, filters);
}

public sealed record FilterGroup(bool All, IReadOnlyList<GridFilter> Children) : GridFilter
{
    public override Func<GridRecord, bool> Compile(GridSchema schema)
    {
        if (Children.Count > 1024) throw new ArgumentException("Too many filter terms.");
        var predicates = Children.Select(child => child.Compile(schema)).ToArray();
        // Compile all children in order once, including branches that short-circuit
        // for a particular record. Do not allocate a capturing All/Any delegate for
        // every row; preserve left-to-right evaluation and exception behavior.
        if (predicates.Length == 0) return All ? _ => true : _ => false;
        if (predicates.Length == 1) return predicates[0];
        return All ? MatchAll : MatchAny;
        bool MatchAll(GridRecord record)
        {
            for (int i = 0; i < predicates.Length; i++) if (!predicates[i](record)) return false;
            return true;
        }
        bool MatchAny(GridRecord record)
        {
            for (int i = 0; i < predicates.Length; i++) if (predicates[i](record)) return true;
            return false;
        }
    }
}
public sealed record NotFilter(GridFilter Child) : GridFilter
{
    public override Func<GridRecord, bool> Compile(GridSchema schema)
    {
        var predicate = Child.Compile(schema);
        return record => !predicate(record);
    }
}
public sealed record FieldFilter(string Field, FilterOperator Operator, object? Value = null, object? Upper = null, IReadOnlyList<object?>? Values = null) : GridFilter
{
    public override Func<GridRecord, bool> Compile(GridSchema schema)
    {
        var column = schema[Field];
        int Compare(object? a, object? b) => column.Comparer?.Compare(a, b) ?? GridValue.Compare(a, b);
        object? ConvertLiteral(object? input)
        {
            if (!column.TryConvert(input, schema.Culture, out object? output, out string? error, validate: false))
                throw new FormatException($"Invalid filter for {column.Caption}: {error}");
            return output;
        }
        if (Operator is FilterOperator.IsNull) return record => column.GetValue(record) is null;
        if (Operator is FilterOperator.IsNotNull) return record => column.GetValue(record) is not null;
        if (Operator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith)
        {
            string needle = Convert.ToString(GridValue.Unwrap(Value), schema.Culture) ?? string.Empty;
            return record =>
            {
                var value = column.GetValue(record);
                if (value is null) return false;
                string text = column.Display(value, schema.Culture);
                return Operator switch
                {
                    FilterOperator.Contains => text.Contains(needle, StringComparison.OrdinalIgnoreCase),
                    FilterOperator.StartsWith => text.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                    _ => text.EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                };
            };
        }
        if (Operator == FilterOperator.In)
        {
            // Compile membership once and read a computed field only once per row.
            // A sorted set respects arbitrary comparison equality without inventing a hash.
            var values = new SortedSet<object?>((Values ?? Array.Empty<object?>()).Select(ConvertLiteral),
                Comparer<object?>.Create(Compare));
            return values.Count == 0 ? _ => false : record => values.Contains(column.GetValue(record));
        }
        object? lower = ConvertLiteral(Value);
        object? upper = Operator == FilterOperator.Between ? ConvertLiteral(Upper) : null;
        return record =>
        {
            object? actual = column.GetValue(record);
            if (Operator == FilterOperator.Equal) return Compare(actual, lower) == 0;
            if (Operator == FilterOperator.NotEqual) return Compare(actual, lower) != 0;
            if (actual is null || lower is null) return false;
            int result = Compare(actual, lower);
            return Operator switch
            {
                FilterOperator.Less => result < 0,
                FilterOperator.LessOrEqual => result <= 0,
                FilterOperator.Greater => result > 0,
                FilterOperator.GreaterOrEqual => result >= 0,
                FilterOperator.Between => upper is not null && result >= 0 && Compare(actual, upper) <= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(Operator))
            };
        };
    }
}

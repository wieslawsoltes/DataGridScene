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
        return record => All ? predicates.All(predicate => predicate(record)) : predicates.Any(predicate => predicate(record));
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
            input = GridValue.Unwrap(input);
            if (input is null) return null;
            if (!column.TryConvert(input, schema.Culture, out object? output, out string? error))
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
            var values = (Values ?? Array.Empty<object?>()).Select(ConvertLiteral).ToArray();
            return record => values.Any(value => Compare(column.GetValue(record), value) == 0);
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

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace DataGridScene.Core;

public enum ColumnAlignment { Left, Center, Right }
public enum ColumnPresentation { Text, CheckBox, Progress, Badge, Sparkline }

/// <summary>Immutable field metadata. Display order and widths belong to the layout package.</summary>
public sealed class GridColumn
{
    public string Field { get; }
    public string Caption { get; init; }
    public Type DataType { get; }
    public int Index { get; internal set; } = -1;
    public double Width { get; init; } = 140;
    public string? Format { get; init; }
    public string? Band { get; init; }
    public bool AllowNull { get; init; } = true;
    public bool ReadOnly { get; init; }
    public ColumnAlignment Alignment { get; init; }
    public ColumnPresentation Presentation { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public Func<object?, string?>? Validator { get; init; }
    public Func<GridRecord, object?>? ValueGetter { get; init; }
    public IComparer<object?>? Comparer { get; init; }
    public bool IsReadOnly => ReadOnly || ValueGetter is not null;

    public GridColumn(string field, Type dataType, string? caption = null)
    {
        if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("A field name is required.", nameof(field));
        ArgumentNullException.ThrowIfNull(dataType);
        Field = field; Caption = caption ?? field; DataType = Nullable.GetUnderlyingType(dataType) ?? dataType;
        Alignment = GridValue.IsNumericType(DataType) ? ColumnAlignment.Right : ColumnAlignment.Left;
        if (DataType == typeof(bool)) Presentation = ColumnPresentation.CheckBox;
    }
    public object? GetValue(GridRecord record) => ValueGetter is null ? record[Index] : ValueGetter(record);
    public string Display(object? value, CultureInfo? culture = null)
    {
        if (value is null) return string.Empty;
        culture ??= CultureInfo.InvariantCulture;
        return value is IFormattable formatted ? formatted.ToString(Format, culture) ?? string.Empty : value.ToString() ?? string.Empty;
    }
    public bool TryConvert(object? input, CultureInfo culture, out object? value, out string? error)
    {
        value = null; error = null;
        try
        {
            input = GridValue.Unwrap(input);
            if (input is null || (input is string blank && string.IsNullOrWhiteSpace(blank) && DataType != typeof(string)))
            {
                if (!AllowNull) throw new FormatException($"{Caption} is required.");
            }
            else if (DataType.IsInstanceOfType(input)) value = input;
            else if (DataType == typeof(string)) value = Convert.ToString(input, culture);
            else if (DataType == typeof(DateTime)) value = DateTime.Parse(Convert.ToString(input, culture)!, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind);
            else if (DataType == typeof(DateTimeOffset)) value = DateTimeOffset.Parse(Convert.ToString(input, culture)!, culture, DateTimeStyles.AllowWhiteSpaces);
            else if (DataType == typeof(Guid)) value = Guid.Parse(Convert.ToString(input, culture)!);
            else if (DataType == typeof(bool))
            {
                string booleanText = Convert.ToString(input, culture)!.Trim();
                value = booleanText == "1" ? true : booleanText == "0" ? false : bool.Parse(booleanText);
            }
            else if (DataType.IsEnum) value = Enum.Parse(DataType, Convert.ToString(input, culture)!, true);
            else
            {
                if (GridValue.IsIntegralType(DataType) && input is not string)
                {
                    decimal number = Convert.ToDecimal(input, culture);
                    if (number != decimal.Truncate(number)) throw new FormatException("A whole number is required.");
                }
                value = Convert.ChangeType(input, DataType, culture);
            }
            if (!AllowNull && (value is null || value is string requiredText && string.IsNullOrWhiteSpace(requiredText))) throw new FormatException($"{Caption} is required.");
            error = Validator?.Invoke(value); return error is null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        { error = ex.Message; return false; }
    }
}

public sealed class GridSchema
{
    private readonly Dictionary<string, GridColumn> _fields;
    public ReadOnlyCollection<GridColumn> Columns { get; }
    public CultureInfo Culture { get; }
    public Func<IReadOnlyList<object?>, string?>? RowValidator { get; init; }
    public GridSchema(IEnumerable<GridColumn> columns, CultureInfo? culture = null)
    {
        var materialized = columns.ToArray();
        if (materialized.Length == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        _fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (var column in materialized)
        {
            if (column.Index != -1) throw new ArgumentException("A column instance cannot belong to more than one schema.");
            if (!double.IsFinite(column.Width) || column.Width <= 0) throw new ArgumentException("Column widths must be finite and positive.");
            if (!_fields.TryAdd(column.Field, column)) throw new ArgumentException($"Duplicate field: {column.Field}");
        }
        for (int i = 0; i < materialized.Length; i++) materialized[i].Index = i;
        Columns = Array.AsReadOnly(materialized); Culture = culture ?? CultureInfo.InvariantCulture;
    }
    public GridColumn this[string field] => _fields.TryGetValue(field, out var column) ? column : throw new ArgumentException($"Unknown field: {field}", nameof(field));
    public bool TryGetColumn(string field, out GridColumn column) => _fields.TryGetValue(field, out column!);
    internal object?[] Normalize(IReadOnlyList<object?> values)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"Expected {Columns.Count} values, received {values.Count}.");
        var output = new object?[values.Count]; var errors = new List<GridValidationError>();
        for (int i = 0; i < values.Count; i++)
        {
            if (Columns[i].ValueGetter is not null) { output[i] = values[i]; continue; }
            if (!Columns[i].TryConvert(values[i], Culture, out output[i], out var error)) errors.Add(new(null, Columns[i].Field, error!));
        }
        string? rowError = errors.Count == 0 ? RowValidator?.Invoke(output) : null;
        if (rowError is not null) errors.Add(new(null, null, rowError));
        if (errors.Count > 0) throw new GridValidationException(errors);
        return output;
    }
}

/// <summary>Comparison rules shared by filters, ordering, grouping and summaries.</summary>
public static class GridValue
{
    public static bool IsIntegralType(Type type) => Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;
    public static bool IsNumericType(Type type) => IsIntegralType(type) || Type.GetTypeCode(type) is TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    public static object? Unwrap(object? value) => value is not JsonElement json ? value : json.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.TryGetDecimal(out var number) ? number : json.GetDouble(),
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new FormatException("A scalar value is required.")
    };
    public static int Compare(object? a, object? b)
    {
        a = Unwrap(a); b = Unwrap(b);
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is string sa && b is string sb) return StringComparer.OrdinalIgnoreCase.Compare(sa, sb);
        if (IsNumericType(a.GetType()) && IsNumericType(b.GetType()))
        {
            try { return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture)); }
            catch (OverflowException) { return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture)); }
        }
        if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
        return StringComparer.OrdinalIgnoreCase.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
    }
    public static string Key(object? value)
    {
        value = Unwrap(value);
        if (value is null) return "null:";
        if (value is string text) return "s:" + text.ToUpperInvariant();
        if (IsNumericType(value.GetType()))
        {
            try { return "n:" + Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture); }
            catch (OverflowException) { return "n:" + Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture); }
        }
        if (value is DateTime date) return "dt:" + date.Ticks;
        if (value is DateTimeOffset offset) return "dto:" + offset.UtcTicks;
        return value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

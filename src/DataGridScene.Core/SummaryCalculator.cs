using System.Collections.ObjectModel;
using System.Globalization;

namespace DataGridScene.Core;

/// <summary>A fresh accumulator is created for each custom summary and each group.</summary>
public interface IGridSummaryAccumulator
{
    void Add(GridRecord record, object? value);
    object? Complete();
}

public static class SummaryCalculator
{
    public static void Validate(GridSchema schema, IReadOnlyList<SummaryDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Count > 1024) throw new ArgumentException("At most 1,024 summaries are supported.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in descriptors)
        {
            if (item is null || !Enum.IsDefined(item.Kind)) throw new ArgumentException("Invalid summary descriptor.");
            var column = schema[item.Field]; _ = schema[item.TargetField];
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 1024 || !keys.Add(item.Key))
                throw new ArgumentException($"Invalid or duplicate summary key: {item.Key}");
            if (item.Kind is SummaryKind.Sum or SummaryKind.Average && !GridValue.IsNumericType(column.DataType))
                throw new ArgumentException($"{item.Kind} requires a numeric field: {column.Field}");
            if (item.Kind == SummaryKind.Custom)
            {
                if (item.CustomAggregate is null || !schema.CustomSummaries.TryGetValue(item.CustomAggregate, out var factory) || factory is null)
                    throw new ArgumentException($"Unknown custom summary: {item.CustomAggregate}");
            }
            else if (item.CustomAggregate is not null) throw new ArgumentException("CustomAggregate requires the Custom summary kind.");
            if (item.DisplayFormat is { } format) SummaryFormat.Validate(format);
        }
    }

    public static IReadOnlyDictionary<string, object?> Calculate(IReadOnlyList<GridRecord> rows, GridSchema schema, IReadOnlyList<SummaryDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Validate(schema, descriptors);
        var output = new Dictionary<string, object?>(descriptors.Count, StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var column = schema[descriptor.Field];
            if (descriptor.Kind == SummaryKind.Count) { output.Add(descriptor.Key, rows.Count); continue; }
            if (descriptor.Kind == SummaryKind.Custom)
            {
                var accumulator = schema.CustomSummaries[descriptor.CustomAggregate!]() ?? throw new InvalidOperationException("Custom summary factory returned null.");
                try
                {
                    foreach (var row in rows) accumulator.Add(row, column.GetValue(row));
                    output.Add(descriptor.Key, accumulator.Complete());
                }
                finally { (accumulator as IDisposable)?.Dispose(); }
                continue;
            }
            bool floating = column.DataType == typeof(double) || column.DataType == typeof(float);
            int count = 0; decimal sum = 0; double floatSum = 0, correction = 0; object? extreme = null;
            HashSet<string>? distinct = descriptor.Kind == SummaryKind.DistinctCount && column.Comparer is null ? [] : null;
            SortedSet<object?>? compared = descriptor.Kind == SummaryKind.DistinctCount && column.Comparer is not null ? new(column.Comparer) : null;
            foreach (var row in rows)
            {
                object? value = column.GetValue(row);
                if (value is null) continue;
                switch (descriptor.Kind)
                {
                    case SummaryKind.Sum:
                    case SummaryKind.Average:
                        if (!floating) sum = checked(sum + Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                        else
                        {
                            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture), next = floatSum + number;
                            // Neumaier compensation for finite values; retain ordinary IEEE behavior for nonfinite results.
                            if (double.IsFinite(next) && double.IsFinite(floatSum) && double.IsFinite(number))
                                correction += Math.Abs(floatSum) >= Math.Abs(number) ? (floatSum - next) + number : (number - next) + floatSum;
                            else correction = 0;
                            floatSum = next;
                        }
                        break;
                    case SummaryKind.Minimum:
                    case SummaryKind.Maximum:
                        int comparison = count == 0 ? 0 : column.Comparer?.Compare(value, extreme) ?? GridValue.Compare(value, extreme);
                        if (count == 0 || (descriptor.Kind == SummaryKind.Minimum ? comparison < 0 : comparison > 0)) extreme = value;
                        break;
                    case SummaryKind.DistinctCount:
                        if (compared is not null) compared.Add(value); else distinct!.Add(GridValue.Key(value));
                        break;
                }
                count++;
            }
            object total = floating ? (object)(floatSum + correction) : sum;
            output.Add(descriptor.Key, descriptor.Kind switch
            {
                SummaryKind.Sum => total,
                SummaryKind.Average => count == 0 ? null : floating ? (object)((floatSum + correction) / count) : sum / count,
                SummaryKind.Minimum or SummaryKind.Maximum => extreme,
                SummaryKind.DistinctCount => compared?.Count ?? distinct!.Count,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor.Kind))
            });
        }
        return new ReadOnlyDictionary<string, object?>(output);
    }
}

using System.Collections.ObjectModel;

namespace DataGridScene.Layout;

/// <summary>Immutable field presentation metadata. Order ties retain source order.</summary>
public sealed record RowEditFieldLayout(string Field)
{
    public string? Caption { get; init; }
    public int Order { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool StartNewRow { get; init; }
    public int ColumnSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
}
public sealed record RowEditFieldPlacement(string Field, int Row, int Column, int RowSpan, int ColumnSpan);
public sealed record RowEditLayoutPlan(int ColumnCount, int RowCount, ReadOnlyCollection<RowEditFieldPlacement> Fields);

/// <summary>Bounded, UI-independent responsive form packing. Never reads model values.
/// Keeps logical order and separate row/column spans without backfilling earlier holes.</summary>
public sealed class RowEditLayout
{
    public const int MaximumFields = 256;
    public ReadOnlyCollection<RowEditFieldLayout> Fields { get; }
    public int MaximumColumnCount { get; }
    public double MinimumColumnWidth { get; }
    public double ColumnSpacing { get; }

    public RowEditLayout(IEnumerable<string> fields, IEnumerable<RowEditFieldLayout>? overrides = null,
        int maximumColumnCount = 1, double minimumColumnWidth = 220, double columnSpacing = 16)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (maximumColumnCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumColumnCount));
        if (!double.IsFinite(minimumColumnWidth) || minimumColumnWidth is < 120 or > 2048) throw new ArgumentOutOfRangeException(nameof(minimumColumnWidth));
        if (!double.IsFinite(columnSpacing) || columnSpacing is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(columnSpacing));
        MaximumColumnCount = maximumColumnCount; MinimumColumnWidth = minimumColumnWidth; ColumnSpacing = columnSpacing;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<RowEditFieldLayout>();
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            if (ordered.Count == MaximumFields) throw new ArgumentException("Choose at most 256 form fields.", nameof(fields));
            if (!names.Add(field)) throw new ArgumentException("Form fields must be unique.", nameof(fields));
            ordered.Add(new(field));
        }
        if (ordered.Count == 0) throw new ArgumentException("At least one form field is required.", nameof(fields));
        var configured = new Dictionary<string, RowEditFieldLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in overrides ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Field);
            if (!names.Contains(item.Field)) throw new ArgumentException($"Unknown form field: {item.Field}", nameof(overrides));
            if (item.ColumnSpan is < 1 or > 4 || item.RowSpan is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(overrides), "Column spans must be 1–4 and row spans 1–8.");
            if (!configured.TryAdd(item.Field, item)) throw new ArgumentException("A field cannot have two form layout overrides.", nameof(overrides));
        }
        Fields = Array.AsReadOnly(ordered.Select(item => configured.TryGetValue(item.Field, out var value)
            ? value with { Field = item.Field } : item).OrderBy(item => item.Order).ToArray());
    }
    public int GetColumnCount(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth < 0) throw new ArgumentOutOfRangeException(nameof(availableWidth));
        // Clamp before conversion; even double.MaxValue must be a valid wide viewport.
        return (int)Math.Clamp(Math.Floor((availableWidth + ColumnSpacing) / (MinimumColumnWidth + ColumnSpacing)), 1, MaximumColumnCount);
    }
    public RowEditLayoutPlan Arrange(double availableWidth)
    {
        int columns = GetColumnCount(availableWidth), row = 0, column = 0, usedRows = 0;
        var occupied = new List<int>(); var result = new List<RowEditFieldPlacement>();
        foreach (var field in Fields)
        {
            if (!field.IsVisible) continue;
            int span = Math.Min(field.ColumnSpan, columns);
            if (field.StartNewRow && column > 0) { row++; column = 0; }
            while (true)
            {
                if (column + span > columns) { row++; column = 0; }
                while (occupied.Count < row + field.RowSpan) occupied.Add(0);
                int mask = ((1 << span) - 1) << column;
                bool free = true;
                for (int r = row; r < row + field.RowSpan; r++)
                    if ((occupied[r] & mask) != 0) { free = false; break; }
                if (!free) { column++; continue; }
                for (int r = row; r < row + field.RowSpan; r++) occupied[r] |= mask;
                result.Add(new(field.Field, row, column, field.RowSpan, span));
                usedRows = Math.Max(usedRows, row + field.RowSpan);
                column += span;
                if (column == columns) { row++; column = 0; }
                break;
            }
        }
        return new(columns, usedRows, result.AsReadOnly());
    }
}

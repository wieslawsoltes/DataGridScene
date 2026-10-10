using DataGridScene.Core;
using System.Collections.ObjectModel;

namespace DataGridScene.Layout;

public enum ColumnPin { None, Left, Right }
public sealed record ColumnState(string Field, double Width, int Order, bool Visible = true, ColumnPin Pin = ColumnPin.None)
{
    /// <summary>Caption path from outermost to innermost band. Null retains schema defaults on restore.</summary>
    public IReadOnlyList<string>? BandPath { get; init; }
}
public sealed record ColumnSlot(string Field, int SchemaIndex, double X, double Width, double ClipLeft, double ClipRight, ColumnPin Pin)
{
    public bool Contains(double x) => x >= ClipLeft && x < ClipRight;
}
public sealed record ColumnViewport(IReadOnlyList<ColumnSlot> Slots, double MiddleLeft, double MiddleRight, double MaximumOffset, double Offset)
{
    public ColumnSlot? HitTest(double x) => Slots.FirstOrDefault(slot => slot.Contains(x));
}
public sealed record BandSlot(string Key, string Caption, int Depth, int RowSpan, double X, double Width,
    double ClipLeft, double ClipRight, ColumnPin Pin, string FirstField)
{
    public bool Contains(double x, int bandRow) => x >= ClipLeft && x < ClipRight && bandRow >= Depth && bandRow < Depth + RowSpan;
}

/// <summary>
/// UI-independent geometry. Prefix indexes restrict scrolling work to O(log columns + visible columns).
/// Band spans and column sequences are rebuilt on structural/size changes, not every frame.
/// </summary>
public sealed class GridLayout
{
    private sealed record BandRange(string Key, string Caption, int Depth, int RowSpan, double Start, double End, string FirstField);
    private sealed class Pane
    {
        public ColumnState[] Columns { get; }
        public double[] Offsets { get; }
        public BandRange[][] Bands { get; }
        public double Width => Offsets[^1];
        public Pane(ColumnState[] columns, int levels)
        {
            Columns = columns;
            Offsets = new double[columns.Length + 1];
            for (int i = 0; i < columns.Length; i++) Offsets[i + 1] = Offsets[i] + columns[i].Width;
            Bands = new BandRange[levels][];
            for (int level = 0; level < levels; level++)
            {
                var ranges = new List<BandRange>();
                for (int start = 0; start < columns.Length;)
                {
                    var path = columns[start].BandPath!;
                    if (path.Count <= level) { start++; continue; }
                    string key = BandKey(path.Take(level + 1));
                    int end = start + 1;
                    while (end < columns.Length && columns[end].BandPath!.Count > level
                           && BandKey(columns[end].BandPath!.Take(level + 1)) == key) end++;
                    // A leaf band spans unused lower header rows. Mixed direct children and
                    // nested children intentionally leave blank cells beneath direct children.
                    bool leaf = true;
                    for (int i = start; i < end; i++) if (columns[i].BandPath!.Count > level + 1) { leaf = false; break; }
                    ranges.Add(new(key, path[level], level, leaf ? levels - level : 1, Offsets[start], Offsets[end], columns[start].Field));
                    start = end;
                }
                Bands[level] = ranges.ToArray();
            }
        }
    }
    private readonly GridSchema _schema;
    private ColumnState[] _columns = [];
    private ReadOnlyCollection<ColumnState> _readOnlyColumns = Array.AsReadOnly(Array.Empty<ColumnState>());
    private ReadOnlyCollection<ColumnState> _displayColumns = Array.AsReadOnly(Array.Empty<ColumnState>());
    private Dictionary<string, int> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private Pane _left = new([], 1), _middle = new([], 1), _right = new([], 1);
    public const double IndicatorWidth = 38;
    public const double MinimumWidth = 36;
    public const double MaximumWidth = 4096;
    public IReadOnlyList<ColumnState> Columns => _readOnlyColumns;
    public int BandLevels { get; private set; } = 1;
    public int Revision { get; private set; }
    public event EventHandler? Changed;

    public GridLayout(GridSchema schema)
    {
        _schema = schema;
        Publish(schema.Columns.Select(column => new ColumnState(column.Field, Math.Clamp(column.Width, MinimumWidth, MaximumWidth), column.Index)
        {
            BandPath = Array.AsReadOnly(new[] { column.Band ?? "General" })
        }).ToArray(), notify: false);
    }
    private void Publish(ColumnState[] columns, bool notify = true)
    {
        _columns = columns;
        _readOnlyColumns = Array.AsReadOnly(columns);
        _indexes = columns.Select((column, index) => (column.Field, index)).ToDictionary(pair => pair.Field, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var visible = columns.Where(column => column.Visible).OrderBy(column => PinOrder(column.Pin)).ThenBy(column => column.Order).ToArray();
        _displayColumns = Array.AsReadOnly(visible);
        BandLevels = Math.Max(1, visible.Length == 0 ? 1 : visible.Max(column => column.BandPath!.Count));
        _left = new(visible.Where(column => column.Pin == ColumnPin.Left).ToArray(), BandLevels);
        _middle = new(visible.Where(column => column.Pin == ColumnPin.None).ToArray(), BandLevels);
        _right = new(visible.Where(column => column.Pin == ColumnPin.Right).ToArray(), BandLevels);
        Revision++;
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }
    private static int PinOrder(ColumnPin pin) => pin == ColumnPin.Left ? 0 : pin == ColumnPin.None ? 1 : 2;
    private static void ValidateWidth(double width)
    {
        if (!double.IsFinite(width) || width < MinimumWidth || width > MaximumWidth) throw new ArgumentOutOfRangeException(nameof(width));
    }
    private void Replace(string field, Func<ColumnState, ColumnState> update)
    {
        if (!_indexes.TryGetValue(field, out int index)) throw new ArgumentException($"Unknown column: {field}", nameof(field));
        var next = (ColumnState[])_columns.Clone();
        next[index] = update(next[index]);
        Publish(next);
    }
    public void SetWidth(string field, double width) { ValidateWidth(width); Replace(field, column => column with { Width = width }); }
    public void SetVisible(string field, bool visible)
    {
        if (!visible && _displayColumns.Count == 1 && _displayColumns[0].Field.Equals(field, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("At least one column must remain visible.");
        Replace(field, column => column with { Visible = visible });
    }
    public void SetPin(string field, ColumnPin pin)
    {
        if (!Enum.IsDefined(pin)) throw new ArgumentOutOfRangeException(nameof(pin));
        Replace(field, column => column with { Pin = pin });
    }
    public void MoveBefore(string field, string beforeField)
    {
        string source = _schema[field].Field, destination = _schema[beforeField].Field;
        if (source == destination) return;
        var ordered = _columns.OrderBy(column => column.Order).ToList();
        var moving = ordered.Single(column => column.Field == source);
        ordered.Remove(moving);
        ordered.Insert(ordered.FindIndex(column => column.Field == destination), moving);
        Publish(ordered.Select((column, index) => column with { Order = index }).ToArray());
    }
    public IReadOnlyList<ColumnState> DisplayColumns() => _displayColumns;

    public static string BandKey(IEnumerable<string> path) => "/" + string.Join("/", path.Select(Uri.EscapeDataString));
    private static IReadOnlyList<string> FreezePath(IEnumerable<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Take(17).ToArray();
        if (segments.Length is 0 or > 16 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment.Length > 256))
            throw new ArgumentException("Band paths require 1–16 nonempty captions, each at most 256 characters.", nameof(path));
        return Array.AsReadOnly(segments);
    }
    public void SetBandPath(string field, params string[] path)
    {
        var frozen = FreezePath(path);
        Replace(field, column => column with { BandPath = frozen });
    }
    private ColumnState[] BandColumns(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var columns = _columns.Where(column =>
        {
            string candidate = BandKey(column.BandPath!);
            return candidate == key || candidate.StartsWith(key + "/", StringComparison.Ordinal);
        }).ToArray();
        if (columns.Length == 0) throw new ArgumentException($"Unknown band: {key}", nameof(key));
        return columns;
    }
    public IReadOnlyList<string> FieldsInBand(string key) => Array.AsReadOnly(BandColumns(key).OrderBy(column => column.Order).Select(column => column.Field).ToArray());
    public void SetBandPin(string key, ColumnPin pin)
    {
        if (!Enum.IsDefined(pin)) throw new ArgumentOutOfRangeException(nameof(pin));
        var fields = BandColumns(key).Select(column => column.Field).ToHashSet(StringComparer.Ordinal);
        Publish(_columns.Select(column => fields.Contains(column.Field) ? column with { Pin = pin } : column).ToArray());
    }
    public void SetBandVisible(string key, bool visible)
    {
        var fields = BandColumns(key).Select(column => column.Field).ToHashSet(StringComparer.Ordinal);
        var next = _columns.Select(column => fields.Contains(column.Field) ? column with { Visible = visible } : column).ToArray();
        if (!next.Any(column => column.Visible)) throw new InvalidOperationException("At least one column must remain visible.");
        Publish(next);
    }
    public void MoveBandBefore(string key, string beforeKey)
    {
        var moving = BandColumns(key).Select(column => column.Field).ToHashSet(StringComparer.Ordinal);
        var destination = BandColumns(beforeKey).Select(column => column.Field).ToHashSet(StringComparer.Ordinal);
        if (moving.SetEquals(destination)) return;
        if (key[..key.LastIndexOf('/')] != beforeKey[..beforeKey.LastIndexOf('/')])
            throw new InvalidOperationException("Bands can be reordered within the same parent. Use SetBandPath to reparent columns.");
        if (moving.Overlaps(destination)) throw new InvalidOperationException("A band cannot be moved into its own descendants.");
        var ordered = _columns.OrderBy(column => column.Order).ToArray();
        var remaining = ordered.Where(column => !moving.Contains(column.Field)).ToList();
        int index = remaining.FindIndex(column => destination.Contains(column.Field));
        remaining.InsertRange(index, ordered.Where(column => moving.Contains(column.Field)));
        Publish(remaining.Select((column, order) => column with { Order = order }).ToArray());
    }
    /// <summary>Resize the visible band columns proportionally, respecting every column's width limits.</summary>
    public void SetBandWidth(string key, double width, IReadOnlyDictionary<string, double>? originalWidths = null)
    {
        var columns = BandColumns(key).Where(column => column.Visible).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("A hidden band has no visible width.");
        if (!double.IsFinite(width) || width < columns.Length * MinimumWidth || width > columns.Length * MaximumWidth)
            throw new ArgumentOutOfRangeException(nameof(width));
        double Weight(ColumnState column) => originalWidths?.GetValueOrDefault(column.Field, column.Width) ?? column.Width;
        if (columns.Any(column => !double.IsFinite(Weight(column)) || Weight(column) <= 0)) throw new ArgumentException("Resize weights must be finite and positive.", nameof(originalWidths));
        var pending = columns.ToList();
        var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
        double remaining = width;
        while (pending.Count > 0)
        {
            double weight = pending.Sum(Weight);
            var clamped = pending.Where(column => remaining * Weight(column) / weight < MinimumWidth || remaining * Weight(column) / weight > MaximumWidth).ToArray();
            if (clamped.Length == 0)
            {
                foreach (var column in pending) sizes[column.Field] = remaining * Weight(column) / weight;
                break;
            }
            foreach (var column in clamped)
            {
                double size = Math.Clamp(remaining * Weight(column) / weight, MinimumWidth, MaximumWidth);
                sizes[column.Field] = size;
            }
            remaining -= clamped.Sum(column => sizes[column.Field]);
            pending.RemoveAll(column => sizes.ContainsKey(column.Field));
        }
        Publish(_columns.Select(column => sizes.TryGetValue(column.Field, out var size) ? column with { Width = size } : column).ToArray());
    }
    // First index with End > offset (exact boundaries belong to the next column/band).
    private static int StartIndex(double[] offsets, double offset)
    {
        int lo = 0, hi = offsets.Length - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (offsets[mid + 1] <= offset) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public ColumnViewport Compute(double width, double horizontalOffset)
    {
        if (!double.IsFinite(width) || !double.IsFinite(horizontalOffset)) throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Max(0, width);
        double middleLeft = Math.Min(width, IndicatorWidth + _left.Width);
        double middleRight = Math.Max(middleLeft, width - _right.Width);
        double maximum = Math.Max(0, _middle.Width - (middleRight - middleLeft));
        horizontalOffset = Math.Clamp(horizontalOffset, 0, maximum);
        var slots = new List<ColumnSlot>();
        void Add(Pane pane, double origin, double clipLeft, double clipRight)
        {
            if (clipRight <= clipLeft) return;
            int start = StartIndex(pane.Offsets, clipLeft - origin);
            for (int i = start; i < pane.Columns.Length && origin + pane.Offsets[i] < clipRight; i++)
            {
                var column = pane.Columns[i]; double x = origin + pane.Offsets[i];
                double left = Math.Max(x, clipLeft), right = Math.Min(x + column.Width, clipRight);
                if (right > left) slots.Add(new(column.Field, _schema[column.Field].Index, x, column.Width, left, right, column.Pin));
            }
        }
        Add(_middle, middleLeft - horizontalOffset, middleLeft, middleRight);
        Add(_left, IndicatorWidth, Math.Min(width, IndicatorWidth), middleLeft);
        Add(_right, width - _right.Width, middleRight, width);
        return new(slots.AsReadOnly(), middleLeft, middleRight, maximum, horizontalOffset);
    }
    public IReadOnlyList<BandSlot> ComputeBands(double width, ColumnViewport viewport)
    {
        if (!double.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Max(0, width);
        var slots = new List<BandSlot>();
        void Add(Pane pane, ColumnPin pin, double origin, double clipLeft, double clipRight)
        {
            if (clipRight <= clipLeft) return;
            foreach (var ranges in pane.Bands)
            {
                int lo = 0, hi = ranges.Length;
                while (lo < hi) { int mid = (lo + hi) / 2; if (ranges[mid].End + origin <= clipLeft) lo = mid + 1; else hi = mid; }
                for (int i = lo; i < ranges.Length && origin + ranges[i].Start < clipRight; i++)
                {
                    var range = ranges[i]; double x = origin + range.Start;
                    slots.Add(new(range.Key, range.Caption, range.Depth, range.RowSpan, x, range.End - range.Start,
                        Math.Max(x, clipLeft), Math.Min(origin + range.End, clipRight), pin, range.FirstField));
                }
            }
        }
        Add(_middle, ColumnPin.None, viewport.MiddleLeft - viewport.Offset, viewport.MiddleLeft, viewport.MiddleRight);
        Add(_left, ColumnPin.Left, IndicatorWidth, Math.Min(width, IndicatorWidth), viewport.MiddleLeft);
        Add(_right, ColumnPin.Right, width - _right.Width, viewport.MiddleRight, width);
        return slots.AsReadOnly();
    }
    public void RestoreColumns(IEnumerable<ColumnState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var incoming = states.Take(16385).ToArray();
        if (incoming.Length > 16384) throw new FormatException("Too many columns.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in incoming)
        {
            if (column is null || string.IsNullOrWhiteSpace(column.Field) || !names.Add(column.Field)) throw new FormatException("Invalid or duplicate columns in saved layout.");
            if (!double.IsFinite(column.Width) || column.Width < MinimumWidth || column.Width > MaximumWidth || !Enum.IsDefined(column.Pin))
                throw new FormatException("Invalid column geometry.");
            if (column.BandPath is not null) FreezePath(column.BandPath);
        }
        var restored = incoming.Where(column => _schema.TryGetColumn(column.Field, out _)).OrderBy(column => column.Order).Select(column =>
            column with { Field = _schema[column.Field].Field, BandPath = column.BandPath is null ? _columns[_indexes[column.Field]].BandPath : FreezePath(column.BandPath) }).ToList();
        var fields = restored.Select(column => column.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        restored.AddRange(_columns.Where(column => !fields.Contains(column.Field)).OrderBy(column => column.Order));
        if (!restored.Any(column => column.Visible)) throw new FormatException("A saved layout must have a visible column.");
        Publish(restored.Select((column, index) => column with { Order = index }).ToArray());
    }
}

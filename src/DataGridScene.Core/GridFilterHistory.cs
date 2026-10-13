using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DataGridScene.Core;

/// <summary>An owned, bounded filter-only snapshot. Search, sorting, grouping, summaries,
/// selection and the enabled flag are deliberately not part of filter history.
/// Capture validates structure, not record-dependent predicate execution; apply through a GridView.</summary>
public sealed class GridFilterState
{
    public const int MaximumNodes = 1024;
    public const int MaximumLiterals = 8192;
    public const int MaximumTextLength = 65536;
    private readonly string _key;
    public GridSchema Schema { get; }
    public GridFilter? Criteria { get; }
    public IReadOnlyDictionary<string, string> TextFilters { get; }
    public IReadOnlyDictionary<string, ColumnValueFilter> ValueFilters { get; }
    public GridFilter? EffectiveFilter { get; }
    public bool IsEmpty => EffectiveFilter is null;

    private GridFilterState(GridSchema schema, GridFilter? criteria, Dictionary<string, string> texts,
        Dictionary<string, ColumnValueFilter> values, string key)
    {
        Schema = schema; Criteria = criteria; _key = key;
        TextFilters = new ReadOnlyDictionary<string, string>(texts);
        ValueFilters = new ReadOnlyDictionary<string, ColumnValueFilter>(values);
        var terms = new List<GridFilter>();
        if (criteria is not null) terms.Add(criteria);
        foreach (var (field, text) in texts) if (CriteriaParser.AutoFilter(field, text) is { } filter) terms.Add(filter);
        foreach (var (field, selection) in values) if (selection.ToFilter(field) is { } filter) terms.Add(filter);
        // Preserve the same grouping as GridControl.ApplyFilter; never stringify/reparse
        // typed criteria or checked values, including large integers and timestamps.
        // The public effective root must own a read-only collection too. Exposing the
        // params array of GridFilter.And would let a caller corrupt retained history.
        EffectiveFilter = terms.Count == 0 ? null : texts.Count == 0 && values.Count == 0 ? criteria : new FilterGroup(true, terms.AsReadOnly());
    }

    public static GridFilterState Capture(GridSchema schema, GridFilter? criteria = null,
        IReadOnlyDictionary<string, string>? textFilters = null,
        IReadOnlyDictionary<string, ColumnValueFilter>? valueFilters = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var copy = new Builder(schema);
        copy.Token(criteria is null ? "no-criteria" : "criteria");
        var ownedCriteria = criteria is null ? null : copy.Filter(criteria, 0);
        copy.Token("texts");
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (textFilters is not null)
        {
            if (textFilters.Count is < 0 or > 1024) throw new ArgumentException("Too many text filters.");
            foreach (var (field, text) in textFilters)
            {
                if (text is null) throw new ArgumentException("A text filter cannot be null.");
                string canonical = schema[field].Field;
                if (texts.Count >= 1024 || !texts.TryAdd(canonical, text)) throw new ArgumentException("Duplicate or excessive text filters.");
            }

        }
        copy.Token(texts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (field, text) in texts.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { copy.Token(field); copy.Token(text); }
        copy.Token("values");
        var values = new Dictionary<string, ColumnValueFilter>(StringComparer.OrdinalIgnoreCase);
        if (valueFilters is not null)
        {
            if (valueFilters.Count is < 0 or > 1024) throw new ArgumentException("Too many value filters.");
            var bounded = valueFilters.Take(1025).ToArray();
            if (bounded.Length > 1024) throw new ArgumentException("Too many value filters.");
            copy.Token(bounded.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var (field, selection) in bounded.OrderBy(pair => schema[pair.Key].Field, StringComparer.Ordinal))
            {
                string canonical = schema[field].Field;
                if (texts.ContainsKey(canonical)) throw new ArgumentException("A field cannot have both text and checked-value filters.");
                if (selection?.Values is null || selection.Values.Count is < 0 or > ColumnValueFilter.MaximumValues)
                    throw new ArgumentException("Invalid checked-value filter.");
                copy.Token(canonical); copy.Token(selection.Exclude ? "exclude" : "include"); copy.Token(selection.Values.Count.ToString(CultureInfo.InvariantCulture));
                var literals = copy.Literals(selection.Values);
                if (values.Count >= 1024 || !values.TryAdd(canonical, new(Array.AsReadOnly(literals), selection.Exclude)))
                    throw new ArgumentException("Duplicate or excessive checked-value filters.");
            }
        }
        else copy.Token("0");
        return new(schema, ownedCriteria, texts, values, copy.Key);
    }

    public bool HasSameConditions(GridFilterState other) => other is not null && ReferenceEquals(Schema, other.Schema) && _key == other._key;

    /// <summary>Structural comparison ignoring transparent one-child AND wrappers, not general logical equivalence. Never invokes
    /// application comparers, getters or Equals implementations. Used to avoid no-op reshaping.</summary>
    public static bool FiltersEqual(GridFilter? left, GridFilter? right)
    {
        int nodes = 0, literals = 0;
        bool Equal(GridFilter? a, GridFilter? b, int depth)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || ++nodes > MaximumNodes || depth > 32) return false;
            if (a is FilterGroup { All: true, Children.Count: 1 } singleA) return Equal(singleA.Children[0], b, depth + 1);
            if (b is FilterGroup { All: true, Children.Count: 1 } singleB) return Equal(a, singleB.Children[0], depth + 1);
            return (a, b) switch
            {
                (FieldFilter x, FieldFilter y) => !string.IsNullOrEmpty(x.Field) && string.Equals(x.Field, y.Field, StringComparison.OrdinalIgnoreCase) && x.Operator == y.Operator &&
                    (literals += 2) <= MaximumLiterals && ScalarEqual(x.Value, y.Value) && ScalarEqual(x.Upper, y.Upper) && ValuesEqual(x.Values, y.Values, ref literals),
                (NotFilter x, NotFilter y) => Equal(x.Child, y.Child, depth + 1),
                (FilterGroup x, FilterGroup y) => GroupsEqual(x, y, depth),
                _ => false
            };
        }
        bool GroupsEqual(FilterGroup a, FilterGroup b, int depth)
        {
            if (a.All != b.All || a.Children is null || b.Children is null) return false;
            int count = a.Children.Count;
            if (count is < 0 or > MaximumNodes || count != b.Children.Count) return false;
            // IReadOnlyList exposes indexed access; do not invoke an unrelated custom
            // enumerator, which need not obey the collection's advertised count.
            for (int i = 0; i < count; i++) if (!Equal(a.Children[i], b.Children[i], depth + 1)) return false;
            return true;
        }
        return Equal(left, right, 0);
    }
    private static bool ValuesEqual(IReadOnlyList<object?>? a, IReadOnlyList<object?>? b, ref int literals)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        int count = a.Count;
        if (count is < 0 or > ColumnValueFilter.MaximumValues || count != b.Count || count > MaximumLiterals - literals) return false;
        literals += count;
        for (int i = 0; i < count; i++) if (!ScalarEqual(a[i], b[i])) return false;
        return true;
    }
    private static bool ScalarEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.GetType() != b.GetType()) return false;
        return a switch
        {
            double x => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits((double)b),
            float x => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits((float)b),
            decimal x => decimal.GetBits(x).SequenceEqual(decimal.GetBits((decimal)b)),
            DateTime x => x.Ticks == ((DateTime)b).Ticks && x.Kind == ((DateTime)b).Kind,
            DateTimeOffset x => x.Ticks == ((DateTimeOffset)b).Ticks && x.Offset == ((DateTimeOffset)b).Offset,
            JsonElement x => x.ValueKind == ((JsonElement)b).ValueKind && x.GetRawText() == ((JsonElement)b).GetRawText(),
            string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or Guid or TimeSpan or DateOnly or TimeOnly or Enum => a.Equals(b),
            _ => false
        };
    }
    private sealed class Builder(GridSchema schema)
    {
        private readonly StringBuilder _key = new();
        private int _nodes, _literals;
        public string Key => _key.ToString();
        public void Token(string value)
        {
            if (value.Length > MaximumTextLength - _key.Length - 12) throw new ArgumentException("Filter history text budget exceeded.");
            _key.Append(value.Length).Append(':').Append(value);
        }
        public object?[] Literals(IReadOnlyList<object?> values)
        {
            int count = values.Count;
            if (count is < 0 or > ColumnValueFilter.MaximumValues) throw new ArgumentException("Invalid literal count.");
            var result = new object?[count];
            for (int i = 0; i < count; i++) result[i] = Literal(values[i]);
            return result;
        }
        public GridFilter Filter(GridFilter filter, int depth)
        {
            if (filter is null || ++_nodes > MaximumNodes || depth > 32) throw new ArgumentException("Filter history structure limit exceeded.");
            switch (filter)
            {
                case FieldFilter field:
                    if (!Enum.IsDefined(field.Operator) || field.Values?.Count > ColumnValueFilter.MaximumValues) throw new ArgumentException("Invalid field filter.");
                    string name = schema[field.Field].Field;
                    Token("field"); Token(name); Token(((int)field.Operator).ToString(CultureInfo.InvariantCulture));
                    var value = Literal(field.Value); var upper = Literal(field.Upper);
                    Token(field.Values is null ? "null" : field.Values.Count.ToString(CultureInfo.InvariantCulture));
                    return field with { Field = name, Value = value, Upper = upper,
                        Values = field.Values is null ? null : Array.AsReadOnly(Literals(field.Values)) };
                case FilterGroup group:
                    if (group.Children is null || group.Children.Count is < 0 or > MaximumNodes) throw new ArgumentException("Invalid filter group.");
                    int count = group.Children.Count;
                    Token(group.All ? "and" : "or"); Token(count.ToString(CultureInfo.InvariantCulture));
                    var children = new GridFilter[count];
                    for (int i = 0; i < count; i++) children[i] = Filter(group.Children[i], depth + 1);
                    return new FilterGroup(group.All, Array.AsReadOnly(children));
                case NotFilter not: Token("not"); return new NotFilter(Filter(not.Child, depth + 1));
                default: throw new NotSupportedException("Application-defined predicates are not retained in automatic filter history.");
            }
        }
        public object? Literal(object? value)
        {
            if (++_literals > MaximumLiterals) throw new ArgumentException("Filter history literal budget exceeded.");
            Token(value is Enum ? value.GetType().AssemblyQualifiedName! : value?.GetType().FullName ?? "null");
            switch (value)
            {
                case null: break;
                case string text: Token(text); break;
                case double number: Token(BitConverter.DoubleToInt64Bits(number).ToString(CultureInfo.InvariantCulture)); break;
                case float number: Token(BitConverter.SingleToInt32Bits(number).ToString(CultureInfo.InvariantCulture)); break;
                case decimal number: foreach (int part in decimal.GetBits(number)) Token(part.ToString(CultureInfo.InvariantCulture)); break;
                case DateTime date: Token(date.Ticks.ToString(CultureInfo.InvariantCulture)); Token(((int)date.Kind).ToString(CultureInfo.InvariantCulture)); break;
                case DateTimeOffset date: Token(date.Ticks.ToString(CultureInfo.InvariantCulture)); Token(date.Offset.Ticks.ToString(CultureInfo.InvariantCulture)); break;
                case JsonElement json:
                    if (json.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined) throw new NotSupportedException("History requires scalar JSON literals.");
                    Token(json.GetRawText()); return json.Clone();
                case TimeOnly time: Token(time.Ticks.ToString(CultureInfo.InvariantCulture)); break;
                case DateOnly date: Token(date.DayNumber.ToString(CultureInfo.InvariantCulture)); break;
                case TimeSpan duration: Token(duration.Ticks.ToString(CultureInfo.InvariantCulture)); break;
                case char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or Guid or Enum:
                    Token(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
                default: throw new NotSupportedException("Mutable or application-defined literal values cannot be retained in filter history.");
            }
            return value;
        }
    }
}

public sealed record GridFilterHistoryEntry(long Id, GridFilterState State);

/// <summary>Bounded filter-only MRU storage. Immutable entry lists remain valid after changes.
/// Structural duplicates keep their identity and move to the front. Instances belong to one schema.</summary>
public sealed class GridFilterHistory
{
    public const int MaximumCapacity = 100;
    private static long _nextId;
    private int _capacity;
    public GridSchema Schema { get; }
    public IReadOnlyList<GridFilterHistoryEntry> Entries { get; private set; } = Array.Empty<GridFilterHistoryEntry>();
    public long Revision { get; private set; }
    public int Capacity
    {
        get => _capacity;
        set
        {
            if (value is < 0 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(value));
            _capacity = value;
            if (Entries.Count > value) Publish(Entries.Take(value).ToArray());
        }
    }
    public GridFilterHistory(GridSchema schema, int capacity = 10) { ArgumentNullException.ThrowIfNull(schema); Schema = schema; Capacity = capacity; }
    public GridFilterHistoryEntry? Remember(GridFilterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!ReferenceEquals(Schema, state.Schema)) throw new ArgumentException("History belongs to a different schema.", nameof(state));
        if (_capacity == 0 || state.IsEmpty) return null;
        int index = -1;
        for (int i = 0; i < Entries.Count; i++) if (Entries[i].State.HasSameConditions(state)) { index = i; break; }
        if (index == 0) return Entries[0];
        var entry = index < 0 ? new GridFilterHistoryEntry(Interlocked.Increment(ref _nextId), state) : Entries[index];
        Publish(new[] { entry }.Concat(Entries.Where((_, i) => i != index)).Take(_capacity).ToArray());
        return entry;
    }
    public bool Remove(long id)
    {
        var next = Entries.Where(entry => entry.Id != id).ToArray();
        if (next.Length == Entries.Count) return false;
        Publish(next); return true;
    }
    public void Clear() { if (Entries.Count > 0) Publish([]); }
    private void Publish(GridFilterHistoryEntry[] entries) { Entries = Array.AsReadOnly(entries); Revision++; }
}

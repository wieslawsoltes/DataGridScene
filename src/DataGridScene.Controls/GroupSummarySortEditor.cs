using System.Collections.ObjectModel;
using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Controls;

public sealed record SummarySortChoice(string Key, string Caption);

/// <summary>Staged, reusable native editor for ordered summary criteria at each
/// grouping level. It never owns or enumerates a table. BuildSorts validates pending
/// duplicates; hosts decide whether/when to apply the returned immutable snapshot.</summary>
public sealed class GroupSummarySortEditor : UserControl
{
    private sealed record Choice(string? Key, string Caption)
    {
        public override string ToString() => Caption;
    }
    private sealed class Level(string field, string caption)
    {
        internal readonly string Field = field, Caption = caption;
        internal readonly List<Row> Rows = [];
        internal readonly StackPanel Panel = new() { Spacing = 4 };
        internal Button Add = null!;
    }
    private sealed class Row
    {
        internal string? Key;
        internal readonly Grid Root = new() { ColumnSpacing = 6 };
        internal readonly TextBlock Priority = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        internal readonly ComboBox Summary = new() { MinWidth = 0, MinHeight = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        internal readonly ComboBox Direction = new() { MinWidth = 0, MinHeight = 28, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        internal Button Up = null!, Down = null!, Remove = null!;
    }
    private readonly List<Level> _levels = [];
    private readonly Dictionary<string, Level> _byField = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _actions = new(StringComparer.Ordinal);
    private readonly StackPanel _root = new() { Spacing = 8 };
    private readonly ScrollViewer _viewport;
    private readonly SolidColorBrush _border = new(Microsoft.UI.Colors.LightGray);
    private SummarySortChoice[] _metadata = [];
    private IReadOnlyList<Choice> _choices = Array.AsReadOnly(new[] { new Choice(null, "(Group value order)") });
    private Dictionary<string, Choice> _byKey = new(StringComparer.Ordinal);
    private bool _changing;
    public IReadOnlyDictionary<string, FrameworkElement> Actions { get; }
    public long CreatedRowCount { get; private set; }
    public long ChoiceRefreshCount { get; private set; }
    public long StructureRefreshCount { get; private set; }
    public double VerticalOffset => _viewport.VerticalOffset;
    public int CriterionCount => _levels.Sum(level => level.Rows.Count(row => row.Key is not null));

    public GroupSummarySortEditor(GridSchema schema, GridQuery query)
    {
        var snapshot = QuerySnapshot.Capture(query, schema);
        Actions = new ReadOnlyDictionary<string, FrameworkElement>(_actions);
        _viewport = new ScrollViewer { Content = _root, MaxHeight = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = _viewport;
        SetSummaries(snapshot.EffectiveGroupSummaries.Select(item => new SummarySortChoice(item.Key, $"{item.Kind}: {schema[item.Field].Caption}")));
        foreach (var group in snapshot.Groups)
        {
            var level = new Level(group.Field, schema[group.Field].Caption);
            _levels.Add(level); _byField.Add(level.Field, level);
            var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = level.Caption, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            level.Add = Button("+ Add criterion", "Add summary sort criterion for " + level.Caption, () => AddCriterion(level.Field));
            Grid.SetColumn(level.Add, 1); header.Children.Add(level.Add);
            var body = new StackPanel { Spacing = 5 }; body.Children.Add(header); body.Children.Add(level.Panel);
            _root.Children.Add(new Border { Child = body, Padding = new Thickness(8), BorderThickness = new Thickness(1), BorderBrush = _border });
            foreach (var sort in snapshot.GroupSummarySorts.Where(sort => sort.Field.Equals(level.Field, StringComparison.OrdinalIgnoreCase)))
                Append(level, sort.SummaryKey, sort.Direction);
            if (level.Rows.Count == 0) Append(level, null, SortDirection.Ascending);
        }
        if (_levels.Count == 0) _root.Children.Add(new TextBlock { Text = "Group the grid to enable group-summary sorting.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        ActualThemeChanged += (_, _) => RefreshTheme(); Loaded += (_, _) => RefreshTheme();
        Reindex();
    }
    private void RefreshTheme() => _border.Color = ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 74, 77, 82) : Windows.UI.Color.FromArgb(255, 215, 219, 224);
    private static Button Button(string caption, string name, Action action)
    {
        var button = new Button { Content = caption, MinWidth = 26, MinHeight = 26, FontSize = 12, Padding = new Thickness(5, 2, 5, 2) };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action(); return button;
    }
    private Row Append(Level level, string? key, SortDirection direction)
    {
        var row = new Row { Key = key };
        row.Root.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        row.Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Root.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        row.Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Summary.ItemsSource = _choices; row.Summary.SelectedItem = key is null ? _choices[0] : _byKey[key];
        row.Direction.ItemsSource = Enum.GetValues<SortDirection>(); row.Direction.SelectedItem = direction;
        row.Up = Button("↑", "Move criterion up", () => MoveCriterion(level.Field, level.Rows.IndexOf(row), level.Rows.IndexOf(row) - 1));
        row.Down = Button("↓", "Move criterion down", () => MoveCriterion(level.Field, level.Rows.IndexOf(row), level.Rows.IndexOf(row) + 1));
        row.Remove = Button("×", "Remove criterion", () => RemoveCriterion(level.Field, level.Rows.IndexOf(row)));
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        commands.Children.Add(row.Up); commands.Children.Add(row.Down); commands.Children.Add(row.Remove);
        row.Root.Children.Add(row.Priority); Grid.SetColumn(row.Summary, 1); row.Root.Children.Add(row.Summary);
        Grid.SetColumn(row.Direction, 2); row.Root.Children.Add(row.Direction); Grid.SetColumn(commands, 3); row.Root.Children.Add(commands);
        level.Rows.Add(row); level.Panel.Children.Add(row.Root); CreatedRowCount++;
        row.Summary.SelectionChanged += (_, _) => {
            if (_changing || row.Summary.SelectedItem is not Choice choice) return;
            row.Key = choice.Key;
            if (row.Key is null)
            {
                int index = level.Rows.IndexOf(row);
                if (index == 0) ClearLevel(level); else RemoveCriterion(level.Field, index);
            }
            else Reindex();
        };
        return row;
    }
    private Level GetLevel(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _byField.TryGetValue(field, out var level) ? level : throw new ArgumentException("Unknown grouping level: " + field, nameof(field));
    }
    private static Row GetRow(Level level, int index) => index >= 0 && index < level.Rows.Count
        ? level.Rows[index] : throw new ArgumentOutOfRangeException(nameof(index));
    private void ClearLevel(Level level)
    {
        _changing = true;
        try
        {
            while (level.Rows.Count > 1) { level.Rows.RemoveAt(level.Rows.Count - 1); level.Panel.Children.RemoveAt(level.Panel.Children.Count - 1); }
            var first = level.Rows[0]; first.Key = null; first.Summary.SelectedItem = _choices[0];
        }
        finally { _changing = false; }
        Reindex();
    }
    /// <summary>Add the first unused summary key. Returns false when there are no
    /// remaining keys or the shared query limit (1,024 criteria) has been reached.</summary>
    public bool AddCriterion(string field)
    {
        var level = GetLevel(field);
        if (CriterionCount >= 1024) return false;
        var used = level.Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var next = _metadata.FirstOrDefault(item => !used.Contains(item.Key));
        if (next is null) return false;
        if (level.Rows.Count == 1 && level.Rows[0].Key is null)
            SetCriterion(field, 0, next.Key, SortDirection.Ascending);
        else { Append(level, next.Key, SortDirection.Ascending); Reindex(); }
        return true;
    }
    /// <summary>Stage a choice. Duplicate keys remain visible until BuildSorts
    /// reports validation. Null clears a level at index zero, or removes a later row.</summary>
    public void SetCriterion(string field, int index, string? key, SortDirection direction)
    {
        var level = GetLevel(field); var row = GetRow(level, index);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (key is not null && !_byKey.ContainsKey(key)) throw new ArgumentException("Unknown summary key: " + key, nameof(key));
        if (key is not null && row.Key is null && CriterionCount >= 1024) throw new InvalidOperationException("At most 1,024 sort criteria are supported.");
        if (key is null) { if (index == 0) ClearLevel(level); else RemoveCriterion(field, index); return; }
        _changing = true;
        try { row.Key = key; row.Summary.SelectedItem = _byKey[key]; row.Direction.SelectedItem = direction; }
        finally { _changing = false; }
        Reindex();
    }
    public void RemoveCriterion(string field, int index)
    {
        var level = GetLevel(field); var row = GetRow(level, index);
        if (level.Rows.Count == 1) { if (row.Key is not null) ClearLevel(level); return; }
        level.Rows.RemoveAt(index); level.Panel.Children.RemoveAt(index); Reindex();
    }
    public void MoveCriterion(string field, int index, int destination)
    {
        var level = GetLevel(field); var row = GetRow(level, index); _ = GetRow(level, destination);
        if (index == destination) return;
        level.Rows.RemoveAt(index); level.Rows.Insert(destination, row);
        // Move just the existing row, preserving all editor instances and choices.
        level.Panel.Children.RemoveAt(index); level.Panel.Children.Insert(destination, row.Root); Reindex();
    }
    /// <summary>Copy bounded field metadata before touching controls. Removing a
    /// summary removes only its referencing criteria; all other priorities survive.</summary>
    public void SetSummaries(IEnumerable<SummarySortChoice> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var copy = new List<SummarySortChoice>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in summaries)
        {
            if (copy.Count >= 1024 || item is null || item.Key is null || item.Caption is null ||
                item.Key.Length > 16384 || item.Caption.Length > 16384 || !keys.Add(item.Key))
                throw new ArgumentException("Invalid, duplicate or excessive summary choices.", nameof(summaries));
            copy.Add(item);
        }
        if (_metadata.SequenceEqual(copy)) return;
        var choices = new List<Choice> { new(null, "(Group value order)") };
        choices.AddRange(copy.Select(item => new Choice(item.Key, item.Caption)));
        var lookup = choices.Skip(1).ToDictionary(item => item.Key!, StringComparer.Ordinal);
        _changing = true;
        try
        {
            _metadata = copy.ToArray(); _choices = choices.AsReadOnly(); _byKey = lookup;
            foreach (var level in _levels)
            {
                for (int i = level.Rows.Count - 1; i >= 0; i--)
                    if (level.Rows[i].Key is string key && !keys.Contains(key))
                    {
                        if (level.Rows.Count == 1) level.Rows[i].Key = null;
                        else { level.Rows.RemoveAt(i); level.Panel.Children.RemoveAt(i); }
                    }
                foreach (var row in level.Rows)
                {
                    row.Summary.ItemsSource = _choices;
                    row.Summary.SelectedItem = row.Key is null ? _choices[0] : _byKey[row.Key]; ChoiceRefreshCount++;
                }
            }
        }
        finally { _changing = false; }
        Reindex();
    }
    private void Reindex()
    {
        StructureRefreshCount++; _actions.Clear(); _actions["sort-viewport"] = _viewport;
        int total = CriterionCount;
        foreach (var level in _levels)
        {
            _actions["sort-add-" + level.Field] = level.Add;
            var used = level.Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            level.Add.IsEnabled = total < 1024 && _metadata.Any(item => !used.Contains(item.Key));
            for (int i = 0; i < level.Rows.Count; i++)
            {
                var row = level.Rows[i]; row.Priority.Text = (i + 1).ToString();
                row.Up.IsEnabled = i > 0; row.Down.IsEnabled = i + 1 < level.Rows.Count;
                row.Remove.IsEnabled = row.Key is not null; row.Direction.IsEnabled = row.Key is not null;
                string suffix = level.Field + (i == 0 ? "" : "-" + i);
                _actions["sort-summary-" + suffix] = row.Summary; _actions["sort-direction-" + suffix] = row.Direction;
                _actions[$"sort-up-{level.Field}-{i}"] = row.Up; _actions[$"sort-down-{level.Field}-{i}"] = row.Down;
                _actions[$"sort-remove-{level.Field}-{i}"] = row.Remove;
                string label = level.Caption + " criterion " + (i + 1);
                AutomationProperties.SetName(row.Summary, label + " summary"); AutomationProperties.SetName(row.Direction, label + " direction");
                AutomationProperties.SetName(row.Up, "Move " + label + " up"); AutomationProperties.SetName(row.Down, "Move " + label + " down");
                AutomationProperties.SetName(row.Remove, "Remove " + label);
            }
        }
    }
    public IReadOnlyList<GroupSummarySortDescriptor> BuildSorts()
    {
        var output = new List<GroupSummarySortDescriptor>();
        foreach (var level in _levels)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in level.Rows)
            {
                if (row.Key is null) continue;
                if (!_byKey.ContainsKey(row.Key) || !seen.Add(row.Key))
                    throw new ArgumentException(level.Caption + " contains a duplicate or missing summary criterion: " + row.Key);
                if (row.Direction.SelectedItem is not SortDirection direction || !Enum.IsDefined(direction))
                    throw new ArgumentException(level.Caption + " contains an invalid sort direction.");
                if (output.Count >= 1024) throw new ArgumentException("At most 1,024 sort criteria are supported.");
                output.Add(new(level.Field, row.Key, direction));
            }
        }
        return output.AsReadOnly();
    }
}

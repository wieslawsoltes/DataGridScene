using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DataGridScene.Controls;

/// <summary>Reusable staged total/group summary editor. No bound grid changes until the host applies BuildQuery().</summary>
public sealed class SummaryEditor : UserControl
{
    private readonly GridSchema _schema;
    private readonly GridQuery _query;
    private readonly Button _applyButton;
    public bool ShowApplyButton { get => _applyButton.Visibility == Visibility.Visible; set => _applyButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    private readonly ComboBox _scope;
    private readonly ComboBox _display;
    private readonly ComboBox _footers;
    private readonly StackPanel _items = new() { Spacing = 10 };
    private readonly SolidColorBrush _itemBorder = new(Microsoft.UI.Colors.LightGray);
    private readonly SolidColorBrush _errorBrush = new(Microsoft.UI.Colors.Red);
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly List<ItemEditor> _totals = [], _groups = [];
    private readonly Dictionary<string, FrameworkElement> _actions = new();
    public GroupSummarySortEditor SortEditor { get; }
    public event EventHandler? ApplyRequested;
    public string? ValidationMessage { get => _error.Text; set => _error.Text = value ?? ""; }
    public bool EditingGroups => _scope.SelectedIndex == 1;
    public int ItemCount => EditingGroups ? _groups.Count : _totals.Count;

    public SummaryEditor(GridSchema schema, GridQuery query, bool groups = true)
    {
        _schema = schema; _query = QuerySnapshot.Capture(query, schema);
        SortEditor = new(schema, _query);
        var root = new StackPanel { Spacing = 10, MaxWidth = 660 };
        _scope = Combo("scope", new[] { "Total summaries", "Group summaries" }, groups ? 1 : 0);
        _display = Combo("display", Enum.GetValues<GroupSummaryDisplayMode>(), (int)_query.GroupSummaryDisplay);
        _footers = Combo("footers", Enum.GetValues<GroupFooterDisplayMode>(), (int)_query.GroupFooterDisplay);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(_scope);
        var add = ActionButton("add", "+ Add summary", () =>
        {
            var list = EditingGroups ? _groups : _totals;
            if (list.Count >= 1024) { ValidationMessage = "At most 1,024 summaries are supported."; return; }
            var column = _schema.Columns.First();
            list.Add(new ItemEditor(this, new(column.Field, SummaryKind.Count, Guid.NewGuid().ToString("N")), EditingGroups));
            Rebuild();
        });
        header.Children.Add(add);
        _applyButton = ActionButton("apply", "Apply summaries", () => ApplyRequested?.Invoke(this, EventArgs.Empty));
        header.Children.Add(_applyButton); root.Children.Add(header);
        root.Children.Add(new TextBlock { Text = "Each summary has a source field, calculation, display column and optional format.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new ScrollViewer { Content = _items, Height = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        options.Children.Add(Labelled("Group row display", _display)); options.Children.Add(Labelled("Group footers", _footers)); root.Children.Add(options);
        root.Children.Add(new TextBlock { Text = "Group sort priority · criteria are evaluated from top to bottom", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(SortEditor);
        _error.Foreground = _errorBrush; AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        root.Children.Add(_error);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        foreach (var item in _query.Summaries) _totals.Add(new ItemEditor(this, item, false));
        foreach (var item in _query.EffectiveGroupSummaries) _groups.Add(new ItemEditor(this, item, true));
        _scope.SelectionChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => RefreshTheme(); Loaded += (_, _) => RefreshTheme();
        Rebuild();
    }
    private void RefreshTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _itemBorder.Color = dark ? Windows.UI.Color.FromArgb(255, 74, 77, 82) : Windows.UI.Color.FromArgb(255, 215, 219, 224);
        _errorBrush.Color = dark ? Windows.UI.Color.FromArgb(255, 255, 180, 171) : Windows.UI.Color.FromArgb(255, 180, 35, 24);
    }
    private static StackPanel Labelled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11 }); panel.Children.Add(control); return panel;
    }
    private ComboBox Combo(string id, object items, int index)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = index, MinWidth = 150 };
        _actions[id] = combo; AutomationProperties.SetName(combo, id); return combo;
    }
    private Button ActionButton(string id, string caption, Action action)
    {
        var button = new Button { Content = caption, Padding = new Thickness(10, 4, 10, 4) };
        _actions[id] = button; AutomationProperties.SetName(button, caption);
        button.Click += (_, _) => action(); return button;
    }
    private void Rebuild()
    {
        _items.Children.Clear();
        foreach (string key in _actions.Keys.Where(key => key.StartsWith("item-", StringComparison.Ordinal)).ToArray()) _actions.Remove(key);
        var list = EditingGroups ? _groups : _totals;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i]; _items.Children.Add(item.Root);
            foreach (var (name, element) in item.Actions) _actions["item-" + i + "-" + name] = element;
        }
        RefreshSortChoices();
    }
    private void RefreshSortChoices() => SortEditor.SetSummaries(_groups.Select(item => new SummarySortChoice(item.Key, item.Caption)));
    private sealed class ItemEditor
    {
        private readonly SummaryDescriptor _original;
        private readonly ComboBox _field, _kind, _target, _custom;
        private readonly TextBox _format;
        private readonly CheckBox _row, _footer;
        public string Key => _original.Key;
        public string Caption => $"{_kind.SelectedItem}: {_field.SelectedItem}";
        public Border Root { get; }
        public Dictionary<string, FrameworkElement> Actions { get; } = new();
        public ItemEditor(SummaryEditor owner, SummaryDescriptor item, bool group)
        {
            _original = item;
            string[] fields = owner._schema.Columns.Select(column => column.Field).ToArray();
            ComboBox Choice(string id, object values, object? selected)
            {
                var combo = new ComboBox { ItemsSource = values, SelectedItem = selected, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                Actions[id] = combo; AutomationProperties.SetName(combo, id); return combo;
            }
            _field = Choice("field", fields, owner._schema[item.Field].Field);
            _kind = Choice("kind", Enum.GetValues<SummaryKind>().Where(kind => kind != SummaryKind.Custom || owner._schema.CustomSummaries.Count > 0).ToArray(), item.Kind);
            _target = Choice("target", fields, owner._schema[item.TargetField].Field);
            _custom = Choice("custom", owner._schema.CustomSummaries.Keys.ToArray(), item.CustomAggregate ?? owner._schema.CustomSummaries.Keys.FirstOrDefault());
            _format = new TextBox { Text = item.DisplayFormat ?? "", PlaceholderText = "Format: {0:N2} · {1} is caption · {2} is calculation", MinHeight = 28, FontSize = 12 };
            Actions["format"] = _format;
            _row = new CheckBox { Content = "Group row", IsChecked = item.ShowInGroupRow, MinHeight = 26 };
            _footer = new CheckBox { Content = "Group footer", IsChecked = item.ShowInGroupFooter, MinHeight = 26 };
            Actions["row"] = _row; Actions["footer"] = _footer;
            var fieldsRow = new Grid { ColumnSpacing = 7 };
            for (int i = 0; i < 3; i++) fieldsRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            fieldsRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Grid.SetColumn(_kind, 1); Grid.SetColumn(_target, 2);
            fieldsRow.Children.Add(_field); fieldsRow.Children.Add(_kind); fieldsRow.Children.Add(_target);
            var remove = new Button { Content = "×", MinWidth = 28, Padding = new Thickness(5) }; Actions["remove"] = remove;
            AutomationProperties.SetName(remove, "Remove summary " + item.Key);
            remove.Click += (_, _) => { (group ? owner._groups : owner._totals).Remove(this); owner.Rebuild(); };
            Grid.SetColumn(remove, 3); fieldsRow.Children.Add(remove);
            var visibility = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            if (group) { visibility.Children.Add(_row); visibility.Children.Add(_footer); }
            visibility.Children.Add(_custom);
            var body = new StackPanel { Spacing = 5 }; body.Children.Add(fieldsRow); body.Children.Add(_format); body.Children.Add(visibility);
            Root = new Border { BorderBrush = owner._itemBorder, BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = body };
            void Update() { _custom.Visibility = _kind.SelectedItem is SummaryKind.Custom ? Visibility.Visible : Visibility.Collapsed; owner.RefreshSortChoices(); }
            _kind.SelectionChanged += (_, _) => Update(); _field.SelectionChanged += (_, _) => Update();
            _custom.Visibility = item.Kind == SummaryKind.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        public SummaryDescriptor Build() => _original with
        {
            Name = _original.Key, Field = (string)_field.SelectedItem,
            Kind = (SummaryKind)_kind.SelectedItem, ShowInColumn = (string)_target.SelectedItem,
            DisplayFormat = string.IsNullOrWhiteSpace(_format.Text) ? null : _format.Text,
            ShowInGroupRow = _row.IsChecked == true, ShowInGroupFooter = _footer.IsChecked == true,
            CustomAggregate = _kind.SelectedItem is SummaryKind.Custom ? (string?)_custom.SelectedItem : null
        };
    }
    public GridQuery BuildQuery()
    {
        var query = _query with
        {
            Summaries = _totals.Select(item => item.Build()).ToArray(), GroupSummaries = _groups.Select(item => item.Build()).ToArray(),
            GroupSummaryDisplay = (GroupSummaryDisplayMode)_display.SelectedItem,
            GroupFooterDisplay = (GroupFooterDisplayMode)_footers.SelectedItem,
            GroupSummarySorts = SortEditor.BuildSorts()
        };
        return QuerySnapshot.Capture(query, _schema);
    }
    public IReadOnlyDictionary<string, RectD> ActionBounds(UIElement relativeTo)
    {
        var result = new Dictionary<string, RectD>();
        foreach (var (name, element) in _actions.Concat(SortEditor.Actions))
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility != Visibility.Visible || !element.IsLoaded) continue;
            var point = element.TransformToVisual(relativeTo).TransformPoint(new Point());
            result["summary-" + name] = new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        return result;
    }
}

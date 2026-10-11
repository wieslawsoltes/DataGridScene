using DataGridScene.Core;
using DataGridScene.Export;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Controls;

/// <summary>Composable visual AND/OR/NOT criteria editor. Builds the same typed AST used by local and remote data sources.</summary>
public sealed class FilterBuilder : UserControl
{
    private readonly GridSchema _schema;
    private readonly GroupEditor _root;
    public FilterBuilder(GridSchema schema, GridFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        filter = QuerySnapshot.Capture(new() { Filter = filter }, schema).Filter;
        ValidateEditable(filter);
        _root = new GroupEditor(this, filter is FilterGroup group ? group : new FilterGroup(true, filter is null ? [] : [filter]), 0);
        Content = new ScrollViewer { Content = _root.Root, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public GridFilter? BuildFilter()
    {
        GridFilter? filter = _root.Build();
        if (filter is FilterGroup { All: true, Children.Count: 0 }) return null;
        return QuerySnapshot.Capture(new() { Filter = filter }, _schema).Filter;
    }
    private static void ValidateEditable(GridFilter? filter)
    {
        switch (filter)
        {
            case null or FieldFilter: return;
            case FilterGroup group: foreach (var child in group.Children) ValidateEditable(child); return;
            case NotFilter not: ValidateEditable(not.Child); return;
            default: throw new NotSupportedException("The visual builder cannot edit this application-defined filter. It has not been removed or changed.");
        }
    }
    private interface IEditor { UIElement Root { get; } GridFilter? Build(); }
    private sealed class GroupEditor : IEditor
    {
        private readonly FilterBuilder _owner;
        private readonly int _depth;
        private readonly ComboBox _operation = new() { ItemsSource = new[] { "All conditions (AND)", "Any condition (OR)" }, MinWidth = 180 };
        private readonly CheckBox _not = new() { Content = "NOT" };
        private readonly StackPanel _children = new() { Spacing = 8, Margin = new Thickness(14, 0, 0, 0) };
        private readonly List<IEditor> _editors = [];
        public UIElement Root { get; }
        public GroupEditor(FilterBuilder owner, FilterGroup filter, int depth)
        {
            _owner = owner; _depth = depth; _operation.SelectedIndex = filter.All ? 0 : 1;
            var body = new StackPanel { Spacing = 8, MinWidth = 580 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(_operation); header.Children.Add(_not);
            var condition = new Button { Content = "+ Condition" }; condition.Click += (_, _) => Add(new ConditionEditor(owner, new FieldFilter(owner._schema.Columns[0].Field, FilterOperator.Equal, "")));
            var group = new Button { Content = "+ Group", IsEnabled = depth < 8 }; group.Click += (_, _) => Add(new GroupEditor(owner, new FilterGroup(true, []), depth + 1));
            header.Children.Add(condition); header.Children.Add(group); body.Children.Add(header); body.Children.Add(_children);
            Root = new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray), Padding = new Thickness(10), Child = body };
            foreach (var child in filter.Children)
            {
                if (child is FilterGroup nested) Add(new GroupEditor(owner, nested, depth + 1));
                else if (child is NotFilter { Child: FilterGroup negated }) { var editor = new GroupEditor(owner, negated, depth + 1); editor._not.IsChecked = true; Add(editor); }
                else if (child is NotFilter not) { var editor = new GroupEditor(owner, new FilterGroup(true, [not.Child]), depth + 1); editor._not.IsChecked = true; Add(editor); }
                else if (child is FieldFilter field) Add(new ConditionEditor(owner, field));
            }
        }
        private void Add(IEditor editor)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(editor.Root); var remove = new Button { Content = "×", VerticalAlignment = VerticalAlignment.Top, MinWidth = 30 };
            remove.Click += (_, _) => { _editors.Remove(editor); _children.Children.Remove(row); };
            Grid.SetColumn(remove, 1); row.Children.Add(remove); _editors.Add(editor); _children.Children.Add(row);
        }
        public GridFilter? Build()
        {
            var conditions = _editors.Select(editor => editor.Build()).OfType<GridFilter>().ToArray();
            GridFilter result = new FilterGroup(_operation.SelectedIndex == 0, conditions);
            return _not.IsChecked == true ? new NotFilter(result) : result;
        }
    }
    private sealed class ConditionEditor : IEditor
    {
        private readonly FieldFilter _initial;
        private readonly string _initialValue, _initialUpper;
        private readonly ComboBox _field;
        private readonly ComboBox _operator;
        private readonly TextBox _value = new() { MinWidth = 140, PlaceholderText = "Value" };
        private readonly TextBox _upper = new() { MinWidth = 140, PlaceholderText = "Upper bound" };
        public UIElement Root { get; }
        public ConditionEditor(FilterBuilder owner, FieldFilter filter)
        {
            var column = owner._schema[filter.Field];
            _initial = filter = filter with { Field = column.Field };
            string Display(object? value, bool text = false)
            {
                if (text) return Convert.ToString(GridValue.Unwrap(value), owner._schema.Culture) ?? "";
                // Schema-directed conversion avoids the decimal-first untyped JSON path.
                if (!column.TryConvert(value, owner._schema.Culture, out var converted, out var error, validate: false))
                    throw new FormatException(error);
                return converted switch
                {
                    DateTime date => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset date => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    _ => Convert.ToString(converted, owner._schema.Culture) ?? ""
                };
            }
            _field = new() { ItemsSource = owner._schema.Columns.Select(column => column.Field).ToArray(), SelectedItem = filter.Field, MinWidth = 140 };
            _operator = new() { ItemsSource = Enum.GetValues<FilterOperator>(), SelectedItem = filter.Operator, MinWidth = 150 };
            _initialValue = _value.Text = filter.Operator == FilterOperator.In
                ? string.Join(",", (filter.Values ?? []).Select(value => DelimitedText.Escape(Display(value))))
                : filter.Operator is FilterOperator.IsNull or FilterOperator.IsNotNull ? ""
                : Display(filter.Value, filter.Operator is FilterOperator.Contains or FilterOperator.StartsWith or FilterOperator.EndsWith);
            _initialUpper = _upper.Text = filter.Operator == FilterOperator.Between ? Display(filter.Upper) : "";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; row.Children.Add(_field); row.Children.Add(_operator); row.Children.Add(_value); row.Children.Add(_upper); Root = row;
            _operator.SelectionChanged += (_, _) => Update(); Update();
        }
        private void Update()
        {
            var operation = _operator.SelectedItem is FilterOperator value ? value : FilterOperator.Equal;
            _value.Visibility = operation is FilterOperator.IsNull or FilterOperator.IsNotNull ? Visibility.Collapsed : Visibility.Visible;
            _upper.Visibility = operation == FilterOperator.Between ? Visibility.Visible : Visibility.Collapsed;
            _value.PlaceholderText = operation == FilterOperator.In ? "Comma-separated values" : "Value";
        }
        public GridFilter Build()
        {
            string field = (string?)_field.SelectedItem ?? throw new FormatException("Choose a field.");
            var operation = (FilterOperator)(_operator.SelectedItem ?? FilterOperator.Equal);
            bool sameField = field == _initial.Field;
            bool sameValue = sameField && _value.Text == _initialValue;
            IReadOnlyList<object?>? values = operation == FilterOperator.In
                ? sameValue && _initial.Operator == FilterOperator.In ? _initial.Values
                    : DelimitedText.Parse(_value.Text).SelectMany(row => row).Cast<object?>().ToArray() : null;
            // Untouched editors retain nulls and exact typed values, not their display strings.
            return new FieldFilter(field, operation, sameValue && _initial.Operator is not (FilterOperator.In or FilterOperator.IsNull or FilterOperator.IsNotNull) ? _initial.Value : _value.Text,
                sameField && _upper.Text == _initialUpper ? _initial.Upper : _upper.Text, values);
        }
    }
}

using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DataGridScene.Controls;

/// <summary>Reusable staged value-filter editor with native virtualized checked items. Searching
/// discovers values across the table and retains checkbox decisions made in previous searches.</summary>
public sealed class ColumnFilterEditor : UserControl
{
    private readonly GridTable _table;
    private readonly ColumnValueSelection _selection;
    private readonly TextBox _search = new() { PlaceholderText = "Search values…", MinWidth = 0 };
    private readonly TextBox _condition = new() { PlaceholderText = "Condition: contains, =value, >=value…", MinWidth = 0 };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, Height = 240 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly CheckBox _visible = new() { Content = "Select visible results", IsThreeState = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, FrameworkElement> _actions = new();
    private bool _sync;
    public string Field { get; }
    public GridDistinctValuesResult Results { get; private set; } = new(Array.Empty<GridDistinctValue>(), false, 0);
    public string SearchText => _search.Text;
    public string? ValidationMessage => _error.Text;
    public int ExceptionCount => _selection.ExceptionCount;
    public bool Exclude => _selection.Exclude;
    public event EventHandler? ApplyRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? ConditionRequested;
    public string ConditionText => _condition.Text;
    public ColumnFilterEditor(GridTable table, string field, ColumnValueFilter? initial = null, string condition = "")
    {
        _table = table; Field = table.Schema[field].Field;
        _selection = new(table.Schema, Field, initial);
        var panel = new StackPanel { Spacing = 8, Width = 330 };
        panel.Children.Add(new TextBlock { Text = table.Schema[Field].Caption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(_search); _actions["search"] = _search;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Make(string name, string caption, Action action)
        {
            var button = new Button { Content = caption, Padding = new Thickness(10, 4, 10, 4) };
            AutomationProperties.SetAutomationId(button, "column-filter-" + name);
            button.Click += (_, _) => Run(action); _actions[name] = button; return button;
        }
        buttons.Children.Add(Make("all", "All values", () => { _selection.SelectAll(true); Sync(); }));
        buttons.Children.Add(Make("none", "No values", () => { _selection.SelectAll(false); Sync(); }));
        panel.Children.Add(buttons); panel.Children.Add(_visible); _actions["visible"] = _visible;
        panel.Children.Add(_list); panel.Children.Add(_status); panel.Children.Add(_error);
        var finish = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        finish.Children.Add(Make("apply", "Apply", () => ApplyRequested?.Invoke(this, EventArgs.Empty)));
        finish.Children.Add(Make("cancel", "Cancel", () => CancelRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(finish);
        _condition.Text = condition; _actions["condition"] = _condition;
        panel.Children.Add(_condition); panel.Children.Add(Make("apply-condition", "Apply condition instead", () => ConditionRequested?.Invoke(this, EventArgs.Empty)));
        Content = panel;
        AutomationProperties.SetName(_search, "Search filter values"); AutomationProperties.SetName(_list, "Filter values");
        _search.TextChanged += (_, _) => { _timer.Stop(); _timer.Start(); };
        _timer.Tick += (_, _) => { _timer.Stop(); Run(Refresh); };
        Unloaded += (_, _) => _timer.Stop();
        _visible.Click += (_, _) => Run(() => { _selection.SetSelected(Results.Items.Select(item => item.Value), _visible.IsChecked == true); Sync(); });
        _list.SelectionChanged += (_, args) =>
        {
            if (_sync) return;
            Run(() =>
            {
                // Native selection events are small for a pointer toggle; bulk state changes
                // are suppressed by Sync and use the atomic core selection API.
                _selection.SetSelected(args.RemovedItems.Cast<GridDistinctValue>().Select(item => item.Value), false);
                _selection.SetSelected(args.AddedItems.Cast<GridDistinctValue>().Select(item => item.Value), true);
            });
            Sync();
        };
        Refresh();
    }
    public ColumnValueFilter BuildFilter() => _selection.Capture();
    public void ShowError(string message) => _error.Text = message;
    private void Run(Action action)
    {
        try { action(); _error.Text = ""; }
        catch (Exception error) { _error.Text = error.Message; }
    }
    private void Refresh()
    {
        Results = GridDistinctValues.Collect(_table, Field, _search.Text);
        _sync = true;
        try { _list.ItemsSource = Results.Items; }
        finally { _sync = false; }
        Sync();
    }
    private void Sync()
    {
        _sync = true;
        try
        {
            foreach (var item in _list.SelectedItems.Cast<GridDistinctValue>().ToArray())
                if (!_selection.IsSelected(item.Value)) _list.SelectedItems.Remove(item);
            var alreadySelected = _list.SelectedItems.Cast<GridDistinctValue>().ToHashSet();
            int count = 0;
            foreach (var item in Results.Items)
                if (_selection.IsSelected(item.Value)) { if (!alreadySelected.Contains(item)) _list.SelectedItems.Add(item); count++; }
            _visible.IsChecked = count == 0 ? false : count == Results.Items.Count ? true : null;
            _visible.IsEnabled = Results.Items.Count > 0;
            _status.Text = $"{Results.Items.Count:N0} values shown · {(_selection.Exclude ? "All except" : "Only")} {_selection.ExceptionCount:N0} values" +
                (Results.HasMore ? "\nMore values exist. Search to narrow the list; other choices are retained." : "");
        }
        finally { _sync = false; }
    }
    /// <summary>Actual realized element bounds for host automation; no mutation or forced realization.</summary>
    public IReadOnlyDictionary<string, RectD> ActionBounds(UIElement relativeTo)
    {
        var output = new Dictionary<string, RectD>();
        void Add(string key, FrameworkElement element)
        {
            if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            output[key] = new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
        }
        foreach (var pair in _actions) Add(pair.Key, pair.Value);
        for (int i = 0; i < Results.Items.Count; i++) if (_list.ContainerFromIndex(i) is FrameworkElement item) Add("item-" + i, item);
        return output;
    }
    public IReadOnlyList<bool> VisibleSelection() => Results.Items.Select(item => _selection.IsSelected(item.Value)).ToArray();
}

using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed record ColumnChooserRealizedRow(long Identity, string Field, bool IsChecked, string Theme, RectD Bounds);

public sealed partial class ColumnChooser
{
    private readonly HashSet<ChoicePresenter> _realized = new();
    public int RealizedRowCount => _realized.Count;
    private sealed class ChoicePresenter : Grid
    {
        private readonly ColumnChooser _owner;
        internal readonly long Identity;
        internal readonly CheckBox Check = new() { MinWidth = 0, MinHeight = 30, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _caption = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _field = new() { FontSize = 10, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis };
        internal ColumnChooserEntry? Entry;
        private ColumnChooserEntry? _subscribed;
        internal ChoicePresenter(ColumnChooser owner, long identity)
        {
            _owner = owner; Identity = identity; Height = 40; ColumnSpacing = 6;
            ColumnDefinitions.Add(new() { Width = new GridLength(32) }); ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Children.Add(Check); var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(_caption); labels.Children.Add(_field); Grid.SetColumn(labels, 1); Children.Add(labels);
            Check.Click += (_, _) => { if (!owner._disposed && Entry is { } entry) owner.SetColumnVisible(entry.Field, Check.IsChecked == true); Sync(); };
            DataContextChanged += (_, _) => Bind();
            Loaded += (_, _) => { if (!owner._disposed) owner._realized.Add(this); Bind(); };
            Unloaded += (_, _) => { owner._realized.Remove(this); Detach(); };
        }
        private void Bind()
        {
            Detach();
            // Queued realization/loading may arrive during a native dialog close.
            // A disposed owner must not acquire a new external model subscription.
            var entry = _owner._disposed ? null : DataContext as ColumnChooserEntry;
            if (!ReferenceEquals(Entry, entry)) { Entry = entry; _owner.RowBindCount++; }
            if (IsLoaded && entry is not null) { _subscribed = entry; entry.Changed += EntryChanged; }
            Sync();
        }
        private void EntryChanged(object? sender, EventArgs args) => Sync();
        internal void Detach() { if (_subscribed is not null) _subscribed.Changed -= EntryChanged; _subscribed = null; }
        private void Sync()
        {
            Check.IsChecked = Entry?.Visible == true;
            Check.IsEnabled = !_owner._disposed && Entry is not null;
            _caption.Text = Entry?.Caption ?? "";
            _field.Text = Entry is null ? "" : $"{Entry.Field} · {Entry.Band}";
            if (Entry is not null) { AutomationProperties.SetName(Check, "Show " + Entry.Caption); AutomationProperties.SetName(this, Entry.Caption); }
        }
    }
    /// <summary>Read existing realized bounds only. Does not force layout, enumerate all
    /// containers, scroll items into view or create native controls for diagnostics.</summary>
    public IReadOnlyDictionary<string, RectD> ActionBounds(UIElement relativeTo)
    {
        var result = new Dictionary<string, RectD>(StringComparer.Ordinal);
        void Add(string key, FrameworkElement element)
        {
            if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            result[key] = new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
        }
        foreach (var pair in _actions) Add(pair.Key, pair.Value);
        foreach (var row in _realized)
            if (row.Entry is { } entry) { Add("row-" + entry.Field, row); Add("check-" + entry.Field, row.Check); }
        return result;
    }
    public IReadOnlyList<ColumnChooserRealizedRow> RealizedRows(UIElement relativeTo)
    {
        var rows = new List<ColumnChooserRealizedRow>();
        foreach (var row in _realized)
        {
            if (row.Entry is not { } entry || row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
            var origin = row.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            rows.Add(new(row.Identity, entry.Field, row.Check.IsChecked == true, row.ActualTheme.ToString(), new(origin.X, origin.Y, row.ActualWidth, row.ActualHeight)));
        }
        return rows.AsReadOnly();
    }
}

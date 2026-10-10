using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private readonly Dictionary<string, CheckBox> _optionChecks = new(StringComparer.Ordinal);
    private ComboBox? _selectionOptions;
    private ComboBox? _sizeOptions;
    private bool _syncingOptions;
    private DataGridScene.Core.GridView? _observedSearchView;
    private string _observedSearch = "";
    private string _displayedScenario = "";

    private void RefreshOptions()
    {
        if (_syncingOptions || DataGrid.View is null) return;
        _syncingOptions = true;
        try
        {
            void Set(string caption, bool value)
            {
                if (_optionChecks.TryGetValue(caption, out var check) && check.IsChecked != value)
                    check.IsChecked = value;
            }
            Set("Group panel", DataGrid.ShowGroupPanel);
            Set("Auto-filter row", DataGrid.ShowFilterRow);
            Set("Column bands", DataGrid.ShowBands);
            Set("Read-only", DataGrid.IsReadOnly);
            Set("Compact rows", DataGrid.RowHeight == 28);
            Set("Dark grid", DataGrid.DarkTheme);
            if (_selectionOptions is not null && !Equals(_selectionOptions.SelectedItem, DataGrid.SelectionMode))
                _selectionOptions.SelectedItem = DataGrid.SelectionMode;
            if (_sizeOptions is not null)
            {
                _sizeOptions.PlaceholderText = $"{RecordCount:N0} records";
                int index = RecordCount switch { 1000 => 0, 10000 => 1, 100000 => 2, _ => -1 };
                if (_sizeOptions.SelectedIndex != index) _sizeOptions.SelectedIndex = index;
            }
            // Only an actual model query change (or a replacement view) may
            // replace the toolbar text. A focus/selection notification can arrive
            // before Uno dispatches the native input's TextChanged notification.
            // Testing IsEnabled/FocusState here races that notification and loses
            // freshly entered text. Unrelated grid state must never write it back.
            var view = DataGrid.View;
            if (!ReferenceEquals(_observedSearchView, view) || _observedSearch != view.Query.Search)
            {
                _observedSearchView = view;
                _observedSearch = view.Query.Search;
                _searchTimer.Stop();
                bool previous = _loading;
                _loading = true;
                try { if (_search.Text != _observedSearch) _search.Text = _observedSearch; }
                finally { _loading = previous; }
            }
            if (_displayedScenario != Scenario)
            {
                _displayedScenario = Scenario;
                foreach (var (name, element) in _actions)
                    if (name.StartsWith("scenario-", StringComparison.Ordinal) && element is Button button)
                        button.Background = name == "scenario-" + Scenario ? Brush("#e4eef7") : Brush("#f7f7f7");
            }
        }
        finally { _syncingOptions = false; }
    }

    private void FlushPendingSearch()
    {
        if (!_searchTimer.IsEnabled) return;
        _searchTimer.Stop();
        DataGrid.SetSearch(_search.Text);
    }

    // Read actual sidebar controls without updating them; diagnostics must not repair
    // stale state merely because a test inspects a snapshot.
    public IReadOnlyDictionary<string, object?> OptionValues()
    {
        var values = _optionChecks.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.IsChecked, StringComparer.Ordinal);
        values["selectionMode"] = _selectionOptions?.SelectedItem?.ToString();
        values["dataset"] = _sizeOptions?.SelectedItem?.ToString() ?? _sizeOptions?.PlaceholderText;
        values["search"] = _search.Text;
        return values;
    }
}

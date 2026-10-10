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
            // Do not overwrite a pending search while its native editor owns focus.
            if (!_searchTimer.IsEnabled && _search.FocusState == FocusState.Unfocused && _search.Text != DataGrid.View.Query.Search)
            {
                bool previous = _loading;
                _loading = true;
                try { _search.Text = DataGrid.View.Query.Search; }
                finally { _loading = previous; }
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

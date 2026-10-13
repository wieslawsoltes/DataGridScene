using DataGridScene.Core;
using Microsoft.UI.Xaml;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private GridFilterHistory? _filterHistory;
    private GridFilterState? _currentFilterState;
    private int _filterHistoryCapacity = 10;
    private readonly Dictionary<long, string> _historyDescriptions = new();
    public IReadOnlyList<GridFilterHistoryEntry> RecentFilters => _filterHistory?.Entries ?? Array.Empty<GridFilterHistoryEntry>();
    public long FilterHistoryRevision => _filterHistory?.Revision ?? 0;
    /// <summary>Why the current condition was excluded from automatic history. Custom predicates
    /// and oversized/mutable snapshots remain usable filters but cannot be safely replayed.</summary>
    public string? FilterHistoryUnavailableReason { get; private set; }
    public int FilterHistoryCapacity
    {
        get => _filterHistoryCapacity;
        set
        {
            if (value is < 0 or > GridFilterHistory.MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(value));
            _filterHistoryCapacity = value;
            if (_filterHistory is not null) _filterHistory.Capacity = value;
            if (value == 0) { _currentFilterState = null; FilterHistoryUnavailableReason = null; }
            RefreshFilterHistory(); Changed();
        }
    }
    private void ResetFilterHistory(GridTable? table)
    {
        FilterPanel.CloseHistory(); FilterPanel.IsExpanded = false;
        _filterHistory = table is null ? null : new GridFilterHistory(table.Schema, _filterHistoryCapacity);
        _currentFilterState = null; FilterHistoryUnavailableReason = null; _historyDescriptions.Clear();
        RefreshFilterHistory();
    }
    // Called only after a successful filter publication, never while painting, scrolling,
    // toggling enabled state, or processing value-only data/selection notifications.
    private void RememberFilterState()
    {
        if (_dataUpdateDepth > 0 || _filterHistory is null || Table is null) return;
        if (_filterHistoryCapacity == 0)
        {
            _currentFilterState = null; FilterHistoryUnavailableReason = null; RefreshFilterHistory(); return;
        }
        try
        {
            var state = GridFilterState.Capture(Table.Schema, _criteria, _filterTexts, _valueFilters);
            _currentFilterState = state; FilterHistoryUnavailableReason = null;
            _filterHistory.Remember(state);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException)
        {
            // Retention failure must not turn an already valid, published filter into a
            // reported query failure. The active filter is untouched; expose the boundary.
            _currentFilterState = null; FilterHistoryUnavailableReason = error.Message;
        }
        RefreshFilterHistory();
    }
    private void RefreshFilterHistory()
    {
        var ids = RecentFilters.Select(entry => entry.Id).ToHashSet();
        foreach (long id in _historyDescriptions.Keys.Where(id => !ids.Contains(id)).ToArray()) _historyDescriptions.Remove(id);
        var items = new List<FilterPanelHistoryItem>();
        foreach (var entry in RecentFilters)
        {
            if (!_historyDescriptions.TryGetValue(entry.Id, out string? description))
            {
                try { description = GridFilterText.Describe(entry.State.EffectiveFilter, entry.State.Schema, 2048); }
                catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException)
                { description = "Previously applied filter (description unavailable)"; }
                _historyDescriptions.Add(entry.Id, description);
            }
            items.Add(new(entry.Id, description, _currentFilterState?.HasSameConditions(entry.State) == true));
        }
        FilterPanel.SetHistory(items);
    }
    public void ClearFilterHistory() { _filterHistory?.Clear(); RefreshFilterHistory(); Changed(); }
    public bool RemoveRecentFilter(long id)
    {
        bool removed = _filterHistory?.Remove(id) == true;
        if (removed) { RefreshFilterHistory(); Changed(); }
        return removed;
    }
    /// <summary>Show the same native recent-filter menu from an application-owned toolbar,
    /// including when the automatic filter panel is hidden after clearing conditions.</summary>
    public void ShowRecentFilters(FrameworkElement anchor)
    {
        RequireView(); ArgumentNullException.ThrowIfNull(anchor); FilterPanel.ShowHistory(anchor);
    }
    /// <summary>Restore only filter conditions and enable them. A rejected edit/predicate
    /// leaves query, metadata, history order and selection intact. Current filters are a no-op.</summary>
    public void ApplyRecentFilter(long id)
    {
        RequireView();
        if (_dataUpdateDepth > 0) throw new InvalidOperationException("Finish data updates before restoring a recent filter.");
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        var entry = RecentFilters.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("This recent filter is no longer available.", nameof(id));
        var state = entry.State;
        if (!ReferenceEquals(state.Schema, Table!.Schema)) throw new InvalidOperationException("The filter belongs to another table schema.");
        bool same = _currentFilterState?.HasSameConditions(state) == true && View!.RequestedQuery.IsFilterEnabled &&
            GridFilterState.FiltersEqual(state.EffectiveFilter, View.RequestedQuery.Filter);
        if (!same)
        {
            // GridView stages all record-dependent work before publishing the query. Do
            // not replace our split criteria/column state or MRU order until it succeeds.
            View!.SetQuery(View.RequestedQuery with { Filter = state.EffectiveFilter, IsFilterEnabled = true });
            _criteria = state.Criteria;
            _filterTexts.Clear(); foreach (var pair in state.TextFilters) _filterTexts.Add(pair.Key, pair.Value);
            _valueFilters.Clear(); foreach (var pair in state.ValueFilters) _valueFilters.Add(pair.Key, pair.Value);
            _offsetY = 0; CloseValueFilter(); RefreshFilterDisplay(); UpdateGeometry();
        }
        else { _filterHistory!.Remember(state); RefreshFilterHistory(); }
        ClearError(); Changed();
    }
}

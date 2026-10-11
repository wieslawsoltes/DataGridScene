using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    public GridFilterPanel FilterPanel { get; } = new();
    public event EventHandler? FilterEditorRequested;
    /// <summary>Suspend filters without removing them. Search is intentionally independent.</summary>
    public bool IsFilterEnabled
    {
        get => View?.RequestedQuery.IsFilterEnabled ?? true;
        set
        {
            RequireView();
            if (!CommitEdit()) { RefreshFilterPanel(); throw new InvalidOperationException(LastError ?? "Finish editing first."); }
            if (View!.RequestedQuery.IsFilterEnabled != value) View.SetQuery(View.RequestedQuery with { IsFilterEnabled = value });
            RefreshFilterPanel(); Changed();
        }
    }
    private void InitializeFilterPanel()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(FilterPanel, 2); Grid.SetColumnSpan(FilterPanel, 2); _root.Children.Add(FilterPanel);
        FilterPanel.EnableRequested += (_, enabled) => Safe(() => IsFilterEnabled = enabled);
        FilterPanel.RemoveRequested += (_, key) => Safe(() => RemoveFilterToken(key));
        FilterPanel.ClearRequested += (_, _) => Safe(ClearFilterConditions);
        FilterPanel.EditRequested += (_, _) => FilterEditorRequested?.Invoke(this, EventArgs.Empty);
        FilterPanel.TokenEditRequested += (_, key) =>
        {
            if (key == "criteria") { FilterEditorRequested?.Invoke(this, EventArgs.Empty); return; }
            Safe(() => { var slot = Frame?.Columns.Slots.FirstOrDefault(slot => slot.Field == key[7..]);
                ShowColumnFilter(key[7..], new Point(slot?.ClipLeft ?? 0, Frame?.FilterTop ?? 0)); });
        };
    }
    /// <summary>Clear only filter conditions. Unlike ClearFilters, this preserves search.</summary>
    public void ClearFilterConditions()
    {
        RequireView(); if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        View!.SetQuery(View.RequestedQuery with { Filter = null, IsFilterEnabled = true });
        _criteria = null; _filterTexts.Clear(); _valueFilters.Clear(); RefreshFilterDisplay(); ClearError(); Changed();
    }
    public void RemoveFilterToken(string key)
    {
        RequireView();
        if (key == "criteria")
        {
            if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
            ApplyFilter(null, _filterTexts); _criteria = null; RefreshFilterDisplay(); Changed();
        }
        else if (key.StartsWith("column:", StringComparison.Ordinal)) SetColumnValues(key[7..], null);
        else throw new ArgumentException("Unknown filter token.", nameof(key));
    }
    // Called after filter mutations (not during paint, hover, selection or value-only updates).
    private void RefreshFilterPanel()
    {
        if (_dataUpdateDepth > 0) return;
        if (Table is null || View is null) { FilterPanel.SetState([], true); return; }
        var tokens = new List<FilterPanelToken>();
        if (_criteria is not null) tokens.Add(new("criteria", "Criteria", GridFilterText.Describe(_criteria, Table.Schema)));
        foreach (var column in Table.Schema.Columns)
        {
            if (_filterTexts.TryGetValue(column.Field, out var text))
                tokens.Add(new("column:" + column.Field, column.Caption, text));
            else if (_valueFilters.TryGetValue(column.Field, out var values))
            {
                string description = values.Values.Count == 0 ? (values.Exclude ? "All values" : "No values")
                    : GridFilterText.Describe(new FieldFilter(column.Field, FilterOperator.In, Values: values.Values), Table.Schema);
                if (description.StartsWith(column.Caption + " ", StringComparison.Ordinal)) description = description[(column.Caption.Length + 1)..];
                if (values.Exclude && values.Values.Count > 0) description = "not " + description;
                tokens.Add(new("column:" + column.Field, column.Caption, description));
            }
        }
        FilterPanel.SetState(tokens, IsFilterEnabled);
    }
}

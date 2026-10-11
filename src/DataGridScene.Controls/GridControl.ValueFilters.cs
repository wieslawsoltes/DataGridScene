using DataGridScene.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private readonly Dictionary<string, ColumnValueFilter> _valueFilters = new(StringComparer.OrdinalIgnoreCase);
    private Flyout? _valueFlyout;
    public ColumnFilterEditor? ActiveColumnFilter { get; private set; }
    public IReadOnlyDictionary<string, ColumnValueFilter> ValueFilters => new System.Collections.ObjectModel.ReadOnlyDictionary<string, ColumnValueFilter>(_valueFilters);
    /// <summary>Replace this column's auto-filter with a typed value selection, retaining base
    /// criteria, other columns, search and summary configuration. Null clears this column.</summary>
    public void SetColumnValues(string field, ColumnValueFilter? filter)
    {
        RequireView(); if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        field = Table!.Schema[field].Field;
        var owned = filter?.Capture(Table.Schema, field);
        var values = new Dictionary<string, ColumnValueFilter>(_valueFilters, StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(_filterTexts, StringComparer.OrdinalIgnoreCase);
        texts.Remove(field);
        if (owned is null || owned is { Exclude: true, Values.Count: 0 }) values.Remove(field); else values[field] = owned;
        ApplyFilter(_criteria, texts, values);
        _valueFilters.Clear(); foreach (var pair in values) _valueFilters.Add(pair.Key, pair.Value);
        _filterTexts.Clear(); foreach (var pair in texts) _filterTexts.Add(pair.Key, pair.Value);
        RefreshFilterDisplay(); ClearError(); Redraw(); Changed();
    }
    private void RefreshFilterDisplay()
    {
        var display = new Dictionary<string, string>(_filterTexts, StringComparer.OrdinalIgnoreCase);
        foreach (var (field, values) in _valueFilters) display[field] = values.Exclude ? $"Except {values.Values.Count:N0} values" : $"{values.Values.Count:N0} values selected";
        Renderer.FilterTexts = display;
    }
    private void CloseValueFilter() { _valueFlyout?.Hide(); _valueFlyout = null; ActiveColumnFilter = null; }
    private void ShowColumnFilter(string field, Point point)
    {
        RequireView(); if (!CommitEdit()) return;
        CloseValueFilter();
        var editor = new ColumnFilterEditor(Table!, field, _valueFilters.GetValueOrDefault(field), _filterTexts.GetValueOrDefault(field) ?? "");
        var flyout = new Flyout { Content = editor };
        ActiveColumnFilter = editor; _valueFlyout = flyout;
        editor.ApplyRequested += (_, _) => { SetColumnValues(field, editor.BuildFilter()); flyout.Hide(); };
        editor.ConditionRequested += (_, _) => { SetColumnFilter(field, editor.ConditionText); flyout.Hide(); };
        editor.CancelRequested += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) => { if (ReferenceEquals(_valueFlyout, flyout)) { _valueFlyout = null; ActiveColumnFilter = null; } };
        flyout.ShowAt(_surface, new FlyoutShowOptions { Position = point });
    }
}

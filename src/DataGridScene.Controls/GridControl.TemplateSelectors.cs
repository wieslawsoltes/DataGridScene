using DataGridScene.Core;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private readonly Dictionary<string, Func<GridCellTemplateContext, GridCellTemplate?>> _displaySelectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<GridCellTemplateContext, GridCellTemplate?>> _editSelectors = new(StringComparer.OrdinalIgnoreCase);
    private sealed record TemplateChoice(GridRecord Record, int Version, bool ReadOnly, GridCellTemplate? Template);
    private readonly Dictionary<(CellAddress Address, bool Editing), TemplateChoice> _templateChoices = [];
    private long _templateTableRevision = -1;
    private bool _templateReadOnly;
    public long TemplateSelectorCallCount { get; private set; }
    public int CachedTemplateChoiceCount => _templateChoices.Count;

    /// <summary>Select per-row display content in one column. Null results use the column's
    /// fixed template or canvas fallback. Results are cached only for visible record versions.</summary>
    public void SetCellTemplateSelector(string field, Func<GridCellTemplateContext, GridCellTemplate?>? selector) =>
        SetTemplateSelector(field, selector, editing: false);
    /// <summary>Select an editor at activation; a null result falls back to the fixed editor,
    /// then the selected display content, then the standard grid editor.</summary>
    public void SetCellEditTemplateSelector(string field, Func<GridCellTemplateContext, GridCellTemplate?>? selector) =>
        SetTemplateSelector(field, selector, editing: true);
    private void SetTemplateSelector(string field, Func<GridCellTemplateContext, GridCellTemplate?>? selector, bool editing)
    {
        RequireView(); field = Table!.Schema[field].Field;
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        var selectors = editing ? _editSelectors : _displaySelectors;
        if (selector is null) selectors.Remove(field); else selectors[field] = selector;
        if (_cellTemplates.ContainsKey(field) || _displaySelectors.ContainsKey(field)) _cellFields.Add(field); else _cellFields.Remove(field);
        InvalidateCellTemplates();
    }
    /// <summary>Re-evaluate viewport choices when external selector configuration changes.
    /// Record edits invalidate the relevant choices automatically. Dirty editors are retained.</summary>
    public void InvalidateCellTemplates()
    {
        RequireView(); _templateChoices.Clear(); _nativeFrame = null;
        TrimUnusedPools(); SynchronizeCellControls(); Redraw();
    }
    private GridCellTemplate? ResolveCellTemplate(CellAddress address, bool editing)
    {
        var record = Table![address.RecordId];
        var column = Table.Schema[address.Field];
        var selectors = editing ? _editSelectors : _displaySelectors;
        var fixedTemplates = editing ? _editCellTemplates : _cellTemplates;
        if (!selectors.TryGetValue(address.Field, out var selector)) return fixedTemplates.GetValueOrDefault(address.Field) ?? DefaultNativeTemplate(column, editing);
        var key = (address, editing); bool readOnly = IsReadOnly || column.IsReadOnly;
        if (_templateChoices.TryGetValue(key, out var choice) && ReferenceEquals(choice.Record, record) && choice.Version == record.Version && choice.ReadOnly == readOnly)
            return choice.Template;
        TemplateSelectorCallCount++;
        var result = selector(new(record, column, editing, readOnly)) ?? fixedTemplates.GetValueOrDefault(address.Field) ?? DefaultNativeTemplate(column, editing);
        _templateChoices[key] = new(record, record.Version, readOnly, result);
        return result;
    }
    private void PruneTemplateChoices(HashSet<CellAddress> visible)
    {
        foreach (var key in _templateChoices.Keys.ToArray())
            if (!visible.Contains(key.Address) || key.Editing && key.Address != _nativeEditCell) _templateChoices.Remove(key);
    }
}

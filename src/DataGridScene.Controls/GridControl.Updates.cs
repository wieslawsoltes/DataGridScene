using DataGridScene.Core;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private int _dataUpdateDepth;
    private bool _rowGeometryPending, _geometryPending, _statePending, _redrawPending;
    private bool _filterDisplayPending, _filterPanelPending;
    private GridFilter? _batchCriteria;
    private Dictionary<string, string>? _batchFilters;
    private Dictionary<string, ColumnValueFilter>? _batchValueFilters;
    /// <summary>Coalesce query/data shaping and control notifications. Dispose on the UI thread.
    /// This does not roll back table writes or preserve cell-edit undo for external updates.</summary>
    public IDisposable BeginDataUpdate()
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first." );
        if (_dataUpdateDepth == 0)
        {
            _batchCriteria = _criteria;
            _batchFilters = new(_filterTexts, StringComparer.OrdinalIgnoreCase);
            _batchValueFilters = new(_valueFilters, StringComparer.OrdinalIgnoreCase);
            _filterDisplayPending = _filterPanelPending = false;
        }
        var refresh = View!.DeferRefresh();
        _dataUpdateDepth++;
        return new DataUpdateScope(this, refresh);
    }
    private sealed class DataUpdateScope(GridControl owner, IDisposable refresh) : IDisposable
    {
        private GridControl? _owner = owner;
        public void Dispose()
        {
            var control = Interlocked.Exchange(ref _owner, null);
            if (control is null) return;
            bool succeeded = false;
            try { refresh.Dispose(); succeeded = true; }
            catch
            {
                // Shaping rejected the staged query. Keep the filter UI paired with
                // the still-published Query; committed table edits are not rolled back.
                control._criteria = control._batchCriteria;
                if (control._batchFilters is not null)
                {
                    control._filterTexts.Clear();
                    foreach (var pair in control._batchFilters) control._filterTexts.Add(pair.Key, pair.Value);
                    control._valueFilters.Clear();
                    if (control._batchValueFilters is not null)
                        foreach (var pair in control._batchValueFilters) control._valueFilters.Add(pair.Key, pair.Value);
                    control.RefreshFilterDisplay();
                }
                throw;
            }
            finally
            {
                if (--control._dataUpdateDepth == 0 && !control._disposed)
                {
                    bool rows = control._rowGeometryPending, geometry = control._geometryPending,
                        state = control._statePending, redraw = control._redrawPending,
                        filters = control._filterDisplayPending, panel = control._filterPanelPending;
                    control._rowGeometryPending = control._geometryPending = control._statePending = control._redrawPending = false;
                    control._filterDisplayPending = control._filterPanelPending = false;
                    control._batchFilters = null; control._batchValueFilters = null; control._batchCriteria = null;
                    // A data/selection/search-only batch is not a newly applied filter.
                    // In particular, it must not resurrect explicitly cleared MRU entries.
                    // Coalesce filter display allocation/formatting too, not just shaping.
                    if (filters) control.RefreshFilterDisplay(remember: succeeded);
                    else if (panel) control.RefreshFilterPanel();
                    if (rows) control.RebuildRows();
                    else
                    {
                        if (geometry) control.UpdateGeometry();
                        else if (redraw) control.Redraw();
                        if (state) control.Changed();
                    }
                }
            }
        }
    }
}

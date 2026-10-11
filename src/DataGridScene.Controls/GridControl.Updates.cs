using DataGridScene.Core;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private int _dataUpdateDepth;
    private bool _geometryPending, _statePending, _redrawPending;
    private GridFilter? _batchCriteria;
    private Dictionary<string, string>? _batchFilters;
    /// <summary>Coalesce query/data shaping and control notifications. Dispose on the UI thread.
    /// This does not roll back table writes or preserve cell-edit undo for external updates.</summary>
    public IDisposable BeginDataUpdate()
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        if (_dataUpdateDepth == 0)
        {
            _batchCriteria = _criteria;
            _batchFilters = new(_filterTexts, StringComparer.OrdinalIgnoreCase);
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
            try { refresh.Dispose(); }
            catch
            {
                // Shaping rejected the staged query. Keep the filter UI paired with
                // the still-published Query; committed table edits are not rolled back.
                control._criteria = control._batchCriteria;
                if (control._batchFilters is not null)
                {
                    control._filterTexts.Clear();
                    foreach (var pair in control._batchFilters) control._filterTexts.Add(pair.Key, pair.Value);
                    control.Renderer.FilterTexts = control._filterTexts;
                }
                throw;
            }
            finally
            {
                if (--control._dataUpdateDepth == 0 && !control._disposed)
                {
                    bool geometry = control._geometryPending, state = control._statePending, redraw = control._redrawPending;
                    control._geometryPending = control._statePending = control._redrawPending = false;
                    control._batchFilters = null; control._batchCriteria = null;
                    if (geometry) control.RebuildRows();
                    else { if (redraw) control.Redraw(); if (state) control.Changed(); }
                }
            }
        }
    }
}

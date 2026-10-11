using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private bool RefocusEditor(GridHit hit)
    {
        if (_editor is null || !string.Equals(_editField, hit.Field, StringComparison.OrdinalIgnoreCase)) return false;
        bool same = _filterEditor ? hit.Region == HitRegion.Filter
            : hit.Region == HitRegion.DataCell && View!.Rows[hit.RowIndex].Record!.Id == _editRecordId;
        if (!same) return false;
        _editor.Focus(FocusState.Programmatic);
        return true;
    }
}

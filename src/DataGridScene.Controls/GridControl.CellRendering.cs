using DataGridScene.Core;

namespace DataGridScene.Controls;

public enum GridCellRenderingMode { Skia, NativeControls }

public sealed partial class GridControl
{
    private GridCellRenderingMode _cellRenderingMode;
    private GridSchema? _defaultTemplateSchema;
    private readonly Dictionary<string, GridCellTemplate> _nativeDisplays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridCellTemplate> _nativeEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allNativeFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Use ordinary Uno cell presenters and in-place controls for any schema.
    /// Explicit templates/selectors take precedence in both modes. This is cell content
    /// selection, not a change to Uno's underlying Skia platform renderer.</summary>
    public GridCellRenderingMode CellRenderingMode
    {
        get => _cellRenderingMode;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_cellRenderingMode == value) return;
            if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
            _cellRenderingMode = value; _templateChoices.Clear(); _nativeFrame = null;
            if (value == GridCellRenderingMode.Skia && _cellFields.Count == 0) ResetCellControls();
            if (Table is not null) { SynchronizeCellControls(); Redraw(); }
            Changed();
        }
    }
    private HashSet<string> NativeFields()
    {
        if (CellRenderingMode != GridCellRenderingMode.NativeControls) return _cellFields;
        EnsureDefaultTemplates(); return _allNativeFields;
    }
    private void EnsureDefaultTemplates()
    {
        var schema = Table!.Schema;
        if (ReferenceEquals(schema, _defaultTemplateSchema)) return;
        _nativeDisplays.Clear(); _nativeEditors.Clear(); _allNativeFields.Clear(); _defaultTemplateSchema = schema;
        foreach (var column in schema.Columns) _allNativeFields.Add(column.Field);
    }
    private GridCellTemplate? DefaultNativeTemplate(GridColumn column, bool editing)
    {
        if (CellRenderingMode != GridCellRenderingMode.NativeControls) return null;
        EnsureDefaultTemplates();
        if (editing && (column.IsReadOnly || IsReadOnly)) return null;
        var cache = editing ? _nativeEditors : _nativeDisplays;
        if (cache.TryGetValue(column.Field, out var template)) return template;
        if (editing)
        {
            // Text preserves exact decimals/integers and timestamp precision. Explicit
            // application templates may opt into NumberBox or a date-only picker.
            template = column.DataType == typeof(bool) ? GridCellTemplates.CheckBox()
                : column.Choices.Count > 0 ? GridCellTemplates.ComboBox(column.Choices) : GridCellTemplates.TextBox();
        }
        else template = column.Presentation == ColumnPresentation.CheckBox ? GridCellTemplates.CheckBox()
            : NativeCellPresenters.Create(column.Presentation, Renderer);
        cache.Add(column.Field, template); return template;
    }
}

using DataGridScene.Layout;

namespace DataGridScene.Controls;

public enum RowEditCaptionPlacement { Above, Beside }

/// <summary>Copied and validated when a form is created. Layout metadata is not a
/// permission boundary: the row buffer still validates complete model transactions.</summary>
public sealed record RowEditFormOptions
{
    public string Title { get; init; } = "Edit row";
    public int MaximumColumnCount { get; init; } = 1;
    public double MinimumColumnWidth { get; init; } = 220;
    public double ColumnSpacing { get; init; } = 16;
    public double MaximumWidth { get; init; } = 520;
    public double MaximumFieldHeight { get; init; } = 400;
    public RowEditCaptionPlacement CaptionPlacement { get; init; } = RowEditCaptionPlacement.Above;
    public double CaptionWidth { get; init; } = 104;
    public bool ShowInstructions { get; init; } = true;
    public IReadOnlyList<RowEditFieldLayout> Fields { get; init; } = Array.Empty<RowEditFieldLayout>();
    /// <summary>Per-field factories, called once per visible field. Null results use
    /// the default editor. Unknown/duplicate names and null delegates are rejected.</summary>
    public IReadOnlyDictionary<string, Func<RowEditEditorContext, RowEditEditor?>> EditorFactories { get; init; }
        = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>>();

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentNullException.ThrowIfNull(Fields); ArgumentNullException.ThrowIfNull(EditorFactories);
        if (!Enum.IsDefined(CaptionPlacement)) throw new ArgumentOutOfRangeException(nameof(CaptionPlacement));
        if (!double.IsFinite(MaximumWidth) || MaximumWidth is < 240 or > 2048) throw new ArgumentOutOfRangeException(nameof(MaximumWidth));
        if (!double.IsFinite(MaximumFieldHeight) || MaximumFieldHeight is < 80 or > 1200) throw new ArgumentOutOfRangeException(nameof(MaximumFieldHeight));
        if (!double.IsFinite(CaptionWidth) || CaptionWidth is < 32 or > 256) throw new ArgumentOutOfRangeException(nameof(CaptionWidth));
    }
}

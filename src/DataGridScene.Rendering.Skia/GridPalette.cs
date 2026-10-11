using SkiaSharp;
using DataGridScene.Core;

namespace DataGridScene.Rendering.Skia;

public sealed record GridPalette(SKColor Background, SKColor Foreground, SKColor Muted, SKColor Header, SKColor Line,
    SKColor Alternate, SKColor Group, SKColor Selection, SKColor Accent, SKColor Filter, SKColor Footer)
{
    public static GridPalette Light { get; } = new(SKColor.Parse("#ffffff"), SKColor.Parse("#242424"), SKColor.Parse("#737373"), SKColor.Parse("#f1f1f1"), SKColor.Parse("#d2d2d2"), SKColor.Parse("#ffffff"), SKColor.Parse("#edf3f8"), SKColor.Parse("#cce4f7"), SKColor.Parse("#1673b8"), SKColor.Parse("#ffffff"), SKColor.Parse("#f5f5f5"));
    public static GridPalette Dark { get; } = new(SKColor.Parse("#202020"), SKColor.Parse("#eeeeee"), SKColor.Parse("#b3b3b3"), SKColor.Parse("#303030"), SKColor.Parse("#484848"), SKColor.Parse("#272727"), SKColor.Parse("#2a3440"), SKColor.Parse("#284864"), SKColor.Parse("#66b4ee"), SKColor.Parse("#242424"), SKColor.Parse("#2e2e2e"));
}
public sealed record GridCellStyle(SKColor? Background = null, SKColor? Foreground = null, bool Bold = false);
public sealed record ConditionalFormatRule(Func<GridRecord, GridColumn, bool> Matches, GridCellStyle Style);
public sealed record GridDetailData(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

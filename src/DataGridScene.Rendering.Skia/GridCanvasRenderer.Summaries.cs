using DataGridScene.Core;
using SkiaSharp;

namespace DataGridScene.Rendering.Skia;

public sealed partial class GridCanvasRenderer
{
    public int SummaryCellsDrawn { get; private set; }
    private void DrawSummaryCell(SKCanvas canvas, GridFrame frame, SummaryCell cell)
    {
        var box = cell.Bounds;
        double top = cell.RowIndex < 0 ? frame.FooterTop : frame.BodyTop;
        double bottom = cell.RowIndex < 0 ? frame.Height : frame.FooterTop;
        if (box.Bottom <= top || box.Y >= bottom) return;
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect((float)box.X, (float)Math.Max(top, box.Y), (float)box.Right, (float)Math.Min(bottom, box.Bottom)));
            Text(canvas, cell.Text, box.X + 8, box.Y + box.Height / 2, box.Width - 16,
                bold: true, alignment: frame.View.Table.Schema[cell.Field].Alignment);
            SummaryCellsDrawn++;
        }
        finally { canvas.Restore(); }
    }
    private void DrawGroupSummaryRow(SKCanvas canvas, GridFrame frame, int index, ViewRow row, RectD bounds)
    {
        bool footer = row.Kind == GridRowKind.GroupFooter;
        Fill(canvas, 0, bounds.Y, frame.Width, bounds.Height, footer ? Palette.Footer : Palette.Group);
        var group = row.Group!;
        string value = group.Value is null ? "(Blanks)" : frame.View.Table.Schema[group.Field].Display(group.Value, frame.View.Table.Schema.Culture);
        string label = $"{(footer ? "Total · " : "")}{frame.View.Table.Schema[group.Field].Caption}: {value}  ({group.Records.Count:N0} records)";
        if (!footer && frame.View.Query.GroupSummaryDisplay == GroupSummaryDisplayMode.Inline)
        {
            var texts = frame.View.Query.EffectiveGroupSummaries.Where(item => item.ShowInGroupRow).Select(item => item.Display(group.Summaries[item.Key], frame.View.Table.Schema));
            string summary = string.Join("; ", texts);
            if (summary.Length > 0) label += "   [" + summary + "]";
        }
        if (!footer) Chevron(canvas, 18 + row.Depth * 16, bounds.Y + SummaryGeometry.GroupLabelHeight / 2, frame.View.IsExpanded(row.Key), Palette.Foreground);
        Text(canvas, label, 34 + row.Depth * 16, bounds.Y + SummaryGeometry.GroupLabelHeight / 2, frame.Width - 60 - row.Depth * 16, bold: true);
        foreach (var cell in SummaryGeometry.GroupCells(frame, index)) DrawSummaryCell(canvas, frame, cell);
        Line(canvas, 0, bounds.Bottom - 0.5, frame.Width, bounds.Bottom - 0.5, Palette.Line);
    }
    private void DrawFooter(SKCanvas canvas, GridFrame frame)
    {
        Fill(canvas, 0, frame.FooterTop, frame.Width, frame.Height - frame.FooterTop, Palette.Footer);
        Line(canvas, 0, frame.FooterTop + 0.5, frame.Width, frame.FooterTop + 0.5, Palette.Line);
        foreach (var cell in SummaryGeometry.TotalCells(frame)) DrawSummaryCell(canvas, frame, cell);
        foreach (var slot in frame.Columns.Slots)
            Line(canvas, slot.ClipRight - 0.5, frame.FooterTop, slot.ClipRight - 0.5, frame.Height, Palette.Line);
    }
}

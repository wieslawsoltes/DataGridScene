using DataGridScene.Core;
using DataGridScene.Layout;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;

namespace DataGridScene.Rendering.Skia;

/// <summary>Paints only visible rows and columns into a caller-owned Skia canvas; no bitmap copy or UI dependency.</summary>
public sealed class GridCanvasRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly ShapedTextCache _text = new();
    public GridPalette Palette { get; set; } = GridPalette.Light;
    public IList<ConditionalFormatRule> ConditionalFormats { get; } = new List<ConditionalFormatRule>();
    public Func<GridRecord, GridDetailData>? DetailProvider { get; set; }
    public IReadOnlyDictionary<string, string> FilterTexts { get; set; } = new Dictionary<string, string>();
    public double? DragMarkerX { get; set; }
    public int CellsDrawn { get; private set; }
    public int RowsDrawn { get; private set; }
    public long DrawCount { get; private set; }
    public double LastDrawMilliseconds { get; private set; }
    public int CachedTextRuns => _text.Count;
    public string FontFamily => _text.FontFamily;
    public float MeasureText(string text, bool bold = false) => _text.Measure(text, bold);
    private void Fill(SKCanvas canvas, double x, double y, double width, double height, SKColor color)
    {
        if (width <= 0 || height <= 0) return;
        _fill.Color = color; canvas.DrawRect((float)x, (float)y, (float)width, (float)height, _fill);
    }
    private void Line(SKCanvas canvas, double x1, double y1, double x2, double y2, SKColor color, float thickness = 1)
    {
        _stroke.Color = color; _stroke.StrokeWidth = thickness;
        canvas.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, _stroke);
    }
    private void Text(SKCanvas canvas, string text, double x, double yCenter, double width, SKColor? color = null, bool bold = false, ColumnAlignment alignment = ColumnAlignment.Left)
    {
        _textPaint.Color = color ?? Palette.Foreground;
        _text.Draw(canvas, text, (float)x, (float)yCenter, (float)Math.Max(0, width), _textPaint, bold,
            alignment == ColumnAlignment.Right ? SKTextAlign.Right : alignment == ColumnAlignment.Center ? SKTextAlign.Center : SKTextAlign.Left);
    }
    private void Chevron(SKCanvas canvas, double x, double y, bool expanded, SKColor color)
    {
        using var path = new SKPath();
        if (expanded) { path.MoveTo((float)x - 3, (float)y - 2); path.LineTo((float)x + 3, (float)y - 2); path.LineTo((float)x, (float)y + 2); }
        else { path.MoveTo((float)x - 2, (float)y - 3); path.LineTo((float)x + 2, (float)y); path.LineTo((float)x - 2, (float)y + 3); }
        path.Close(); _fill.Color = color; canvas.DrawPath(path, _fill);
    }
    private void CheckBox(SKCanvas canvas, double x, double y, bool selected, bool mixed = false)
    {
        _fill.Color = selected || mixed ? Palette.Accent : Palette.Background; canvas.DrawRoundRect(new SKRect((float)x - 6, (float)y - 6, (float)x + 6, (float)y + 6), 1, 1, _fill);
        _stroke.Color = selected || mixed ? Palette.Accent : Palette.Muted; _stroke.StrokeWidth = 1;
        canvas.DrawRoundRect(new SKRect((float)x - 6, (float)y - 6, (float)x + 6, (float)y + 6), 1, 1, _stroke);
        if (mixed) Line(canvas, x - 3, y, x + 3, y, SKColors.White, 1.5f);
        else if (selected) { Line(canvas, x - 3, y, x - 1, y + 2, SKColors.White, 1.5f); Line(canvas, x - 1, y + 2, x + 4, y - 3, SKColors.White, 1.5f); }
    }
    public void Draw(SKCanvas canvas, GridFrame frame)
    {
        var watch = Stopwatch.StartNew(); CellsDrawn = RowsDrawn = 0; DrawCount++;
        canvas.Save(); canvas.ClipRect(new SKRect(0, 0, (float)frame.Width, (float)frame.Height));
        Fill(canvas, 0, 0, frame.Width, frame.Height, Palette.Background);
        DrawBody(canvas, frame); DrawHeaders(canvas, frame); DrawFooter(canvas, frame);
        if (DragMarkerX is double marker) Line(canvas, marker, frame.HeaderTop, marker, frame.FooterTop, Palette.Accent, 2);
        canvas.Restore(); LastDrawMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
    private void DrawHeaders(SKCanvas canvas, GridFrame frame)
    {
        if (frame.Metrics.ShowGroupPanel)
        {
            Fill(canvas, 0, 0, frame.Width, frame.BandTop, Palette.Header);
            if (frame.GroupChips.Count == 0) Text(canvas, "Drag a column header here to group by that column", 14, frame.BandTop / 2, frame.Width - 28, Palette.Muted);
            for (int i = 0; i < frame.GroupChips.Count; i++)
            {
                var chip = frame.GroupChips[i]; var box = chip.Bounds;
                Fill(canvas, box.X, box.Y, box.Width, box.Height, Palette.Background);
                _stroke.Color = Palette.Line; _stroke.StrokeWidth = 1; canvas.DrawRect(new SKRect((float)box.X + 0.5f, (float)box.Y + 0.5f, (float)box.Right - 0.5f, (float)box.Bottom - 0.5f), _stroke);
                Text(canvas, chip.Caption, box.X + 10, box.Y + box.Height / 2, box.Width - 45);
                Chevron(canvas, box.Right - 31, box.Y + box.Height / 2, frame.View.Query.Groups[i].Direction == SortDirection.Descending, Palette.Muted);
                Line(canvas, box.Right - 16, box.Y + 10, box.Right - 10, box.Y + 16, Palette.Muted);
                Line(canvas, box.Right - 16, box.Y + 16, box.Right - 10, box.Y + 10, Palette.Muted);
                if (i + 1 < frame.GroupChips.Count) Text(canvas, ">", box.Right + 4, box.Y + box.Height / 2, 12, Palette.Muted);
            }
            Line(canvas, 0, frame.BandTop - 0.5, frame.Width, frame.BandTop - 0.5, Palette.Line);
        }
        Fill(canvas, 0, frame.BandTop, frame.Width, frame.BodyTop - frame.BandTop, Palette.Header);
        if (frame.Metrics.ShowFilterRow) Fill(canvas, 0, frame.FilterTop, frame.Width, frame.Metrics.FilterHeight, Palette.Filter);
        foreach (var slot in frame.Columns.Slots)
        {
            var column = frame.View.Table.Schema.Columns[slot.SchemaIndex];
            canvas.Save(); canvas.ClipRect(new SKRect((float)slot.ClipLeft, (float)frame.BandTop, (float)slot.ClipRight, (float)frame.BodyTop));
            if (frame.Metrics.ShowBands)
            {
                Text(canvas, column.Band ?? "General", slot.X + 8, frame.BandTop + frame.Metrics.BandHeight / 2, slot.Width - 16, Palette.Muted, true, ColumnAlignment.Center);
                Line(canvas, slot.X, frame.HeaderTop - 0.5, slot.X + slot.Width, frame.HeaderTop - 0.5, Palette.Line);
            }
            Text(canvas, column.Caption, slot.X + 9, frame.HeaderTop + frame.Metrics.HeaderHeight / 2, slot.Width - 42, bold: true);
            var sort = frame.View.Query.Sorts.Select((value, index) => (value, index)).FirstOrDefault(item => item.value.Field.Equals(column.Field, StringComparison.OrdinalIgnoreCase));
            if (sort.value is not null)
            {
                Chevron(canvas, slot.X + slot.Width - 29, frame.HeaderTop + 15, sort.value.Direction == SortDirection.Descending, Palette.Accent);
                if (frame.View.Query.Sorts.Count > 1) Text(canvas, (sort.index + 1).ToString(), slot.X + slot.Width - 23, frame.HeaderTop + 21, 10, Palette.Accent);
            }
            // Funnel glyph is vector geometry, not a copied icon asset.
            double fx = slot.X + slot.Width - 12, fy = frame.HeaderTop + frame.Metrics.HeaderHeight / 2;
            using (var funnel = new SKPath())
            {
                funnel.MoveTo((float)fx - 4, (float)fy - 4); funnel.LineTo((float)fx + 4, (float)fy - 4); funnel.LineTo((float)fx + 1, (float)fy);
                funnel.LineTo((float)fx + 1, (float)fy + 4); funnel.LineTo((float)fx - 1, (float)fy + 5); funnel.LineTo((float)fx - 1, (float)fy); funnel.Close();
                _stroke.Color = FilterTexts.ContainsKey(column.Field) ? Palette.Accent : Palette.Muted; _stroke.StrokeWidth = 1; canvas.DrawPath(funnel, _stroke);
            }
            if (frame.Metrics.ShowFilterRow)
            {
                bool hasFilter = FilterTexts.TryGetValue(column.Field, out string? filter);
                Text(canvas, hasFilter ? filter! : "Filter…", slot.X + 9, frame.FilterTop + frame.Metrics.FilterHeight / 2, slot.Width - 18, hasFilter ? Palette.Accent : Palette.Muted);
            }
            Line(canvas, slot.X + slot.Width - 0.5, frame.BandTop, slot.X + slot.Width - 0.5, frame.BodyTop, Palette.Line);
            canvas.Restore();
        }
        CheckBox(canvas, 19, frame.HeaderTop + frame.Metrics.HeaderHeight / 2, frame.Selection.Count > 0 && frame.Selection.Count == frame.View.FilteredRecords.Count, frame.Selection.Count > 0 && frame.Selection.Count != frame.View.FilteredRecords.Count);
        if (frame.Metrics.ShowFilterRow) Text(canvas, "⌕", 12, frame.FilterTop + frame.Metrics.FilterHeight / 2, 20, Palette.Muted);
        Line(canvas, 0, frame.FilterTop - 0.5, frame.Width, frame.FilterTop - 0.5, Palette.Line);
        Line(canvas, 0, frame.BodyTop - 0.5, frame.Width, frame.BodyTop - 0.5, Palette.Line);
    }
    private void DrawBody(SKCanvas canvas, GridFrame frame)
    {
        canvas.Save(); canvas.ClipRect(new SKRect(0, (float)frame.BodyTop, (float)frame.Width, (float)frame.FooterTop));
        for (int index = frame.VisibleRows.Start; index < frame.VisibleRows.EndExclusive; index++)
        {
            var row = frame.View.Rows[index]; var bounds = frame.RowBounds(index); RowsDrawn++;
            if (row.Kind == GridRowKind.Group)
            {
                Fill(canvas, 0, bounds.Y, frame.Width, bounds.Height, Palette.Group);
                Chevron(canvas, 18 + row.Depth * 16, bounds.Y + bounds.Height / 2, frame.View.IsExpanded(row.Key), Palette.Foreground);
                var group = row.Group!; string value = group.Value is null ? "(Blanks)" : frame.View.Table.Schema[group.Field].Display(group.Value, frame.View.Table.Schema.Culture);
                Text(canvas, $"{frame.View.Table.Schema[group.Field].Caption}: {value}  ({group.Records.Count:N0} records)", 34 + row.Depth * 16, bounds.Y + bounds.Height / 2, frame.Width - 60 - row.Depth * 16, bold: true);
                Line(canvas, 0, bounds.Bottom - 0.5, frame.Width, bounds.Bottom - 0.5, Palette.Line); continue;
            }
            if (row.Kind == GridRowKind.Detail) { DrawDetail(canvas, row.Record!, bounds); continue; }
            var record = row.Record!;
            bool selected = frame.Selection.Contains(record.Id);
            SKColor background = selected ? Palette.Selection : index % 2 == 0 ? Palette.Background : Palette.Alternate;
            Fill(canvas, 0, bounds.Y, frame.Width, bounds.Height, background);
            foreach (var slot in frame.Columns.Slots)
            {
                var column = frame.View.Table.Schema.Columns[slot.SchemaIndex]; object? value = column.GetValue(record);
                var rectangle = new SKRect((float)slot.X, (float)bounds.Y, (float)(slot.X + slot.Width), (float)bounds.Bottom);
                canvas.Save(); canvas.ClipRect(new SKRect((float)slot.ClipLeft, (float)bounds.Y, (float)slot.ClipRight, (float)bounds.Bottom));
                GridCellStyle? style = null;
                foreach (var rule in ConditionalFormats) if (rule.Matches(record, column)) style = rule.Style;
                if (style?.Background is SKColor fill && !selected) Fill(canvas, slot.X, bounds.Y, slot.Width, bounds.Height, fill);
                DrawCell(canvas, column, value, rectangle, frame.View.Table.Schema.Culture, style);
                if (frame.Selection.FocusedCell is { } focus && focus.RecordId == record.Id && focus.Field.Equals(column.Field, StringComparison.OrdinalIgnoreCase))
                {
                    _stroke.Color = Palette.Accent; _stroke.StrokeWidth = 1.5f; canvas.DrawRect(new SKRect(rectangle.Left + 0.75f, rectangle.Top + 0.75f, rectangle.Right - 0.75f, rectangle.Bottom - 0.75f), _stroke);
                }
                Line(canvas, slot.X + slot.Width - 0.5, bounds.Y, slot.X + slot.Width - 0.5, bounds.Bottom, Palette.Line.WithAlpha(150));
                canvas.Restore(); CellsDrawn++;
            }
            // Row selector is painted after columns so frozen cells cannot cover it.
            Fill(canvas, 0, bounds.Y, GridLayout.IndicatorWidth, bounds.Height, background);
            CheckBox(canvas, 14, bounds.Y + bounds.Height / 2, selected);
            Chevron(canvas, 30, bounds.Y + bounds.Height / 2, frame.View.IsDetailExpanded(record.Id), Palette.Muted);
            Line(canvas, 0, bounds.Bottom - 0.5, frame.Width, bounds.Bottom - 0.5, Palette.Line.WithAlpha(150));
        }
        if (frame.View.Rows.Count == 0)
        {
            Text(canvas, "No records match the current filter", 24, frame.BodyTop + 58, Math.Max(0, frame.Width - 48), Palette.Muted, false, ColumnAlignment.Center);
        }
        Line(canvas, GridLayout.IndicatorWidth - 0.5, frame.BodyTop, GridLayout.IndicatorWidth - 0.5, frame.FooterTop, Palette.Line);
        if (frame.Columns.MiddleLeft > GridLayout.IndicatorWidth) Line(canvas, frame.Columns.MiddleLeft - 0.5, frame.BodyTop, frame.Columns.MiddleLeft - 0.5, frame.FooterTop, Palette.Muted.WithAlpha(140));
        if (frame.Columns.MiddleRight < frame.Width) Line(canvas, frame.Columns.MiddleRight + 0.5, frame.BodyTop, frame.Columns.MiddleRight + 0.5, frame.FooterTop, Palette.Muted.WithAlpha(140));
        canvas.Restore();
    }
    private void DrawCell(SKCanvas canvas, GridColumn column, object? value, SKRect rect, CultureInfo culture, GridCellStyle? style)
    {
        float centerY = rect.MidY;
        if (column.Presentation == ColumnPresentation.CheckBox)
        { if (value is bool flag) CheckBox(canvas, rect.MidX, centerY, flag); else Text(canvas, "—", rect.Left + 8, centerY, rect.Width - 16, Palette.Muted, alignment: ColumnAlignment.Center); return; }
        if (column.Presentation == ColumnPresentation.Progress && value is not null)
        {
            double progress = Math.Clamp(Convert.ToDouble(value, culture), 0, 100);
            if (double.IsFinite(progress)) Fill(canvas, rect.Left + 7, rect.Top + 6, (rect.Width - 14) * progress / 100, Math.Max(1, rect.Height - 12), Palette.Accent.WithAlpha(55));
        }
        if (column.Presentation == ColumnPresentation.Badge && value is not null)
        {
            string text = column.Display(value, culture);
            var color = text.ToLowerInvariant() switch { "shipped" or "complete" or "approved" => SKColor.Parse("#21834c"), "cancelled" or "overdue" or "at risk" => SKColor.Parse("#c64646"), "pending" or "on hold" => SKColor.Parse("#a86d13"), _ => Palette.Accent };
            float width = Math.Min(rect.Width - 14, _text.Measure(text) + 16);
            _fill.Color = color.WithAlpha(24); canvas.DrawRoundRect(new SKRect(rect.Left + 7, rect.Top + 4, rect.Left + 7 + width, rect.Bottom - 4), 3, 3, _fill);
            Text(canvas, text, rect.Left + 15, centerY, width - 16, color); return;
        }
        if (column.Presentation == ColumnPresentation.Sparkline && value is IReadOnlyList<double> values && values.Count > 1)
        {
            var finite = values.Where(double.IsFinite).ToArray();
            if (finite.Length < 2) return;
            double min = finite.Min(), range = Math.Max(0.000001, finite.Max() - min);
            using var path = new SKPath();
            for (int i = 0; i < finite.Length; i++)
            {
                float x = rect.Left + 8 + (rect.Width - 16) * i / (finite.Length - 1);
                float y = rect.Bottom - 6 - (float)((finite[i] - min) / range) * Math.Max(1, rect.Height - 12);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            _stroke.Color = Palette.Accent; _stroke.StrokeWidth = 1.5f; canvas.DrawPath(path, _stroke); return;
        }
        Text(canvas, column.Display(value, culture), rect.Left + 8, centerY, rect.Width - 16, style?.Foreground, style?.Bold ?? false, column.Alignment);
    }
    private void DrawDetail(SKCanvas canvas, GridRecord record, RectD bounds)
    {
        Fill(canvas, 0, bounds.Y, bounds.Width, bounds.Height, Palette.Alternate);
        Fill(canvas, 37, bounds.Y + 8, 3, bounds.Height - 16, Palette.Accent);
        var detail = DetailProvider?.Invoke(record);
        if (detail is null)
        {
            Text(canvas, $"Record {record.Id} · version {record.Version}", 54, bounds.Y + 26, bounds.Width - 70, bold: true);
            Text(canvas, "Set DetailProvider to display related records.", 54, bounds.Y + 55, bounds.Width - 70, Palette.Muted); return;
        }
        double width = Math.Max(0, bounds.Width - 76) / Math.Max(1, detail.Headers.Count);
        for (int column = 0; column < detail.Headers.Count; column++)
        {
            Text(canvas, detail.Headers[column], 54 + column * width, bounds.Y + 23, width - 16, Palette.Muted, true);
            for (int row = 0; row < detail.Rows.Count && 48 + row * 23 < bounds.Height - 8; row++)
                Text(canvas, column < detail.Rows[row].Count ? detail.Rows[row][column] : "", 54 + column * width, bounds.Y + 48 + row * 23, width - 16);
        }
        Line(canvas, 0, bounds.Bottom - 0.5, bounds.Width, bounds.Bottom - 0.5, Palette.Line);
    }
    private void DrawFooter(SKCanvas canvas, GridFrame frame)
    {
        Fill(canvas, 0, frame.FooterTop, frame.Width, frame.Height - frame.FooterTop, Palette.Footer);
        Line(canvas, 0, frame.FooterTop + 0.5, frame.Width, frame.FooterTop + 0.5, Palette.Line);
        foreach (var slot in frame.Columns.Slots)
        {
            canvas.Save(); canvas.ClipRect(new SKRect((float)slot.ClipLeft, (float)frame.FooterTop, (float)slot.ClipRight, (float)frame.Height));
            var summary = frame.View.Query.Summaries.FirstOrDefault(item => item.Field.Equals(slot.Field, StringComparison.OrdinalIgnoreCase));
            if (summary is not null && frame.View.Summaries.TryGetValue(summary.Key, out var value))
            {
                var column = frame.View.Table.Schema[slot.Field];
                string text = summary.Kind is SummaryKind.Count or SummaryKind.DistinctCount ? $"Count: {value:N0}" : $"{(summary.Kind == SummaryKind.Sum ? "Σ" : summary.Kind.ToString())}: {column.Display(value, frame.View.Table.Schema.Culture)}";
                Text(canvas, text, slot.X + 8, frame.FooterTop + (frame.Height - frame.FooterTop) / 2, slot.Width - 16, bold: true, alignment: column.Alignment);
            }
            Line(canvas, slot.X + slot.Width - 0.5, frame.FooterTop, slot.X + slot.Width - 0.5, frame.Height, Palette.Line);
            canvas.Restore();
        }
    }
    public void Dispose() { _text.Dispose(); _fill.Dispose(); _stroke.Dispose(); _textPaint.Dispose(); }
}

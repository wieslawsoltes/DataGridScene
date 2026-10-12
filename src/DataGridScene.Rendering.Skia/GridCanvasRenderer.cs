using DataGridScene.Core;
using DataGridScene.Layout;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;

namespace DataGridScene.Rendering.Skia;

/// <summary>Paints only visible rows and columns into a caller-owned Skia canvas; no bitmap copy or UI dependency.</summary>
public sealed partial class GridCanvasRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private ShapedTextCache _text = new();
    public GridPalette Palette { get; set; } = GridPalette.Light;
    public IList<ConditionalFormatRule> ConditionalFormats { get; } = new List<ConditionalFormatRule>();
    public Func<GridRecord, GridDetailData>? DetailProvider { get; set; }
    public Func<CellAddress, bool>? IsNativeCell { get; set; }
    public IReadOnlyDictionary<string, string> FilterTexts { get; set; } = new Dictionary<string, string>();
    public double? DragMarkerX { get; set; }
    public int CellsDrawn { get; private set; }
    public int RowsDrawn { get; private set; }
    public long DrawCount { get; private set; }
    public double LastDrawMilliseconds { get; private set; }
    public int CachedTextRuns => _text.Count;
    public string FontFamily => _text.FontFamily;
    /// <summary>Retains an independent copy; the caller may dispose its typeface immediately.</summary>
    public void SetTypeface(SKTypeface typeface, float size = 13)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(typeface);
        var replacement = new ShapedTextCache(size, typeface: typeface);
        _text.Dispose(); _text = replacement;
    }
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
    public string? HoveredColumn { get; set; }
    public long? HoveredRecordId { get; set; }
    // Retained immutable native paths eliminate per-row/per-header allocations during paint.
    private static SKPath Triangle(bool down, bool horizontal = false)
    {
        var path = new SKPath();
        if (horizontal) { path.MoveTo(-2, -3); path.LineTo(2, 0); path.LineTo(-2, 3); }
        else { path.MoveTo(-3, down ? -2 : 2); path.LineTo(3, down ? -2 : 2); path.LineTo(0, down ? 2 : -2); }
        path.Close(); return path;
    }
    private static SKPath FunnelPath()
    {
        var path = new SKPath(); path.MoveTo(-4,-4); path.LineTo(4,-4); path.LineTo(1,0);
        path.LineTo(1,4); path.LineTo(-1,5); path.LineTo(-1,0); path.Close(); return path;
    }
    private readonly SKPath _down = Triangle(true), _up = Triangle(false), _right = Triangle(false, true), _funnel = FunnelPath();
    public int RetainedGlyphPaths => 4;
    private static void Glyph(SKCanvas canvas, SKPath path, double x, double y, SKPaint paint)
    {
        int saved = canvas.SaveCount; canvas.Save();
        try { canvas.Translate((float)x, (float)y); canvas.DrawPath(path, paint); }
        finally { canvas.RestoreToCount(saved); }
    }
    private void Chevron(SKCanvas canvas, double x, double y, bool expanded, SKColor color)
    {
        _fill.Color = color; Glyph(canvas, expanded ? _down : _right, x, y, _fill);
    }
    private void SortArrow(SKCanvas canvas, double x, double y, SortDirection direction, SKColor color)
    {
        _fill.Color = color; Glyph(canvas, direction == SortDirection.Ascending ? _up : _down, x, y, _fill);
    }
    private void CheckBox(SKCanvas canvas, double x, double y, bool selected, bool mixed = false)
    {
        _fill.Color = selected || mixed ? Palette.Accent : Palette.Background; canvas.DrawRoundRect(new SKRect((float)x - 6, (float)y - 6, (float)x + 6, (float)y + 6), 1, 1, _fill);
        _stroke.Color = selected || mixed ? Palette.Accent : Palette.Muted; _stroke.StrokeWidth = 1;
        canvas.DrawRoundRect(new SKRect((float)x - 6, (float)y - 6, (float)x + 6, (float)y + 6), 1, 1, _stroke);
        if (mixed) Line(canvas, x - 3, y, x + 3, y, SKColors.White, 1.5f);
        else if (selected) { Line(canvas, x - 3, y, x - 1, y + 2, SKColors.White, 1.5f); Line(canvas, x - 1, y + 2, x + 4, y - 3, SKColors.White, 1.5f); }
    }
    private bool _disposed;
    public void Draw(SKCanvas canvas, GridFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(frame);
        var watch = Stopwatch.StartNew(); CellsDrawn = RowsDrawn = SummaryCellsDrawn = 0; DrawCount++;
        int saved = canvas.SaveCount;
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(0, 0, (float)frame.Width, (float)frame.Height));
            Fill(canvas, 0, 0, frame.Width, frame.Height, Palette.Background);
            DrawBody(canvas, frame); DrawHeaders(canvas, frame); DrawFooter(canvas, frame);
            if (DragMarkerX is double marker) Line(canvas, marker, frame.HeaderTop, marker, frame.FooterTop, Palette.Accent, 2);
        }
        finally
        {
            // Application getters, styles and detail providers can throw under nested
            // clips. Restore the caller's entire stack, not just one saved level.
            canvas.RestoreToCount(saved); LastDrawMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
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
                SortArrow(canvas, box.Right - 31, box.Y + box.Height / 2, frame.View.Query.Groups[i].Direction, Palette.Muted);
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
            if (HoveredColumn == column.Field) Fill(canvas, slot.X, frame.HeaderTop, slot.Width, frame.Metrics.HeaderHeight, Palette.Selection.WithAlpha(150));
            Text(canvas, column.Caption, slot.X + 9, frame.HeaderTop + frame.Metrics.HeaderHeight / 2, slot.Width - 42);
            var sort = frame.View.Query.Sorts.Select((value, index) => (value, index)).FirstOrDefault(item => item.value.Field.Equals(column.Field, StringComparison.OrdinalIgnoreCase));
            if (sort.value is not null)
            {
                SortArrow(canvas, slot.X + slot.Width - 29, frame.HeaderTop + 15, sort.value.Direction, Palette.Accent);
                if (frame.View.Query.Sorts.Count > 1) Text(canvas, (sort.index + 1).ToString(), slot.X + slot.Width - 23, frame.HeaderTop + 21, 10, Palette.Accent);
            }
            // Funnel glyph is vector geometry, not a copied icon asset.
            double fx = slot.X + slot.Width - 12, fy = frame.HeaderTop + frame.Metrics.HeaderHeight / 2;
            _stroke.Color = FilterTexts.ContainsKey(column.Field) ? Palette.Accent : HoveredColumn == column.Field ? Palette.Muted : Palette.Muted.WithAlpha(100);
            _stroke.StrokeWidth = 1; Glyph(canvas, _funnel, fx, fy, _stroke);
            if (frame.Metrics.ShowFilterRow)
            {
                bool hasFilter = FilterTexts.TryGetValue(column.Field, out string? filter);
                Text(canvas, hasFilter ? filter! : "Filter…", slot.X + 9, frame.FilterTop + frame.Metrics.FilterHeight / 2, slot.Width - 18, hasFilter ? Palette.Accent : Palette.Muted);
            }
            Line(canvas, slot.X + slot.Width - 0.5, frame.HeaderTop, slot.X + slot.Width - 0.5, frame.BodyTop, Palette.Line);
            canvas.Restore();
        }
        foreach (var band in frame.Bands)
        {
            double top = frame.BandTop + band.Depth * frame.Metrics.BandHeight;
            double bottom = top + band.RowSpan * frame.Metrics.BandHeight;
            canvas.Save();
            canvas.ClipRect(new SKRect((float)band.ClipLeft, (float)top, (float)band.ClipRight, (float)bottom));
            Fill(canvas, band.X, top, band.Width, bottom - top, Palette.Header);
            // Keep the caption visible for partially clipped, very wide bands.
            Text(canvas, band.Caption, band.ClipLeft + 8, (top + bottom) / 2, band.ClipRight - band.ClipLeft - 16, Palette.Muted, true, ColumnAlignment.Center);
            Line(canvas, band.X, bottom - 0.5, band.X + band.Width, bottom - 0.5, Palette.Line);
            Line(canvas, band.X + band.Width - 0.5, top, band.X + band.Width - 0.5, bottom, Palette.Line);
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
            if (row.Kind is GridRowKind.Group or GridRowKind.GroupFooter)
            { DrawGroupSummaryRow(canvas, frame, index, row, bounds); continue; }
            if (row.Kind == GridRowKind.Detail) { DrawDetail(canvas, row.Record!, bounds); continue; }
            var record = row.Record!;
            bool selected = frame.Selection.Mode != GridSelectionMode.Cells && frame.Selection.Contains(record.Id);
            SKColor background = selected ? Palette.Selection : HoveredRecordId == record.Id ? Palette.Group : index % 2 == 0 ? Palette.Background : Palette.Alternate;
            Fill(canvas, 0, bounds.Y, frame.Width, bounds.Height, background);
            foreach (var slot in frame.Columns.Slots)
            {
                var column = frame.View.Table.Schema.Columns[slot.SchemaIndex];
                bool hosted = IsNativeCell?.Invoke(new(record.Id, column.Field)) == true;
                object? value = hosted ? null : column.GetValue(record);
                var rectangle = new SKRect((float)slot.X, (float)bounds.Y, (float)(slot.X + slot.Width), (float)bounds.Bottom);
                canvas.Save(); canvas.ClipRect(new SKRect((float)slot.ClipLeft, (float)bounds.Y, (float)slot.ClipRight, (float)bounds.Bottom));
                GridCellStyle? style = null;
                foreach (var rule in ConditionalFormats) if (rule.Matches(record, column)) style = rule.Style;
                bool cellSelected = frame.Selection.ContainsCell(record.Id, column.Field);
                if (frame.Selection.Mode == GridSelectionMode.Cells && cellSelected) Fill(canvas, slot.X, bounds.Y, slot.Width, bounds.Height, Palette.Selection);
                if (style?.Background is SKColor fill && !cellSelected) Fill(canvas, slot.X, bounds.Y, slot.Width, bounds.Height, fill);
                if (!hosted) DrawCell(canvas, column, value, rectangle, frame.View.Table.Schema.Culture, style);
                if (frame.Selection.FocusedCell is { } focus && focus.RecordId == record.Id && focus.Field.Equals(column.Field, StringComparison.OrdinalIgnoreCase))
                {
                    _stroke.Color = Palette.Accent; _stroke.StrokeWidth = 1.5f; canvas.DrawRect(new SKRect(rectangle.Left + 0.75f, rectangle.Top + 0.75f, rectangle.Right - 0.75f, rectangle.Bottom - 0.75f), _stroke);
                }
                Line(canvas, slot.X + slot.Width - 0.5, bounds.Y, slot.X + slot.Width - 0.5, bounds.Bottom, Palette.Line.WithAlpha(150));
                canvas.Restore(); CellsDrawn++;
            }
            // Row selector is painted after columns so frozen cells cannot cover it.
            Fill(canvas, 0, bounds.Y, GridLayout.IndicatorWidth, bounds.Height, background);
            CheckBox(canvas, 14, bounds.Y + bounds.Height / 2, frame.Selection.Contains(record.Id), frame.Selection.Mode == GridSelectionMode.Cells && frame.Selection.Contains(record.Id));
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
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _text.Dispose(); _fill.Dispose(); _stroke.Dispose(); _textPaint.Dispose();
        _down.Dispose(); _up.Dispose(); _right.Dispose(); _funnel.Dispose();
    }
}

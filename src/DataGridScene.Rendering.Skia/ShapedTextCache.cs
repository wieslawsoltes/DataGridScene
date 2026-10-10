using System.Globalization;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace DataGridScene.Rendering.Skia;

/// <summary>Bounded HarfBuzz-shaped glyph cache. Font resources are owned by the cache.</summary>
public sealed class ShapedTextCache : IDisposable
{
    private sealed record Entry(SKTextBlob? Blob, float Width);
    private readonly Dictionary<(string Text, bool Bold), Entry> _entries = [];
    private readonly Queue<(string Text, bool Bold)> _order = [];
    private readonly SKTypeface _typeface;
    private readonly SKFont _normal;
    private readonly SKFont _bold;
    private readonly SKShaper _shaper;
    public int Capacity { get; }
    public int Count => _entries.Count;
    public float BaselineOffset { get; }
    public string FontFamily => _typeface.FamilyName;

    public ShapedTextCache(float size = 13, int capacity = 4096, SKTypeface? typeface = null)
    {
        if (capacity < 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (!float.IsFinite(size) || size <= 0 || size > 256) throw new ArgumentOutOfRangeException(nameof(size));
        Capacity = capacity;
        // A loaded web font is not installed in SKFontManager. Looking it up again
        // by FamilyName silently selects the browser's monospace fallback.
        if (typeface is null) _typeface = SKTypeface.FromFamilyName("Arial");
        else
        {
            using var stream = typeface.OpenStream(out int faceIndex);
            using var serialized = SKData.Create(stream);
            _typeface = SKTypeface.FromData(serialized, faceIndex) ?? throw new InvalidOperationException("Unable to retain the supplied typeface.");
        }
        _normal = new(_typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        _bold = new(_typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Embolden = true };
        _shaper = new(_typeface);
        _normal.GetFontMetrics(out var metrics);
        BaselineOffset = -(metrics.Ascent + metrics.Descent) / 2;
    }

    private unsafe Entry Shape(string text, bool bold)
    {
        var key = (text, bold);
        if (_entries.TryGetValue(key, out var cached)) return cached;
        var font = bold ? _bold : _normal;
        var shaped = _shaper.Shape(text, 0, 0, font);
        SKTextBlob? blob = null;
        if (shaped.Codepoints.Length > 0)
        {
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocateRawPositionedRun(font, shaped.Codepoints.Length, null);
            for (int index = 0; index < shaped.Codepoints.Length; index++)
            {
                run.Glyphs[index] = checked((ushort)shaped.Codepoints[index]);
                run.Positions[index] = shaped.Points[index];
            }
            blob = builder.Build();
        }
        var entry = new Entry(blob, shaped.Width);
        _entries.Add(key, entry); _order.Enqueue(key);
        while (_entries.Count > Capacity)
        {
            var oldest = _order.Dequeue();
            _entries[oldest].Blob?.Dispose();
            _entries.Remove(oldest);
        }
        return entry;
    }

    public float Measure(string text, bool bold = false) => text.Length == 0 ? 0 : Shape(text, bold).Width;

    public void Draw(SKCanvas canvas, string text, float x, float centerY, float maximumWidth, SKPaint paint, bool bold = false, SKTextAlign alignment = SKTextAlign.Left)
    {
        if (text.Length == 0 || maximumWidth < 1) return;
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        var entry = Shape(text, bold);
        if (entry.Width > maximumWidth)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(text);
            int low = 0, high = starts.Length;
            while (low < high)
            {
                int count = (low + high + 1) / 2;
                int end = count == starts.Length ? text.Length : starts[count];
                if (Measure(text[..end] + "…", bold) <= maximumWidth) low = count;
                else high = count - 1;
            }
            int length = low == starts.Length ? text.Length : starts[low];
            entry = Shape(text[..length] + "…", bold);
        }
        float left = alignment switch
        {
            SKTextAlign.Right => x + maximumWidth - entry.Width,
            SKTextAlign.Center => x + (maximumWidth - entry.Width) / 2,
            _ => x
        };
        if (entry.Blob is not null) canvas.DrawText(entry.Blob, left, centerY + BaselineOffset, paint);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Blob?.Dispose();
        _entries.Clear(); _order.Clear();
        _shaper.Dispose(); _normal.Dispose(); _bold.Dispose(); _typeface.Dispose();
    }
}

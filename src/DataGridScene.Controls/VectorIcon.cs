using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace DataGridScene.Controls;

public enum SceneGlyph { Table, Previous, Next, Add, Delete, Undo, Redo, Copy, Paste, Columns, Filter, Save, Restore, Export, Theme, Expand, Collapse, Search, Settings, Help, Group, Bands, Live, Edit, BestFit, DropDown }

/// <summary>Independent stroke icons: no icon font, system glyph fallback or vendor bitmap assets.
/// Fixed logical geometry remains stable while text fonts load. Native paints are released on unload.</summary>
public sealed class VectorIcon : SKCanvasElement
{
    private SKPaint? _paint;
    public SceneGlyph Glyph { get; }
    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(nameof(IsDark), typeof(bool), typeof(VectorIcon),
        new PropertyMetadata(false, (owner, _) => ((VectorIcon)owner).Invalidate()));
    public bool IsDark { get => (bool)GetValue(IsDarkProperty); set => SetValue(IsDarkProperty, value); }
    public VectorIcon(SceneGlyph glyph, double size = 24)
    {
        Glyph = glyph; Width = Height = size; IsHitTestVisible = false;
        Loaded += (_, _) => Invalidate();
        Unloaded += (_, _) => { _paint?.Dispose(); _paint = null; };
    }
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        _paint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        _paint.Color = IsDark ? SKColor.Parse("#91c8f2") : SKColor.Parse("#0878bc");
        int saved = canvas.SaveCount; canvas.Save();
        try
        {
            canvas.Scale((float)area.Width / 24, (float)area.Height / 24);
            void L(float x, float y, float xx, float yy) => canvas.DrawLine(x, y, xx, yy, _paint);
            void R(float x, float y, float w, float h) => canvas.DrawRect(x, y, w, h, _paint);
            void C(float x, float y, float radius) => canvas.DrawCircle(x, y, radius, _paint);
            void Arrow(bool left)
            {
                if (left) { L(16, 4, 7, 12); L(7, 12, 16, 20); } else { L(8, 4, 17, 12); L(17, 12, 8, 20); }
            }
            switch (Glyph)
            {
                case SceneGlyph.DropDown: L(6, 9, 12, 15); L(12, 15, 18, 9); break;
                case SceneGlyph.Previous: Arrow(true); break;
                case SceneGlyph.Next: Arrow(false); break;
                case SceneGlyph.Add: R(3, 3, 13, 17); L(17, 11, 17, 21); L(12, 16, 22, 16); break;
                case SceneGlyph.Delete: L(6, 7, 18, 7); R(8, 7, 8, 13); L(10, 4, 14, 4); L(11, 10, 11, 17); L(14, 10, 14, 17); break;
                case SceneGlyph.Copy: R(3, 3, 12, 14); R(8, 8, 12, 14); break;
                case SceneGlyph.Paste: R(5, 5, 14, 17); R(9, 2, 6, 5); L(9, 12, 16, 12); L(9, 16, 16, 16); break;
                case SceneGlyph.Undo: case SceneGlyph.Redo:
                    if (Glyph == SceneGlyph.Redo) { canvas.Translate(24, 0); canvas.Scale(-1, 1); }
                    L(4, 5, 4, 12); L(4, 12, 11, 12); L(4, 12, 10, 6); L(10, 6, 17, 6); L(17, 6, 20, 10); L(20, 10, 20, 16); L(20, 16, 16, 20); break;
                case SceneGlyph.Filter: L(3, 4, 21, 4); L(21, 4, 14, 12); L(14, 12, 14, 20); L(14, 20, 10, 18); L(10, 18, 10, 12); L(10, 12, 3, 4); break;
                case SceneGlyph.Save: R(3, 3, 18, 18); R(7, 3, 9, 7); R(7, 14, 10, 7); L(13, 5, 13, 8); break;
                case SceneGlyph.Restore: R(3, 3, 15, 13); L(7, 7, 14, 7); L(7, 11, 12, 11); L(10, 17, 6, 21); L(6, 21, 13, 21); L(6, 21, 6, 15); L(13, 21, 20, 16); break;
                case SceneGlyph.Export: R(3, 3, 14, 18); L(9, 12, 22, 12); L(18, 8, 22, 12); L(22, 12, 18, 16); break;
                case SceneGlyph.Expand: L(3, 9, 3, 3); L(3, 3, 9, 3); L(15, 3, 21, 3); L(21, 3, 21, 9); L(21, 15, 21, 21); L(21, 21, 15, 21); L(9, 21, 3, 21); L(3, 21, 3, 15); break;
                case SceneGlyph.Collapse: L(3, 9, 9, 9); L(9, 9, 9, 3); L(15, 3, 15, 9); L(15, 9, 21, 9); L(21, 15, 15, 15); L(15, 15, 15, 21); L(9, 21, 9, 15); L(9, 15, 3, 15); break;
                case SceneGlyph.Search: C(10, 10, 6); L(14, 14, 21, 21); break;
                case SceneGlyph.Theme: R(3, 4, 18, 16); L(3, 9, 21, 9); L(9, 9, 9, 20); C(15, 14, 2); break;
                case SceneGlyph.Settings: L(4, 6, 20, 6); L(4, 12, 20, 12); L(4, 18, 20, 18); C(9, 6, 2); C(16, 12, 2); C(8, 18, 2); break;
                case SceneGlyph.Help: C(12, 12, 9); L(9, 8, 12, 6); L(12, 6, 15, 8); L(15, 8, 15, 10); L(15, 10, 12, 13); L(12, 13, 12, 14); C(12, 18, .5f); break;
                case SceneGlyph.Live: L(2, 12, 6, 12); L(6, 12, 9, 4); L(9, 4, 13, 20); L(13, 20, 17, 9); L(17, 9, 20, 12); L(20, 12, 23, 12); break;
                case SceneGlyph.Edit: L(5, 16, 16, 4); L(16, 4, 20, 8); L(20, 8, 9, 20); L(9, 20, 3, 22); L(3, 22, 5, 16); L(5, 16, 9, 20); break;
                case SceneGlyph.BestFit: R(3, 3, 18, 18); L(12, 3, 12, 21); L(4, 12, 20, 12); L(7, 9, 4, 12); L(4, 12, 7, 15); L(17, 9, 20, 12); L(20, 12, 17, 15); break;
                case SceneGlyph.Group: R(2, 3, 9, 5); R(11, 16, 11, 5); L(6, 8, 6, 18); L(6, 18, 11, 18); L(6, 11, 21, 11); break;
                default:
                    R(3, 3, 18, 18); L(3, 9, 21, 9); L(3, 15, 21, 15); L(9, 9, 9, 21); L(15, Glyph == SceneGlyph.Bands ? 3 : 9, 15, 21);
                    break;
            }
        }
        finally { canvas.RestoreToCount(saved); }
    }
}

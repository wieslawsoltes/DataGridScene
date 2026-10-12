using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;
using Windows.Foundation;

namespace DataGridScene.Controls;

/// <summary>Lightweight ordinary Uno visual trees for model-formatted text, badges,
/// progress and sparklines. These are controls/shapes, not per-cell Skia surfaces.</summary>
public static class NativeCellPresenters
{
    public static GridCellTemplate Create(ColumnPresentation presentation, GridCanvasRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!Enum.IsDefined(presentation) || presentation == ColumnPresentation.CheckBox)
            throw new ArgumentOutOfRangeException(nameof(presentation));
        return new(() => new Presenter(presentation, renderer))
        {
            IsDisplayOnly = true, CommitOnLostFocus = false,
            Bind = (element, buffer) => ((Presenter)element).BindValue(buffer),
            UpdateAppearance = (element, state) => ((Presenter)element).ApplyAppearance(state),
            Unbind = (element, _) => ((Presenter)element).ClearValue()
        };
    }
    private sealed class Presenter : Grid
    {
        private readonly ColumnPresentation _kind;
        private readonly GridCanvasRenderer _renderer;
        private readonly TextBlock _text = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
        private readonly SolidColorBrush _foreground = new(), _accent = new(), _tint = new();
        private readonly Border? _badge;
        private readonly ProgressBar? _progress;
        private readonly Polyline? _line;
        private GridCellEditBuffer? _buffer;
        private GridCellVisualState _state;
        private double[] _values = [];

        public Presenter(ColumnPresentation kind, GridCanvasRenderer renderer)
        {
            _kind = kind; _renderer = renderer; IsHitTestVisible = false;
            _text.Foreground = _foreground;
            if (kind == ColumnPresentation.Progress)
            {
                _progress = new() { Minimum = 0, Maximum = 100, Height = 7,
                    Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _accent, Opacity = .35, IsTabStop = false };
                Children.Add(_progress);
            }
            if (kind == ColumnPresentation.Sparkline)
            {
                _line = new() { Stroke = _accent, StrokeThickness = 1.5 };
                Children.Add(_line); SizeChanged += (_, _) => UpdatePoints();
            }
            else if (kind == ColumnPresentation.Badge)
            {
                _text.Margin = new Thickness(6, 0, 6, 0); _text.FontSize = 12;
                _badge = new() { Child = _text, Background = _tint, CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(7, 4, 7, 4), HorizontalAlignment = HorizontalAlignment.Left };
                Children.Add(_badge);
            }
            else Children.Add(_text);
        }
        public void BindValue(GridCellEditBuffer buffer)
        {
            _buffer = buffer;
            _text.Text = _kind == ColumnPresentation.Sparkline ? "" : buffer.Column.Display(buffer.Value, buffer.Culture);
            _text.TextAlignment = buffer.Column.Alignment switch
            { ColumnAlignment.Right => TextAlignment.Right, ColumnAlignment.Center => TextAlignment.Center, _ => TextAlignment.Left };
            if (_progress is not null)
            {
                double value = Convert.ToDouble(buffer.Value, buffer.Culture);
                _progress.Value = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            }
            if (_line is not null)
            {
                _values = buffer.Value is IReadOnlyList<double> values ? values.Where(double.IsFinite).ToArray() : [];
                UpdatePoints();
            }
            ApplyAppearance(_state);
        }
        public void ApplyAppearance(GridCellVisualState state)
        {
            _state = state;
            SKColor foreground = _renderer.Palette.Foreground, accent = _renderer.Palette.Accent;
            GridCellStyle? style = null;
            if (_buffer is { } buffer)
                foreach (var rule in _renderer.ConditionalFormats)
                    if (rule.Matches(buffer.Record, buffer.Column)) style = rule.Style;
            if (style?.Foreground is { } color) foreground = color;
            _text.FontWeight = style?.Bold == true ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            if (_badge is not null)
            {
                accent = _text.Text.ToLowerInvariant() switch
                {
                    "shipped" or "complete" or "approved" => SKColor.Parse("#21834c"),
                    "cancelled" or "overdue" or "at risk" => SKColor.Parse("#c64646"),
                    "pending" or "on hold" => SKColor.Parse("#a86d13"), _ => accent
                };
                foreground = accent; SetColor(_tint, accent.WithAlpha(24));
            }
            SetColor(_foreground, foreground); SetColor(_accent, accent);
        }
        private static void SetColor(SolidColorBrush brush, SKColor color)
        {
            var value = Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);
            if (brush.Color != value) brush.Color = value;
        }
        private void UpdatePoints()
        {
            if (_line is null) return;
            var points = new PointCollection();
            if (_values.Length > 1 && ActualWidth > 16 && ActualHeight > 12)
            {
                double min = _values.Min(), range = Math.Max(.000001, _values.Max() - min);
                for (int i = 0; i < _values.Length; i++)
                    points.Add(new Point(8 + (ActualWidth - 16) * i / (_values.Length - 1),
                        ActualHeight - 6 - (_values[i] - min) / range * (ActualHeight - 12)));
            }
            _line.Points = points;
        }
        public void ClearValue() { _buffer = null; _values = []; _text.Text = ""; }
    }
}

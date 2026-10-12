using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    /// <summary>Persistent native scrollbars. A zero range leaves a disabled rail, not an invisible hit target.</summary>
    public ScrollBar VerticalScrollBar => _vertical;
    public ScrollBar HorizontalScrollBar => _horizontal;
    public const double ScrollBarThickness = 18;

    private void InitializeScrolling()
    {
        // Standalone WinUI ScrollBar defaults to IndicatorMode.None; unlike a
        // ScrollViewer, this canvas host has nobody to select its visible state.
        // https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.scrollbar.indicatormode
        _vertical.Width = _horizontal.Height = ScrollBarThickness;
        _root.ColumnDefinitions[1].Width = new GridLength(ScrollBarThickness);
        _root.RowDefinitions[1].Height = new GridLength(ScrollBarThickness);
        foreach (var bar in new[] { _vertical, _horizontal })
        {
            bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            bar.IsTabStop = true;
            bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            bar.VerticalAlignment = VerticalAlignment.Stretch;
            bool vertical = ReferenceEquals(bar, _vertical);
            AutomationProperties.SetName(bar, vertical ? "Grid vertical scrollbar" : "Grid horizontal scrollbar");
            AutomationProperties.SetAutomationId(bar, vertical ? "GridVerticalScrollBar" : "GridHorizontalScrollBar");
            bar.ValueChanged += (_, _) =>
            {
                if (_updatingScroll || _disposed) return;
                ScrollTo(vertical ? _offsetX : bar.Value, vertical ? bar.Value : _offsetY);
                // The value changed before validation ran. A rejected edit must
                // restore the thumb as well as keeping the viewport unchanged.
                RestoreScrollBarValues();
            };
            bar.KeyDown += (_, args) =>
            {
                if (args.Handled) return;
                double? next = args.Key switch
                {
                    VirtualKey.Home => bar.Minimum,
                    VirtualKey.End => bar.Maximum,
                    VirtualKey.PageUp => bar.Value - bar.LargeChange,
                    VirtualKey.PageDown => bar.Value + bar.LargeChange,
                    VirtualKey.Up when vertical => bar.Value - bar.SmallChange,
                    VirtualKey.Down when vertical => bar.Value + bar.SmallChange,
                    VirtualKey.Left when !vertical => bar.Value - bar.SmallChange,
                    VirtualKey.Right when !vertical => bar.Value + bar.SmallChange,
                    _ => null
                };
                if (next is not double value) return;
                bar.Value = Math.Clamp(value, bar.Minimum, bar.Maximum);
                args.Handled = true;
            };
        }
        // A noninteractive corner joins the rails and never covers native cells.
        var corner = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsHitTestVisible = false };
        Grid.SetRow(corner, 1); Grid.SetColumn(corner, 1); _root.Children.Add(corner);
        UpdateScrollBars();
    }

    private void UpdateScrollBars()
    {
        if (_disposed) return;
        _updatingScroll = true;
        try
        {
            _vertical.Maximum = Frame is null ? 0 : Math.Max(0, HeightIndex.TotalHeight - Frame.BodyHeight);
            _vertical.ViewportSize = Frame?.BodyHeight ?? 0;
            _vertical.SmallChange = RowHeight;
            _vertical.LargeChange = Math.Max(RowHeight, _vertical.ViewportSize);
            _horizontal.Maximum = Frame?.Columns.MaximumOffset ?? 0;
            _horizontal.ViewportSize = Frame is null ? 0 : Math.Max(0, Frame.Columns.MiddleRight - Frame.Columns.MiddleLeft);
            _horizontal.SmallChange = 32;
            _horizontal.LargeChange = Math.Max(32, _horizontal.ViewportSize);
            _vertical.IsEnabled = _vertical.Maximum > 0;
            _horizontal.IsEnabled = _horizontal.Maximum > 0;
            _vertical.Value = _offsetY;
            _horizontal.Value = _offsetX;
        }
        finally { _updatingScroll = false; }
    }

    private void RestoreScrollBarValues()
    {
        if (_disposed || _updatingScroll) return;
        _updatingScroll = true;
        try { _vertical.Value = _offsetY; _horizontal.Value = _offsetX; }
        finally { _updatingScroll = false; }
    }
}

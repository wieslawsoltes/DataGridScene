using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    public long CellControlTreeAttachCount { get; private set; }
    public long CellControlTreeDetachCount { get; private set; }
    public long CellControlSizeWriteCount { get; private set; }
    public long CellControlTranslationWriteCount { get; private set; }
    public long CellControlTabIndexWriteCount { get; private set; }
    public int AttachedCellControlCount => _nativeCells.Children.Count;

    // A layout-only Border/Panel is transparent to native tab-stop candidate search.
    // Give its content a real, non-focusable Control scope so the cell's rank is
    // compared before the independent TabIndex values of its descendants. This
    // scope never becomes an extra tab stop and does not handle traversal keys.
    private sealed class NativeCellFocusScope : UserControl
    {
        public NativeCellFocusScope()
        {
            IsTabStop = false;
            TabFocusNavigation = KeyboardNavigationMode.Local;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Padding = new Thickness(0);
            BorderThickness = new Thickness(0);
            MinWidth = MinHeight = 0;
        }
    }

    // Retain pooled controls in the same visual parent. Collapsed inactive hosts cannot
    // paint, hit-test or enter the focus traversal. Their buffers are still deactivated.
    // Reuse avoids unloading/reloading the native editor template on each scrolled row.
    private void AttachCellHost(NativeHost host)
    {
        if (host.Attached) return;
        var body = host.Container.Child;
        host.Container.Child = null;
        host.Container.Child = new NativeCellFocusScope { Content = body };
        _nativeCells.Children.Add(host.Container); host.Attached = true; CellControlTreeAttachCount++;
    }
    private void DetachCellHost(NativeHost host)
    {
        if (!host.Attached) return;
        _nativeCells.Children.Remove(host.Container); host.Attached = false; CellControlTreeDetachCount++;
        // Restore the direct body so ReleaseCellHost can detach application-owned
        // content. Pooled hosts never take this path and retain their entire tree.
        if (host.Container.Child is NativeCellFocusScope scope)
        {
            var body = scope.Content;
            scope.Content = null;
            host.Container.Child = null;
            host.Container.Child = body;
        }
    }
    // During one reconciliation, the outgoing viewport is a transient reuse set.
    // Do not throw away its 129th control just to recreate it for the incoming row.
    // Only the unused remainder is subject to the persistent idle-pool limit.
    private void TrimCellPool()
    {
        if (_pooledCells <= MaximumPooledCellControls) return;
        foreach (var pair in _cellPools.ToArray())
        {
            while (_pooledCells > MaximumPooledCellControls && pair.Value.TryPop(out var host))
            { _pooledCells--; ReleaseCellHost(host); }
            if (pair.Value.Count == 0) _cellPools.Remove(pair.Key);
            if (_pooledCells <= MaximumPooledCellControls) break;
        }
    }
    private void PlaceCellHost(NativeHost host, NativeCellPlacement placement)
    {
        var bounds = placement.Bounds; var clip = placement.ClipBounds;
        // Rank only the private Control scope. Application descendants retain their
        // own TabIndex, IsTabStop, enabled state and custom native Tab handling.
        if (host.Container.Child is NativeCellFocusScope scope && scope.TabIndex != placement.TabIndex)
        { scope.TabIndex = placement.TabIndex; CellControlTabIndexWriteCount++; }
        // Translation is a render change, not a Canvas.Left/Top arrange change.
        // Dimensions remain full-cell dimensions even where a frozen pane clips them.
        if (host.Position.X != bounds.X) { host.Position.X = bounds.X; CellControlTranslationWriteCount++; }
        if (host.Position.Y != bounds.Y) { host.Position.Y = bounds.Y; CellControlTranslationWriteCount++; }
        if (host.Container.Width != bounds.Width) { host.Container.Width = bounds.Width; CellControlSizeWriteCount++; }
        if (host.Container.Height != bounds.Height) { host.Container.Height = bounds.Height; CellControlSizeWriteCount++; }
        var rectangle = new Rect(clip.X - bounds.X, clip.Y - bounds.Y, clip.Width, clip.Height);
        if (host.Clip.Rect != rectangle) host.Clip.Rect = rectangle;
        host.Placement = placement; CellControlPlacementCount++;
    }
}

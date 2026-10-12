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
    // A non-focusable Control scope ranks the cell before its independent descendants.
    // This scope never becomes an extra tab stop or handles native traversal keys.
    private sealed class NativeCellFocusScope : UserControl
    {
        public NativeCellFocusScope(GridControl owner)
        {
            IsTabStop = false; TabFocusNavigation = KeyboardNavigationMode.Local;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Padding = new Thickness(0); BorderThickness = new Thickness(0);
            MinWidth = MinHeight = 0;
            GettingFocus += (_, args) => owner.PrepareNativeKeyboardHost(this, args);
            Unloaded += (_, _) => owner.CancelNativeKeyboardHost(this);
        }
    }
    // No implementation or interop in desktop builds. Only the browser head needs
    // to hand DOM keyboard ownership off when Uno releases its shared text input.
    partial void PrepareNativeKeyboardHost(UIElement scope, GettingFocusEventArgs args);
    partial void CancelNativeKeyboardHost(UIElement scope);

    private void AttachCellHost(NativeHost host)
    {
        if (host.Attached) return;
        var body = host.Container.Child;
        host.Container.Child = null;
        host.Container.Child = new NativeCellFocusScope(this) { Content = body };
        _nativeCells.Children.Add(host.Container); host.Attached = true; CellControlTreeAttachCount++;
    }
    private void DetachCellHost(NativeHost host)
    {
        if (host.Attached)
        {
            _nativeCells.Children.Remove(host.Container); host.Attached = false; CellControlTreeDetachCount++;
        }
        // Restore the direct body even if attachment failed, so ReleaseCellHost
        // detaches application-owned content. Pooled hosts never take this path.
        if (host.Container.Child is NativeCellFocusScope scope)
        {
            CancelNativeKeyboardHost(scope);
            var body = (UIElement?)scope.Content;
            scope.Content = null; host.Container.Child = null; host.Container.Child = body;
        }
    }
    // The outgoing viewport is a transient reuse set. Do not discard its 129th
    // control only to recreate it immediately; cap just the unused persistent pool.
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
        // Only private scopes are ranked. App descendants retain TabIndex, IsTabStop,
        // enabled state and native key handling. Unchanged ranks are not rewritten.
        if (host.Container.Child is NativeCellFocusScope scope && scope.TabIndex != placement.TabIndex)
        { scope.TabIndex = placement.TabIndex; CellControlTabIndexWriteCount++; }
        if (host.Position.X != bounds.X) { host.Position.X = bounds.X; CellControlTranslationWriteCount++; }
        if (host.Position.Y != bounds.Y) { host.Position.Y = bounds.Y; CellControlTranslationWriteCount++; }
        if (host.Container.Width != bounds.Width) { host.Container.Width = bounds.Width; CellControlSizeWriteCount++; }
        if (host.Container.Height != bounds.Height) { host.Container.Height = bounds.Height; CellControlSizeWriteCount++; }
        var rectangle = new Rect(clip.X - bounds.X, clip.Y - bounds.Y, clip.Width, clip.Height);
        if (host.Clip.Rect != rectangle) host.Clip.Rect = rectangle;
        host.Placement = placement; CellControlPlacementCount++;
    }
}

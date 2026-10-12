using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices.JavaScript;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private bool _nativeKeyboardHostQueued;

    // Use framework interop directly: the reusable control library must not depend
    // on the sample's Uno.Foundation.Runtime.WebAssembly host reference. This import
    // only evaluates the constant implementation below, never application input.
    [JSImport("globalThis.eval")]
    private static partial void EvaluateNativeKeyboardHost(string script);

    partial void RestoreNativeKeyboardHost()
    {
        if (_disposed || _nativeKeyboardHostQueued || XamlRoot is null ||
            FocusManager.GetFocusedElement(XamlRoot) is null or TextBox) return;
        _nativeKeyboardHostQueued = true;
        // Uno defers detaching its shared #uno-input during text-to-control focus
        // changes. Run after that handoff, and re-check managed AND browser focus.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _nativeKeyboardHostQueued = false;
            if (_disposed || XamlRoot is null || !IsFocusWithin(_nativeCells) ||
                FocusManager.GetFocusedElement(XamlRoot) is null or TextBox) return;
            EvaluateNativeKeyboardHost("""
                (() => {
                    const active = document.activeElement;
                    // Never take focus from an external DOM input, a semantic control,
                    // the accessibility entry button, or a newer text editor.
                    if (active && active !== document.body && active !== document.documentElement) return;
                    // Scope this compatibility path to Uno's opt-in accessibility mode.
                    if (!document.getElementById('uno-enable-accessibility')) return;
                    const canvas = document.getElementById('uno-canvas');
                    const host = canvas?.parentElement;
                    if (!host || host === document.body || !host.isConnected ||
                        host.closest('[aria-hidden="true"], [inert]')) return;
                    // Use the existing application host, NOT the aria-hidden canvas.
                    // A negative index allows programmatic ownership without adding
                    // a sequential Tab stop or changing any control's native order.
                    if (!host.hasAttribute('tabindex')) host.tabIndex = -1;
                    host.focus({ preventScroll: true });
                })()
                """);
        })) _nativeKeyboardHostQueued = false;
    }
}

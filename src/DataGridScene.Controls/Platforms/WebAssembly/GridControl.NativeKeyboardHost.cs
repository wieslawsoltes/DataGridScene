using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices.JavaScript;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private JSObject? _nativeKeyboardHandoff;
    private UIElement? _nativeKeyboardHandoffScope;

    // The reusable library has no sample/runtime-host dependency. Only this constant
    // implementation is evaluated; no application text is ever executable input.
    [JSImport("globalThis.eval")]
    private static partial JSObject? CreateNativeKeyboardHandoff(string script);

    partial void PrepareNativeKeyboardHost(UIElement scope, GettingFocusEventArgs args)
    {
        CancelNativeKeyboardHandoff();
        if (_disposed || XamlRoot is null || args.OldFocusedElement is not TextBox ||
            args.NewFocusedElement is null or TextBox) return;
        _nativeKeyboardHandoffScope = scope;
        // GettingFocus runs before Uno schedules the outgoing TextBox's input detach.
        // Observe that exact input's blur. Its microtask completes the DOM handoff
        // before another keyboard event, rather than adding a DispatcherQueue turn.
        // A cancelled focus change leaves the input connected, so this does nothing.
        _nativeKeyboardHandoff = CreateNativeKeyboardHandoff("""
            (() => {
                const input = document.getElementById('uno-input');
                if (!input || document.activeElement !== input ||
                    !document.getElementById('uno-enable-accessibility')) return null;
                const host = document.getElementById('uno-canvas')?.parentElement;
                if (!host || host === document.body || !host.isConnected ||
                    host.closest('[aria-hidden="true"], [inert]')) return null;
                let cancelled = false;
                const afterBlur = () => queueMicrotask(() => {
                    // Do not steal focus from a new text editor, semantic DOM control,
                    // external element, browser chrome or accessibility entry button.
                    const active = document.activeElement;
                    if (cancelled || input.isConnected || document.getElementById('uno-input') ||
                        !document.hasFocus() || !host.isConnected ||
                        host.closest('[aria-hidden="true"], [inert]') ||
                        (active && active !== document.body && active !== document.documentElement)) return;
                    // The real non-hidden app host is a keyboard owner, not another
                    // sequential Tab stop. Never focus the aria-hidden canvas.
                    if (!host.hasAttribute('tabindex')) host.tabIndex = -1;
                    host.focus({ preventScroll: true });
                });
                input.addEventListener('blur', afterBlur, { once: true });
                // A managed cancellation removes the listener as well as invalidating
                // a microtask already queued. No document-level listeners or globals.
                return Object.defineProperty({}, 'cancelled', {
                    set(value) {
                        if (value) { cancelled = true; input.removeEventListener('blur', afterBlur); }
                    }
                });
            })()
            """);
    }

    partial void CancelNativeKeyboardHost(UIElement scope)
    {
        if (ReferenceEquals(scope, _nativeKeyboardHandoffScope)) CancelNativeKeyboardHandoff();
    }

    private void CancelNativeKeyboardHandoff()
    {
        var pending = _nativeKeyboardHandoff;
        _nativeKeyboardHandoff = null; _nativeKeyboardHandoffScope = null;
        if (pending is null) return;
        try { pending.SetProperty("cancelled", true); }
        finally { pending.Dispose(); }
    }
}

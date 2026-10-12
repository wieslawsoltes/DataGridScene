# Native cell Tab order

Native cell hosts have an explicit viewport-local focus rank. Tab and Shift+Tab
follow visible data rows and actual left-to-right column positions rather than the
order in which controls were created or recycled. Frozen panes are ranked by visible
position, not by the Skia painter's middle/left/right layering order. Group/detail
rows, hidden columns and zero-width intersections do not receive native cell ranks.

## Retained local focus scopes

`NativeCellPlacement.TabIndex` is computed by `NativeCellGeometry.Collect` in the
renderer-independent geometry portion of the rendering package. Only intersecting
column slots are sorted, once per collection. Only requested, intersecting data cells
are ranked. Indices start at zero for the viewport, so deep source-row ordinals cannot
overflow them. The existing maximum-cell limit remains enforced.

`GridControl` assigns each rank to a private, non-focusable `UserControl` scope, not
to application child controls. A layout-only Border/Panel is transparent to Uno's
focus-candidate search; ranking it alone can mix a descendant's index with unrelated
cells. The Control scope participates as a candidate containing focusable children,
with `TabFocusNavigation.Local` and `IsTabStop=false`. It is not another Tab stop.

The normal Uno focus system honors disabled controls, a composite cell's own TabIndex
values and native editor key handling. Pending-input validation runs before leaving
an editor. The scope is created once with a new host and retained with the complete
pooled tree. Idle pool members remain collapsed and outside focus traversal. Scope
cleanup restores the direct body before releasing application content, including
failed attachment paths.

Unchanged ranks are not reassigned. `CellControlTabIndexWriteCount` exposes these writes
alongside attachment, binding, creation, translation and size counters. Subrow movement
with an unchanged visible set does not write TabIndex. Larger jumps can change ranks
on reused hosts without creating controls. There is one extra lightweight scope per
retained native host, not per source record. No per-frame focus-tree scan or synthetic
Tab traversal is added.

## Browser text-to-control keyboard handoff

Uno's Skia browser host uses a shared HTML input while a native TextBox is editing.
When focus moves to a non-text control, its input is detached in a microtask. With the
accessibility entry button still present, a subsequent Tab whose browser target is
BODY is intentionally left to browser navigation. Managed focus alone therefore does
not guarantee that the next Tab is delivered to the selected native control.

The browser-only compatibility path observes the exact outgoing input at GettingFocus,
before the framework schedules its detach. A one-shot blur listener queues a microtask
that completes the handoff only after that input is disconnected and no replacement
text input exists. It focuses the existing non-hidden application host with a negative
TabIndex, not the aria-hidden canvas, and never adds a sequential Tab stop. Ordinary
native focus navigation still chooses the next cell/control.

Cancelled or superseded focus changes leave the input connected or create a replacement,
so the handoff does nothing. Moving to another native scope cancels previous work;
unloading/releasing the destination also removes the listener and invalidates an already
queued callback. No document-level input listener, polling loop or global mutable
helper is installed. The handoff refuses to take browser focus from an external input,
semantic control, accessibility entry button or an unfocused browser document.

The initial accessibility entry remains keyboard reachable. Text-to-text moves retain
Uno's real input, including IME behavior. Desktop builds contain no browser interop for
this path. Browser interop evaluates only a constant library-owned script, never model
values or application input; strict CSP deployments that disallow evaluation require
host-side integration instead. This compatibility path is tied to the tested Uno Skia
browser input lifecycle and should be reviewed when that dependency changes.

The integrated [row edit dialog](row-editing.md) observes the same handoff at its own
GettingFocus boundary, including Update/Cancel buttons, and cancels it when the dialog
closes. It does not install a second global keyboard handler or alter native Tab traversal.

## Boundaries

This addresses the **currently realized native controls**. It does not make canvas-only
cells native Tab stops, implement virtualized accessibility providers, or automatically
open editors for offscreen cells. Grid arrow navigation and F2 editing remain separate.
Full DevExpress keyboard/API/accessibility parity and all native desktop/browser/IME
combinations have not been established.

## Validation

Eight geometry contracts run on .NET and in the actual WASM runtime: frozen panes,
reordering, sorting, group/detail rows, clipping, requested-field subsets, 100,000-row
deep scrolling and the native-cell limit.

`tests/browser/native-tab-order.mjs` has fifteen real-input checks covering forward and
reverse traversal, recycled controls, frozen panes, sorted/grouped data, validation,
composite child TabIndex values, disabled children, browser text/control handoffs,
fresh accessibility entry, external DOM focus and retained-tree work counters.
Diagnostics only read existing cells/focus/counters; they do not search for or set the
next focus candidate, attach input handlers, force layout or realize controls.

The complete suite inventory is maintained in [validation](validation.md). Counts are
inventory, not evidence that a particular run passed. Consult the exact commit's Actions
reports. Browser CI uses ANGLE/SwiftShader and does not establish hardware GPU throughput
or end-to-end input latency.

References: [UIElement.TabIndex](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.tabindex),
[UIElement.TabFocusNavigation](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.tabfocusnavigation),
[Uno focus candidate implementation](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI/UI/Xaml/Input/Internal/FocusProperties.cs),
[Uno browser keyboard dispatch](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/ts/Runtime/BrowserKeyboardInputSource.ts),
and [Uno shared input lifecycle](https://github.com/unoplatform/uno/blob/da217df2312e9e39676880f9cb4b8d519cc297e0/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/ts/Runtime/BrowserInvisibleTextBoxViewExtension.ts).

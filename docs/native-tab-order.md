# Native cell Tab order

Native cell hosts now have an explicit viewport-local focus rank. Tab and Shift+Tab
follow visible data rows and the actual left-to-right column positions rather than the
order in which controls happened to be created or recycled. Frozen panes are ranked by
visible position, not by the Skia painter's middle/left/right layering order. Group and
detail rows, hidden columns and zero-width intersections have no native cell ranks.

`NativeCellPlacement.TabIndex` is computed by `NativeCellGeometry.Collect` in the
renderer-independent geometry portion of the rendering package. Only intersecting
column slots are sorted, once per collection. Only the requested, intersecting cells
are ranked. Indices start at zero for the viewport, so a large underlying row ordinal
cannot overflow them. The existing maximum-cell limit remains enforced.

`GridControl` assigns the rank to its private host, not to application-owned child
controls. The normal Uno focus system continues to honor disabled controls, a composite
cell's own TabIndex values, and native editor key handling. Pending-input validation
still runs before a native editor loses focus. Pool members remain collapsed and out
of focus traversal; repairing order never detaches or reinserts them.

Unchanged host ranks are not reassigned. `CellControlTabIndexWriteCount` makes those
writes observable alongside attachment, binding, creation, translation and size counters.
Small translations within an unchanged visible row set require no TabIndex writes.
Large jumps can require new ranks on reused hosts but do not require new controls.

This fixes ordering of the **currently realized native controls**. It does not make
canvas-only cells native tab stops, implement virtualized accessibility providers, or
automatically open an edit-only template for every offscreen cell. Existing grid arrow
navigation and F2 editing remain separate from native focus traversal. It does not
claim full DevExpress keyboard or accessibility parity.

## Validation

Eight shared geometry contracts run on .NET and in the actual WASM runtime. They cover
frozen panes, reordering, sorting, group/detail rows, clipping, subsets, 100,000-row deep
scrolling and rejection at the native-cell limit.

`tests/browser/native-tab-order.mjs` adds eleven checks using real Tab/Shift+Tab and
pointer input. They check initial/recycled order, frozen panes, sorted/grouped data,
invalid edits, custom composite child TabIndex values, disabled child controls, and
bounded work. Diagnostic reads inspect existing controls; they never set focus, repair
TabIndex or realize elements. The eight geometry contracts are executed by one of these
browser checks. Existing suites remain enabled.

The complete inventory is 359 shared managed/geometry contracts (351 baseline plus
8 new), 229 browser checks across 14 harnesses, and 42 embedded real Uno control API
contracts. Counts describe test inventory; consult the commit's Actions result for
pass/fail status. CI uses SwiftShader and is not a hardware GPU throughput benchmark.

References: [UIElement.TabIndex](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.tabindex)
and [UIElement.TabFocusNavigation](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.tabfocusnavigation).

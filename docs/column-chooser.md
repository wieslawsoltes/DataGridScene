# Native column customization

The independent UI takes behavioral guidance from the public [DevExpress WPF column/band chooser](https://docs.devexpress.com/WPF/6217/controls-and-libraries/data-grid/visual-elements/common-elements/column-band-chooser): searchable visibility choices, keyboard selection and checkbox toggles. This is not the vendor control or a complete API, band-tree, drag/drop or pixel-theme reproduction. It uses original native Uno controls and no proprietary assets.

## Using the control

```csharp
// Integrated native dialog, also used by the sample's Columns command.
await grid.ShowColumnChooserAsync();

// Or host the reusable control yourself. Dispose it when its lifetime ends,
// particularly when it has never been loaded into a visual tree.
using var chooser = new ColumnChooser(layout, schema);
chooser.FilterScope = ColumnChooserScope.Hidden;
chooser.SetSearch("Customer");
chooser.ValidateChange = () => { /* throw to reject a changed layout write */ };
```

The native list searches caption, field name and band path. All/Visible/Hidden scopes preserve column order, while real Arrow/Space input selects and toggles visibility. Rows show both the caption and field/band context. Only realized items create native row controls. The retained Freeze and Width editors belong to one selected column rather than being recreated for every search match. Controls inherit the dialog theme; errors have explicit light/dark colors.

Visibility and pinning changes apply immediately. Changed widths commit on Enter, Apply, focus loss or successful dialog dismissal. Opening or visiting an untouched width never rounds its double value or publishes a new layout. Values are invariant decimal-point input in the existing 36..4096 finite range. Invalid input remains available; Cancel width or Escape within the width field discards only that unfinished width. A dialog dismissal with invalid width is rejected. A width changed by another owner while a dirty buffer exists is a conflict, not a silent overwrite. Cancel width loads the new value.

Filtering the chooser does not change grid filters or discard unfinished width text, even if the selected column is no longer in the search result. Changing selection first validates that text. Last-visible-column rejection restores the actual checkbox state. Unchanged width/visibility/pin actions skip layout publication and host validation. Layout changes are not cell-edit undo items; prior successful changes are not rolled back when the dialog closes.

## Ownership and integration

`ShowColumnChooserAsync` validates pending cell edits before opening and before a changed write. It rejects concurrent row forms, another chooser and active data-update scopes. `ActiveColumnChooser` exposes the current control to the host. Normal native focus uses the same browser keyboard handoff as row forms. Table replacement or grid unloading disposes the chooser before hiding it, so an old LostFocus cannot post into a replaced layout. The dialog follows the grid's requested theme. `CloseColumnChooser()` explicitly dismisses and discards unfinished width text; a host disposing an attached grid should close its owned dialogs first.

Standalone owners may call `Refresh()` while unloaded; loaded choosers track `GridLayout.Changed` automatically. `Unloaded` detaches the external subscription and stops debounce, and `Dispose` is idempotent. Application validation callbacks are synchronous, trusted and non-reentrant, matching the existing ownership rules. They must not replace the chooser's layout or modify it within validation.

## Work bounds and evidence

Metadata/result storage is O(columns), not O(records). A changed search scans metadata after a 160 ms debounce; native row realization remains viewport-bounded. Equal searches and equal layout revisions are no-ops. Width/pin changes update existing entries without replacing the result list. Visibility, order and band changes can rescan matching metadata; this is not an incrementally indexed remote metadata source. No records are enumerated by chooser search or refresh.

Sixteen Uno API cases cover exact/no-op widths, malformed/nonfinite/range rejection, pending conflicts, host validation, last-visible state, caption/field/band searches, scopes, identity retention, ordering, disposal and 10,000 metadata entries. Twenty browser cases use actual native input, 10,000 columns, 100,000 rows, pointer/keyboard/scopes, error recovery, owner replacement, white/dark narrow screenshots and repeated open/close. Read-only snapshots enumerate realized presenters, never force layout or realize containers for an assertion. Cases also execute the twelve shared filter-engine contracts in WASM and replay a long flat filter through the actual history menu. Inventory counts are not proof that an unfinished CI run passed.

## Filter-engine review

The old grouped predicate used a per-record capturing `All`/`Any` delegate. Compiled groups now use ordered short-circuit loops, compile every child once in original order, and return the child directly for one-term groups. A warmed allocation regression permits at most 4 KiB for 20,000 group invocations, independent of wall-clock speed. It does not claim field getters, conversions or arbitrary application predicates are allocation-free.

Ordinary textual AND/OR chains now produce shallow read-only groups instead of a left-deep tree. A 128-term AND or 256-term OR can pass the existing snapshot depth guard; precedence, explicit parenthesized groups, NOT and evaluation order remain intact. Complexity and text limits are unchanged. Existing saved nested criteria continue to work, but structural history identity can differ between a newly parsed flat chain and an older nested tree. No general logical-equivalence normalization is attempted.

Current inventory after this increment: **423 shared contracts**, **321 browser checks in nineteen interaction harnesses**, **110 Uno API contracts in ten suites invoked by nine harnesses**, eight Node runner/deployment tests and a separate fourteen-step deployment smoke (twenty total browser commands). Complete exact-head .NET/WASM/browser validation, not an inventory or a compile alone, is the merge gate. Browser graphics remain SwiftShader correctness evidence, not hardware GPU or end-to-end latency measurements.

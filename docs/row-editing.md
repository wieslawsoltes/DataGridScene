# Transactional row edit forms

Choose **Edit selected row…** directly below the Editing example to open a cached popup
form for the focused record (or the first filtered record when nothing is focused).
This command does not change the current example, rendering mode, query or selection.
It works with both the ordinary Skia cells and Native cells. Existing F2/double-click
in-place editing remains unchanged.

The form contains native controls, **Update** and **Cancel**. Ctrl+Enter updates the
row; Escape cancels. Moving focus between fields never commits individual changes.
The browser popup reuses the native cell keyboard-owner handoff for text-to-control
transitions without hiding the accessibility entry or focusing an aria-hidden canvas.

## Independent core buffer

```csharp
using var row = new GridRowEditBuffer(table, history, recordId,
    fields: new[] { "Quantity", "Limit" });
row.SetValue("Quantity", "8");
row.SetValue("Limit", "9");
if (!row.TryCommit())
{
    // Inspect row.Errors (field and complete-row errors) and row.Error.
    // Invalid raw input remains staged for correction or cancellation.
}
```

`GridRowEditBuffer` belongs to the Core package and has no Uno dependency. It snapshots
one record identity/version and the requested field values, in owned field order.
It accepts raw staged values without touching the table or invoking validators. Update
uses the existing `EditHistory.Execute`/`GridTable.Apply` path: convert and validate all
candidate fields and the complete proposed row before publishing any of those edits.
A successful changed row increments its version once, emits one table edit notification,
and creates one undo item. Undo/redo applies the row's complete change set together.

An unchanged row creates no revision, notification or undo entry. Even an unchanged
form checks the captured identity and version. A concurrent write to any field of that
record rejects Update; deletion/reinsertion with the same numeric ID is a different
identity and cannot receive old form values. Changes to other records do not conflict.
There is no implicit merge or force-overwrite option: cancel/reopen to see current data.

`Errors` is an owned read-only list. Field names are case-insensitive; duplicate, unknown,
or empty field lists and mismatched history ownership are rejected. Read-only/computed
columns cannot be staged. A host `canEdit` callback is checked when staging and committing.
Reentrant commits are rejected. Dispose cancels and releases staged value references.
Like the table and history, use the buffer on the owning single-writer context. Application
validators and table/history event handlers must not mutate the table reentrantly or
throw after publication; this follows the existing table/history contract, not a new
cross-thread or distributed transaction guarantee.

## Reusable Uno form and grid integration

```csharp
// Popup integrated with the grid's table, history and lifecycle:
bool updated = await grid.ShowRowEditAsync(recordId);

// A selected field order can include columns hidden in the grid:
bool partialRow = await grid.ShowRowEditAsync(recordId,
    new[] { "Company", "Quantity", "Status" });

// Standalone form for a host-provided surface:
var buffer = new GridRowEditBuffer(table, history, recordId);
var form = new RowEditForm(buffer);
// Host Update invokes form.TryCommit(); host Cancel disposes the form.
```

The popup commits or rejects an outstanding in-place cell edit before opening. A grid
read-only transition disables Update and makes its inputs read-only without replacing
typed text. Replacing the bound table/history or unloading the grid closes the popup
without applying its staged edits. Ordinary query/selection changes do not retarget the
form to a different record. Event subscriptions and a pending browser focus handoff are
removed when the popup closes. A second open request does not allocate a second form.

`RowEditForm` owns the supplied buffer's lifetime; disposing it cancels an unfinished edit.
Its `Editors` and `NullToggles` dictionaries expose the native controls without exposing
mutable dictionary storage. `RefreshReadOnly` applies host editability changes without
rebinding values. A standalone host supplies the containing dialog, keyboard-host
integration and lifecycle; the grid popup provides these through `ShowRowEditAsync`.

## Exact native values and validation

Strings/numbers/other convertible scalar fields use TextBox, booleans use a two- or
three-state CheckBox, and string choices use ComboBox. Read-only fields remain visible.
Optional non-boolean fields have an explicit **Null** checkbox, so a nullable string can
be intentionally changed from null to empty or back without relying on ambiguous text.

Editable text is not a formatted display value: decimals and integral types never pass
through double, and date/time inputs use round-trip text including subsecond precision
and the original offset. The schema's culture performs user-input conversion. If text
has not changed, Update passes the original typed value, not reparsed display text.
Update reads actual native control properties rather than trusting possibly deferred
TextChanged notifications. Field errors appear beside the corresponding controls and
complete-row/concurrency errors remain visible below the form. Failed validation keeps
the dialog and all input open for correction; Cancel does not write or clear undo history.

## Bounded work and current boundaries

The buffer is proportional to the selected field count, not the source row count. The
form creates one native editor per requested field only while open. It uses a scrolling
field viewport and rejects more than 256 fields rather than building an unbounded visual
tree. This is not field virtualization. Opening it does not realize another grid viewport
or rebuild retained native cell hosts. Browser tests check those operation counts with
100,000 records; this is not a hardware GPU or input-latency benchmark.

Implemented scope is **cached popup editing of an existing row**. Inline/inline-hide-row
forms, immediate posting, new-item rows, insert/delete undo, arbitrary mask/editor factories,
full vendor event/API parity and remote asynchronous commits remain separate work.
Mutable reference-valued model objects are not deep-cloned. Use scalar values or immutable
application values; mutating an object retrieved from the buffer is not a staged property
edit. Boolean keyboard focus and popup behavior depend on the underlying Uno host.

## Validation

`RowEditValidation.Run` adds 16 shared .NET/WASM contracts. `RowEditControlChecks.Run`
adds six real Uno control contracts. `tests/browser/row-editing.mjs` adds 17 browser checks
for Skia/native atomic Update/Cancel and undo/redo, field/row validation, read-only state,
concurrent/replaced records, table replacement, exact localized values, null/empty,
keyboard commands and 100,000-row bounded work. Diagnostics only read real existing
control properties and geometry; they never focus, scroll, realize or repair controls.

All existing suites remain enabled. The combined inventory is 375 shared contracts,
250 checks in 15 browser harnesses and 48 embedded Uno API contracts. Inventory is not
evidence that an unfinished run passed; consult the exact commit's Actions reports.

Behavioral reference: [DevExpress WPF Edit Form](https://docs.devexpress.com/WPF/403491/controls-and-libraries/data-grid/data-editing-and-validation/modify-cell-values/edit-form).
Host API reference: [WinUI ContentDialog](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs).
This implementation uses independent APIs and code, not proprietary source or assets.

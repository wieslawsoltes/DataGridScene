# Native controls and templates inside cells

## Scope

A column can host ordinary Uno `FrameworkElement` content instead of a canvas-only presentation. Both code-first factories and `DataTemplate` content are supported, including composite panels, data binding, buttons, native popups and application controls. `GridCellTemplates` supplies native TextBox, NumberBox, CheckBox, ComboBox, Button, ProgressBar, Slider and CalendarDatePicker adapters. These are actual Uno controls, not a drawing that imitates a control.

The public behavioral reference is DevExpress WPF's [Assign Editors to Cells](https://docs.devexpress.com/WPF/401011/controls-and-libraries/data-grid/data-editing-and-validation/modify-cell-values/assign-an-editor-to-a-cell): custom cell content and separate display/edit templates. DataGridScene uses its own API, data buffer and Uno implementation. This does not establish full DevExpress editor, API, template, theme or keyboard parity.

## Application API

```csharp
using DataGridScene.Controls;

// These controls are visible and interactive in every realized cell of their column.
grid.SetCellTemplate("Company", GridCellTemplates.TextBox());
grid.SetCellTemplate("Verified", GridCellTemplates.CheckBox());
grid.SetCellTemplate("Status", GridCellTemplates.ComboBox(statuses));
grid.SetCellTemplate("Quantity", GridCellTemplates.NumberBox(1, 100000));

// A display control can be replaced with another control only for the active edit.
grid.SetCellTemplate("Progress", GridCellTemplates.ProgressBar());
grid.SetCellEditTemplate("Progress", GridCellTemplates.Slider());

// Without a display template the column keeps the fast canvas representation.
grid.SetCellEditTemplate("OrderDate", GridCellTemplates.CalendarDatePicker());
grid.BeginEdit(recordId, "OrderDate");
```

`SetCellDataTemplate` and `SetCellEditDataTemplate` accept ordinary Uno DataTemplates. Their inherited DataContext is a `GridCellEditBuffer` exposing `Value`, `Record`, `RecordId`, `Column`, `Field`, `Culture`, `CanEdit`, `IsDirty`, `Error` and `IsActive`. For editable binding, bind the editor value to `Value`, not directly to the stored record. Changes are staged until `TryCommit` or the grid's normal commit path validates and records them.

A factory-based `GridCellTemplate` has `Create`, optional `Bind`, `Flush`, `Unbind` and `Release` callbacks. `Create` must return a fresh unparented element. `Bind` updates a visual from its current buffer; it may run more than once and must preserve the buffer's pending input. `Flush` transfers internal editor text into the buffer before committing. `Unbind` removes cell-specific subscriptions. `Release` releases application resources when an element leaves the recycling pool. Callbacks are trusted UI-thread application code, not sandboxed.

The **Native controls** sample demonstrates a composite DataTemplate containing an ordinary bound TextBlock and Button. Its action reads the current inherited buffer rather than capturing the original record ID when the control was created.

## Virtualization, clipping and lifetime

Only visible, intersecting data cells are realized. Group rows, detail rows, group footers and totals do not acquire native cell controls. Full cell dimensions and a separate clipping rectangle keep controls aligned through partially scrolled rows and frozen panes. Ordinary cells continue to use the Skia renderer. Hosted cells skip the renderer's duplicate value drawing while retaining its selection and grid-line rendering.

Visuals are recycled by template identity. The inactive pool is capped at 128 visuals across templates; active visuals and retained invalid edits are capped at 4,096. Normal viewports realize much less. A new buffer is assigned for every recycled identity. The old buffer is deactivated before unbinding, so late events cannot write to a different row. Empty template configuration incurs no per-record realization work. Unchanged paints do not recreate, reposition or rebind cell controls; external row-version or read-only changes refresh affected realized bindings.

Counters expose `RealizedCellControlCount`, `PooledCellControlCount`, `CreatedCellControlCount`, `ReusedCellControlCount`, `CellControlBindCount` and `CellControlPlacementCount`. `GetCellControl` returns an already-realized element only; it does not materialize an offscreen record. GetNativeCells provides a viewport snapshot for diagnostics.

Assign `null` to a template to return that column to the canvas. `ClearCellTemplates` commits or rejects pending edits before clearing both display and edit registrations. The sample clears its registrations when changing scenarios. Table replacement and control disposal clear live bindings and pools. Templates are application configuration and are not serialized as executable code in saved workspaces.

## Editing and native input

The buffer uses record identity and version checks, schema conversion/validation and the existing EditHistory. Untouched nulls and exact stored values remain unchanged. A removed/replaced record cannot receive a stale edit. Pristine buffers refresh after external writes; dirty buffers retain the pending input and report a version conflict instead of overwriting it.

Text and number inputs are staged. Checks and choices commit on activation; sliders commit on release. Grid operations commit first and refuse to scroll/filter away an invalid pending edit. Escape cancels pending input. Normal control keys are not reinterpreted as canvas-grid navigation; native text selection, popup input and focus traversal remain with the control. Grid Ctrl+Z/Ctrl+Y remain available when the grid itself has focus. Full virtualized tab navigation and every custom control's event policy are not claimed.

An unchanged NumberBox display does not write its rounded or clamped presentation back to the model; cancellation restores invalid text even when the underlying numeric value did not change. NumberBox and Slider are double-based native controls. Use a TextBox or a custom decimal/integer editor for values outside their exact representable range. CalendarDatePicker edits the date, not time-of-day. NumberBox's overwrite-invalid-input behavior is disabled so the grid can report validation rather than silently discard malformed text. See Microsoft's [NumberBoxValidationMode](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.numberboxvalidationmode) reference.

Externally initiated shaping may hide an invalid dirty cell. Its buffer is retained and its visual hidden until explicit cancellation or a successful commit; it is not silently recycled. Batches of independently edited cells are not a new atomic row-transaction feature. Arbitrary application actions must use `Invoke` to surface errors and must respect their own read-only semantics; `TryCommit` always enforces grid/schema read-only state.

Exports use model values and formatting, not a screenshot or serialization of arbitrary child UI. Native-cell templates do not add recursive editable master-detail, full accessibility providers, full editor masks, or seamless server-mode data to the grid.

## Validation

The shared suite contains 347 managed/raster contracts, including identity recycling, validation, version conflicts, cancellation, bound values and viewport/frozen-pane geometry. The 22-check native-cell browser suite exercises real controls and native input, and also runs 11 individual Uno API contracts. Existing suites remain enabled. Read the exact commit's CI results rather than treating this inventory as a pass claim.

Browser correctness uses software ANGLE/SwiftShader. Control count and identity assertions are bounded-work tests, not hardware GPU frame rate or end-to-end input-latency benchmarks. Native desktop compilation is not a complete Windows/macOS/Linux runtime matrix.

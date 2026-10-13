# Custom native row editors

**Custom row editors…** opens the existing cached row form with application-supplied native field editors. The sample includes a real `DataTemplate` for the customer, a composite exact-integer quantity editor with decrement/increment buttons, and a spanning multiline Notes editor. It uses the active table and selection in either Skia or native cell mode, not a separate implementation of the data grid.

The compact form uses side captions, the existing responsive field layout and Update/Cancel commands. Field failures have a stable-width error border and a theme-aware summary. These are independent visual/behavioral improvements, not a pixel-identical vendor theme or complete editor catalog.

## Field factory API

Factories belong to `RowEditFormOptions.EditorFactories`. Each visible configured field is constructed once per form. A null factory result selects the default editor. The form validates and copies the configuration before creating editors; unknown, duplicate (case-insensitive), and null factory entries are rejected. Hidden fields do not invoke factories.

```csharp
using DataGridScene.Controls;
using DataGridScene.Layout;

var options = new RowEditFormOptions
{
    Title = "Edit order",
    ShowInstructions = false,
    MaximumWidth = 960,
    MaximumColumnCount = 3,
    MinimumColumnWidth = 280,
    CaptionPlacement = RowEditCaptionPlacement.Beside,
    Fields =
    [
        new("Company") { Caption = "Customer", ColumnSpan = 2 },
        new("Notes") { StartNewRow = true, ColumnSpan = 3, RowSpan = 2 }
    ],
    EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>>
    {
        ["Notes"] = context => RowEditEditors.Text(context, multiline: true, minimumHeight: 88)
    }
};

bool saved = await grid.ShowRowEditAsync(recordId, fields: null, options: options);
```

Use `new RowEditForm(buffer, options)` for a standalone host. The host owns the form's dialog/surface and disposal. The old constructors and overloads keep the original default editors and single-column layout.

`RowEditEditorContext` provides record ID, canonical field name, caption, data type, nullable/read-only flags, initial value, culture and copied choices. It does not expose the live table, history or staged buffer. It is a metadata/value snapshot, not a generic deep clone of arbitrary application objects. Do not mutate objects referenced by an initial value or capture a live row and write it from an editor event.

## Ordinary controls and DataTemplates

A `RowEditEditor` combines a fresh `FrameworkElement` with three explicit operations: capture its current pending value, apply read-only state, and optionally release application subscriptions/resources. A composite may name a child `Control` as `FocusTarget`; it must belong to that editor tree. The form uses that target after a field failure rather than trying to focus a non-focusable layout panel.

```csharp
static RowEditEditor CreateCustomerEditor(RowEditEditorContext context)
{
    string initial = Convert.ToString(context.InitialValue, context.Culture) ?? "";
    var text = new Microsoft.UI.Xaml.Controls.TextBox { Text = initial };
    return new RowEditEditor(
        text,
        readValue: () => text.Text,
        setReadOnly: readOnly => text.IsReadOnly = readOnly);
}
```

`RowEditEditors.FromTemplate` calls `DataTemplate.LoadContent`, sets the root's DataContext to the field context and uses the supplied capture/read-only/release adapters. Bind to the context to initialize or label the editor; keep user input in the native controls until Update. The sample demonstrates a code-created DataTemplate. The capture adapter reads the actual native control value, not a potentially delayed change event or a binding that immediately posts to the model.

Templates must return new, unparented roots. A root already owned by another form is rejected **before** its DataContext is changed. An already-claimed editor adapter cannot be claimed again. Failed construction releases earlier acquired adapters but never invokes cleanup on the rejected editor belonging to another owner.

The default text helper preserves the original typed value when its text is untouched. This avoids converting exact decimal, Int64 and timestamp values through a double-based widget. Application adapters that intentionally use a NumberBox or Slider remain responsible for their double precision boundary. Custom readers may return raw text for the model's typed conversion/validation or a correctly typed object.

## Atomic capture, staging and commit

Update performs three distinct steps:

1. Refresh edit permissions, then capture each visible writable editor into a temporary field/value batch.
2. Atomically stage that complete batch using `GridRowEditBuffer.SetValues`.
3. Use the existing row transaction to validate identity/version, convert values, validate the complete row, apply changes and create at most one undo item.

A reader exception does not partially stage preceding readers. Their native pending input remains available, with a field error and no model or undo mutation. A subsequent successful capture retries normally. Conversion and complete-row validation failures likewise leave the model unchanged and retain the native input for correction.

The Core-only `SetValues` API consumes its input once, bounds the batch by the buffer's fields, checks all names/duplicates/read-only flags and rechecks permission after enumeration before its first staged write. Enumeration failures retain the preceding staged values and errors. It performs no type conversion or model writes itself. Nested staging, commit and disposal during a batch are rejected.

`SetValue` remains available for application staging. Hidden fields are not read from controls; their original or application-staged values still participate in row validation. Visibility is not an authorization or validation bypass.

Readers and adapters are trusted synchronous application code on the UI owner. They must not modify the live table or call back into the form. Reentrant form commit/disposal is guarded, but these contracts are not a sandbox for untrusted delegates. External writes still trigger the row buffer's optimistic identity/version checks.

## Nulls, keyboard input and validation

Nullable text/composite editors receive an explicit Null toggle by default. `UseNullToggle: false` lets an editor represent null itself, as the default three-state CheckBox does. Untouched null, empty string and multiline content remain distinct. A null toggle disables its value editor without discarding that editor's pending content.

`RowEditEditors.Text(..., multiline: true)` is supported only for string fields and uses a normal wrapping TextBox with `AcceptsReturn`. Plain Enter inserts a newline. The integrated form handles Ctrl+Enter in the preview key route, before an editor inserts another newline or consumes Enter, and commits through the same transaction as Update. Escape/Cancel discards unsaved values. A standalone host should provide its own commit/cancel commands around `TryCommit`.

`ValidationMessage` and `ValidationErrors` include form capture/permission errors as well as row-buffer validation failures; those are distinct from `Buffer.Error`, which only reports buffer commit failures. Error labels use live settings. The form keeps the same border thickness in valid/invalid states, preventing the editor's text area from shifting when the error color changes. This is not a complete screen-reader/provider certification.

## Lifetime and performance

Factories and DataTemplates are not reinvoked on resize, validation or an unchanged read-only state. Each successful permission state is cached per adapter. A callback that partially updates a control and then throws invalidates that cache; returning to the previous state must actually reapply it. Failed permission adaptation blocks Update until it recovers.

`EditorCreationCount`, `ReaderCaptureCount` and `ReadOnlyUpdateCount` complement the layout reflow/placement counters. Capturing occurs on Update, not on every redraw or key. Responsive packing still runs only when the column count changes and never recreates/rebinds the field controls. These counters measure work done by DataGridScene, not all internal Uno/driver operations.

Disposal releases acquired adapters in reverse order, at most once, and continues after an application's release throws. `CleanupErrors` retains those failures for a standalone owner to inspect. Cleanup is not interpreted as a failed database/model commit after an update has already succeeded. The sample removes its button handlers and diagnostic references when the form closes. Rejected foreign roots are not disposed by the new form.

As with ordinary retained controls, an application that holds a closed form or its exposed `Editors` retains that visual tree. Release those references when no longer needed. A release callback is for resources/subscriptions owned by that editor; it must not dispose another form's control.

## Review and validation

The custom-editor harness has 16 browser checks. It invokes ten shared atomic-staging contracts and sixteen real Uno API contracts, and tests native DataTemplates/composites, both cell modes, one-step undo, multiline keyboard input, reader failure, null/empty values, read-only transitions, exact localized values, owner replacement, responsive identities and 100,000-record bounded work. All existing suites remain enabled.

Public deployment review found that the old smoke script requested a theme button before its newly selected native ribbon finished layout. The shared `clickChromeAction` helper now waits for the selected surface and actual nonzero on-screen bounds before using real pointer input. It does not force layout, invoke actions, repair controls or remove theme assertions. The identical smoke verifier also runs against the exact CI artifact before merge; its report labels that as `local-artifact`, not public deployment verification. Public HTTPS/commit verification remains a separate post-deploy gate.

See [validation](validation.md) for total inventory and the exact Actions result for pass/fail evidence. Hosted browser graphics use ANGLE/SwiftShader. Desktop compilation, operation counts and screenshots do not establish full native runtime coverage, hardware throughput or pixel parity.

## Remaining boundaries

These APIs cover cached popup editing of existing materialized records. They do not implement inline/immediate-post/new-item forms, whole-form DataTemplate replacement, all mask/editor types, asynchronous remote updates, dependency-aware editor-to-editor value notifications, insertion/deletion undo or complete accessible grid providers. Application factories are configuration, not saved workspace delegates.

The behavioral/design reference is the public [DevExpress WPF Edit Form](https://docs.devexpress.com/WPF/403491/controls-and-libraries/data-grid/data-editing-and-validation/modify-cell-values/edit-form) and [column edit template](https://docs.devexpress.com/WPF/DevExpress.Xpf.Grid.ColumnBase.EditFormTemplate) documentation. The implementation and APIs are independent; no vendor source, branding or proprietary assets are used.

# Responsive row edit form layout

The **Row form layout…** command uses the same transactional row editor as **Edit selected row…**, with configurable field order, captions, visibility, row starts and spans. Wide windows show up to three columns; narrower windows reduce the column count without replacing the editors. The ordinary one-column form and its existing overloads remain supported.

## Reusable control API

```csharp
using DataGridScene.Controls;
using DataGridScene.Layout;

var options = new RowEditFormOptions
{
    Title = "Edit order",
    MaximumColumnCount = 3,
    MinimumColumnWidth = 280,
    MaximumWidth = 960,
    CaptionPlacement = RowEditCaptionPlacement.Beside,
    Fields =
    [
        new("Company") { Caption = "Customer", Order = -10, ColumnSpan = 2 },
        new("Quantity") { Caption = "Units", Order = 10 },
        new("Notes") { Order = 100, StartNewRow = true, ColumnSpan = 3, RowSpan = 2 }
    ]
};
bool saved = await grid.ShowRowEditAsync(recordId, fields: null, options: options);
```

Fields not mentioned in `Fields` keep their source order, caption and visibility. `Order` defaults to zero; ties keep source order. Overrides must name fields in the row buffer. Unknown fields, case-insensitive duplicates, invalid spans and invalid width/height options are rejected before editor construction. Canonical model field names are preserved. Configuration is copied into immutable layout metadata when the form is constructed, so later changes to a caller's list do not change an open form.

`CaptionPlacement.Above` preserves the original presentation. `Beside` uses a configurable `CaptionWidth`, labels aligned alongside native controls, and field validation messages below the editor. Row/column spans resize the field container; they do not change the editor type, enable multiline editing or replace text editing with a less precise numeric/date control.

`MaximumFieldHeight` bounds the scrolling field area. The integrated dialog respects the window size and the configured maximum width; its resize subscription is removed on close. Standalone hosts can construct `new RowEditForm(buffer, options)` and supply their own containing surface/width and lifecycle/keyboard integration. `MaxWidth` is a limit rather than an unconditional width request in a standalone host.

## Independent layout package

`RowEditLayout` lives in `DataGridScene.Layout`, with no Uno or Skia dependency. It consumes field names and presentation metadata, not model values, getters or row counts.

```csharp
var layout = new RowEditLayout(
    new[] { "Name", "Quantity", "Notes" },
    new[] { new RowEditFieldLayout("Notes") { StartNewRow = true, ColumnSpan = 3 } },
    maximumColumnCount: 3,
    minimumColumnWidth: 220);
RowEditLayoutPlan plan = layout.Arrange(800);
```

A plan contains the actual column count, row count, and each visible field's row, column and spans. Declared column spans clamp to the available column count on narrow surfaces. Row spans reserve their occupied cells. Placement proceeds forward in logical field order and does not backfill earlier holes, keeping reading/tab order stable. `StartNewRow` starts the field search on a new row unless it is already at a row boundary. All-hidden configurations produce an empty plan.

Limits are explicit: one to four columns, at most 256 field names, column spans one to four, and row spans one to eight. Widths/spacings must be finite and within the documented constructor bounds. A width of zero still produces a one-column plan; a non-finite or negative width is rejected.

## Retained editing and bounded work

`RowEditForm` creates each visible editor once and keeps it in the same field container. The actual field viewport width determines the responsive column count. A width change that remains in the same column-count range does not call the packing algorithm or rewrite row/column properties. Crossing a threshold rebuilds only grid definitions and changes the necessary placement properties. No editor is recreated, rebound, or refocused by the layout routine.

`LayoutReflowCount` and `FieldPlacementWriteCount` expose that work; `LayoutPlan` is the last applied plan, not a request to force layout. `FieldPanels` exposes existing field containers for hosting/diagnostics. The browser tests compare the actual native editor identities across three, one, two and three columns, preserving unsaved text and invalid values. They also check that sub-threshold resizing leaves placement counters unchanged.

The form still operates on one record, even for a 100,000-record grid. Resizing the form does not reshape the grid or execute record getters. The host/window may independently relayout the underlying grid during a window resize; this is not a claim that Uno performs zero internal layout work.

## Transaction and visibility semantics

The existing row buffer still applies Update as one validated transaction and one undo item. Cancel and Escape do not save. Width changes do not post edits. Read-only/concurrency checks, exact decimal/Int64/timestamp preservation, explicit null values and complete-row validation remain unchanged.

Hidden fields do not acquire editors and are not flushed by the form. Their original or caller-prestaged values remain in the row buffer and still participate in the transaction's validation. Hiding a field is presentation, not permission enforcement or a way to suppress a validation error. A hidden invalid prestaged value produces a form-level error; the host must correct the buffer or reopen with that field available.

Layout options are application configuration, not serialized executable templates or a new grid workspace format. This increment does not add inline/immediate-post forms, new-row creation, insertion/deletion undo, arbitrary editor templates/masks, asynchronous remote commits, field virtualization, or complete accessibility/pixel parity.

## Validation and reference

Ten shared layout contracts run on .NET and WASM, including randomized non-overlap/bounds, stable order, ownership, span collapse and invalid configuration. Six additional real Uno API contracts cover actual controls, hidden-field transactions, captions and validation. Twelve browser checks exercise both rendering modes, atomic undo, real panel geometry, native editor identity across resizing, rejected input, logical keyboard order, bounded work and the actual dark spanning-field sample. Existing suites remain enabled.

Counts are test inventory; see the exact head/main Actions reports for execution results. Hosted graphics use ANGLE/SwiftShader, not a hardware GPU benchmark. The reference is the public [DevExpress WPF Edit Form documentation](https://docs.devexpress.com/WPF/403491/controls-and-libraries/data-grid/data-editing-and-validation/modify-cell-values/edit-form). The APIs and implementation are independent; no vendor source or proprietary visual assets are included.

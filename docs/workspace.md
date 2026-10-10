# Bands and workspace state

## Nested bands

```csharp
using DataGridScene.Core;
using DataGridScene.Layout;

grid.ShowBands = true;
grid.ColumnLayout!.SetBandPath("Company", "Customer", "Contact");
grid.ColumnLayout.SetBandPath("City", "Customer", "Location");
grid.ColumnLayout.SetBandPath("Region", "Customer", "Location");

string customer = GridLayout.BandKey(new[] { "Customer" });
grid.ColumnLayout.SetBandPin(customer, ColumnPin.Left);
grid.ColumnLayout.SetBandWidth(customer, 640);
```

Band paths have 1–16 nonempty captions, each at most 256 characters. Keys escape individual captions. Use `BandKey` rather than concatenating captions containing `/`, `%` or other reserved characters.

`FieldsInBand` returns descendants. `SetBandVisible` and `SetBandPin` update the entire band in one layout notification. Hiding the last visible column is rejected. `MoveBandBefore` reorders siblings while retaining descendant order; it does not implicitly reparent. `SetBandPath` performs explicit reparenting.

The renderer can show separate fragments when a band is split by other bands or pinned panes. A whole-band operation applies to its descendants across fragments. This release has nested **header rows**, not multiple data-cell rows within a record and not merged data cells.

## Selection and expansion

```csharp
grid.SelectionMode = GridSelectionMode.Cells;
grid.Selection.Select(grid.View!, new CellAddress(10001, "Company"));
grid.Selection.Select(grid.View!, new CellAddress(10003, "Region"),
    extend: true,
    columnOrder: grid.ColumnLayout!.DisplayColumns()
        .Select(column => column.Field).ToArray());

var selection = grid.Selection.CaptureState();
var expansion = grid.View!.CaptureState();

// Restore against the current table/view; missing IDs are omitted.
grid.View.RestoreState(expansion);
grid.Selection.RestoreState(grid.View, selection);
```

Selection snapshots retain the mode, selected rows or compressed cell blocks, focused cell and range anchor. Restoring the anchor preserves subsequent Shift-key range extension. Expansion snapshots retain collapsed group paths and expanded detail IDs.

## Save and restore the control

```csharp
string json = grid.SaveLayout();
grid.RestoreLayout(json);

// Omit optional selection data for a very large sparse selection.
string configurationOnly = grid.SaveLayout(includeSelection: false);
```

Version 2 records column state and band paths, the base query, separate auto-filter-row text, display options, selection mode, custom row heights, group/detail expansion, scroll offsets, and optional selection state. Search/sort/group/summary descriptors remain in the query.

Separating base criteria from auto-filter-row values matters: restoring an `Alpine` filter and changing it to `Blue` replaces that field's old filter rather than ANDing both values permanently. The control recombines them when shaping the view.

`LayoutSerializer.Load` accepts DataGridScene version 1 and version 2 JSON. Version 1 lacks the newer fields and uses defaults; missing saved columns receive current schema defaults. Unknown saved columns are ignored. Unsupported future versions, null collections, invalid widths/offsets/row heights and excessive filter complexity are rejected.

The format is **not DevExpress XML**. It is not a data export, undo-history format or serializer for arbitrary application delegates/templates. The 1 MiB JSON limit protects imports; two million selected cells and other documented collection limits are checked before mutation. Save without selection when its optional payload exceeds that limit.

Restoring a control replaces its view/layout instances after validation; retain the `GridControl` reference and use its `StateChanged` event rather than retaining an old `GridView` subscription. Custom selection restoration can also be used separately without replacing the control's view.

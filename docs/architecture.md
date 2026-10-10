# Architecture

## Ownership and dependencies

`Core` is the single-writer data model. `Data`, `Layout` and `Export` depend on it. `Rendering.Skia` depends on Core/Layout plus SkiaSharp/HarfBuzz; it accepts a caller-owned canvas. `Controls` composes these libraries with Uno. The demo supplies synthetic orders, menus and a JavaScript diagnostic bridge.

A `GridTable` owns normalized records with stable `long` identities and record versions. A `GridView` shapes those records into data, group and detail rows. `GridSelection` stores identities rather than visual indexes. `GridLayout` owns visual column state independently of immutable schema metadata. A `GridFrame` is the geometry snapshot consumed by painting, pointer hit testing and editor placement.

The view currently materializes local filtering/sorting/grouping synchronously. Visual virtualization does not imply remote data virtualization: the independently reusable page cache is not yet a seamless server-mode adapter for the control.

## Rendering and layout

The Uno surface derives from `SKCanvasElement`; drawing targets its supplied `SKCanvas`, not a bitmap of an HTML table. Native Uno editors are placed above the surface only when editing. HarfBuzz-shaped runs are cached with a fixed entry limit and disposed on eviction. The sample loads an existing Uno package typeface explicitly rather than assuming browser system-font lookup succeeds.

Rows use a Fenwick-tree height index for prefix offsets and inverse offset lookup. Default-height initialization is linear; individual height updates and offset lookup are logarithmic. Rebuilding a view still rebuilds row geometry.

Column layout builds separate cached left/middle/right panes. Each stores cumulative widths. A frame binary-searches the first intersecting column and enumerates only intersecting columns. Band runs are cached per header level and pane and queried in the same way. Structural changes, pinning, hiding and width changes rebuild these indexes; scrolling does not.

Band paths are caption sequences. Prefixes identify parent bands, with escaped keys that distinguish a literal slash in a caption from a path separator. Only adjacent compatible columns in the same pane share a rendered header rectangle. A leaf header spans unused lower header rows. Moving a whole band preserves its descendants' order; reparenting is explicit through `SetBandPath`.

The proportional band resizer preserves the original pointer-down width weights and distributes the requested total subject to each column's limits. Shrinking to a clamp and expanding again does not accumulate rounding/drift from successive pointer moves.

## Editing and clipboard

`GridTable.Apply` stages conversions and validation for every requested cell before changing any record. `EditTransaction` checks record versions and identity replacement. `EditHistory` stores successful cell changes, supports undo/redo, and invalidates history after external table writes.

Clipboard handling uses displayed column order and hierarchical visible row order. Sparse cell selection preserves blank holes inside the selected rectangle. Scalar paste can fill selected cells. Range paste fails rather than silently truncating rows/columns or writing read-only fields. CSV export enables spreadsheet-formula protection by default; clipboard transfer deliberately preserves the selected raw values for application-to-application editing.

Record insertion/deletion is not part of the cell-edit undo stack. Native editors currently cover text, convertible scalar values and choice lists, not the complete DevExpress editor/mask ecosystem.

## State and lifetime

Workspace restoration validates JSON and geometry, constructs a replacement layout and view against actual table data, and stages expansion/selection checks before swapping live control state. Invalid restoration leaves the existing view/layout/selection intact, except that a previously active edit must first commit successfully.

Restoration replaces the control's `View` and `ColumnLayout` objects. Subscribe to control-level `StateChanged` for a stable subscription across restoration rather than retaining subscriptions to an old view.

Expansion and selection snapshots use stable group paths and record IDs. Missing identities can be dropped on restoration when data/schema evolve. Cell blocks share identical field sets to keep large rectangular snapshots compact. Limits are explicit; oversized state is rejected, not silently truncated.

The model is single-writer. Callers must marshal table/control mutations to the owning UI context. Page-source cancellation is independently tested, but it does not make table or visual mutations thread-safe.

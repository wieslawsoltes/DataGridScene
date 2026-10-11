# Compatibility matrix

## Reference and meaning of status

The behavioral reference is the public [DevExpress WPF GridControl documentation](https://docs.devexpress.com/WPF/6084/controls-and-libraries/data-grid), including [bands](https://docs.devexpress.com/WPF/15660/controls-and-libraries/data-grid/grid-view-data-layout/bands). This project implements independent APIs and code. “Implemented” below means the listed DataGridScene behavior exists, not that every DevExpress option, event, visual state or edge case has parity.

There is **no complete functional, API, visual, accessibility or performance parity certification**. The browser demo is an implementation preview, not a pixel-identical reproduction or production-readiness guarantee.

| Area | Implemented in this repository | Remaining boundaries |
|---|---|---|
| Local data | Typed schema, stable IDs, value getters, local shaping, trim-safe object projection. | Full `ICollectionView`/binding-source semantics, live/incremental shaping, every provider integration. |
| Sorting/search/filtering | Stable multi-sort, cross-column search, field filter row, AND/OR/NOT criteria, comparisons, IN/BETWEEN/null/string operations, visual filter builder. | Complete criteria language/function catalog, Excel-style multi-select filter UX, all locale/custom comparison edge cases. |
| Grouping | Nested groups, date/value/first-letter intervals, collapse/expand, group-path persistence, per-level multi-summary sorting and configurable group footers. | Full group-row templates, custom grouping identity policies and all group selection/navigation options. |
| Summaries | Independent totals/groups; count/sum/average/minimum/maximum/distinct; custom accumulator factories; compensated floating sums; inline or aligned summaries; multiple target-column lines; staged Uno summary editor. | Built-in running/window functions, full formatting/template parity, visual editing of secondary sort criteria, and asynchronous/incremental aggregate execution. |
| Selection | Single row, multiple rows, sparse cells, rectangles, mouse drag, keyboard range, identity persistence and compressed snapshots. | Every DevExpress selection mode/event/drag-scroll behavior and UI automation selection provider. |
| Editing | Native text/choice editors, conversion, required/custom row and cell validation, optimistic record versions, atomic batch paste, cell undo/redo. | Full mask/editor catalog, row edit forms, new-item row, insertion/deletion undo, all event and validation timing compatibility. |
| Columns and bands | Reorder/resize/hide/pin, chooser, best fit, nested shared band headers, leaf-header spans, whole-band operations, indexed viewport lookup. | Multiple data-cell rows per record, arbitrary cell spanning/merging, complete band customization UI and template parity. |
| Virtualization | Variable-height row indexing; clipped row/column drawing; cached left/middle/right column and band geometry. | Incremental view shaping and full remote/server-mode integration in the control; all heterogeneous automatic measurement modes. |
| Master-detail | Expanded detail rows and a custom detail-data renderer. | Recursive independently editable child grids, full relation descriptors, async detail lifecycle and templates. |
| Remote data | Independent local/HTTP page source contract, LRU cache, coalescing, cancellation and stale-query suppression. | The visual control still uses a materialized `GridTable`; no seamless server-mode grid, server group rows or remote editing. |
| Conditional presentation | Programmatic style rules, badges, checkboxes, progress bars and sparklines. | Complete rule editor, icon sets, color scales, data bars and expression-backed formatting parity. |
| Clipboard/export | Display-aware TSV, sparse cells, scalar fill, atomic paste, CSV, typed XLSX, printable HTML. | Group-header/footer export formatting, PDF/XPS/RTF exports, full print-preview/pagination engine, merged/banded export layout parity and all Excel formatting options. |
| Workspace state | Versioned JSON, v1 migration, bands, base/field filters, sort/group/summary, selection/focus/anchor, expansion, offsets and custom row heights. | DevExpress XML/API compatibility, arbitrary delegates/templates and undo history; every host-specific state option. |
| Rendering | Skia canvas, shaped text cache, viewport clipping, light/dark grid palettes, native editor overlays. | Pixel-for-pixel theme parity, complete multilingual font fallback/bidi, all DPI/driver cases, independent WebGPU renderer and measured hardware benchmarks. |
| Accessibility | Keyboard navigation, named grid/editor elements, native editors. | Full table/grid/selection/value/scroll automation providers, virtualized assistive-technology tree, verified screen-reader matrix. |
| Platforms | Shared Uno desktop/browser source; desktop compilation and browser interactions in CI. | Comprehensive runtime validation on Windows/macOS/Linux, Firefox/WebKit, actual mobile devices and hardware graphics configurations. |
| Packaging | Six NuGet packages built by CI; separate core/rendering/control/export assemblies. | nuget.org publication, stable compatibility policy, complete API documentation and broad independent consumer matrix. |

## Validation is narrower than product parity

The shared contracts cover both positive and rejected operations. Browser tests cover genuine interactions and exported files. They do not execute a licensed DevExpress build side-by-side, and screenshots are evidence of this implementation rather than pixel-diff parity baselines.

Hosted GPU tests use Chromium's software graphics backend. A low CPU paint number is not an end-to-end frame-rate, input-latency or hardware-performance claim. See [validation](validation.md).

## Priority follow-up areas

The largest remaining gaps are complete accessible grid providers, seamless asynchronous/server-mode data, recursive editable master-detail, advanced editors/new-item and row transactions, multi-row/cell-merged layout, and data-aware printing. Each should have independent package contracts plus actual browser interaction coverage before being marked implemented.

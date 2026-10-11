# Command chrome and filter panel

## Visual reference and scope

The reference is the public [DevExpress WPF demo screenshot](https://docs.devexpress.com/WPF/images/open-wpf-demo-project-from-demo-module%402x.png) and [WPF filter-panel documentation](https://docs.devexpress.com/WPF/6409/controls-and-libraries/data-grid/visual-elements/common-elements/filter-panel), reviewed on October 11, 2026. This implementation adopts their general compact title/tab strip, grouped command ribbon, flat grid headers, restrained selection colors and bottom filter strip. It uses independent code and vector geometry rather than vendor source, branding, icon assets or font files.

The sample has Demo/Themes tabs, 18 ribbon commands, searchable examples, previous/next navigation, full-width view, whole-shell white/dark themes and 22/28-pixel row density. Existing grid operations remain available through the ribbon and native menus. The options panel remains available alongside the grid and hides at narrow widths. All icons use fixed-size geometry rather than depending on icon-font loading.

This is **closer to the reference, not pixel-identical parity**. The demo uses its existing licensed Uno Open Sans dependency; native control templates, font metrics, popup details and theme variants differ. `CommandRibbon` is a focused reusable command surface, not an implementation of the complete DevExpress ribbon product. Complete key-tip navigation, quick-access customization, filter-history menus and a full visual-reference test matrix remain separate work.

## Reusable controls

```csharp
using DataGridScene.Controls;

var ribbon = new CommandRibbon();
ribbon.AddGroup("Layout",
    new RibbonCommand("save", "Save layout", SceneGlyph.Save, SaveLayout),
    new RibbonCommand("restore", "Restore layout", SceneGlyph.Restore, RestoreLayout));
ribbon.CommandFailed += (_, error) => ReportError(error);
ribbon.SetTheme(dark: false);
```

Each command is a native focusable button with an automation name and ID. Duplicate IDs are rejected before the group is changed. The horizontal scroll host keeps commands reachable at narrow widths; it does not silently remove overflow commands. The host decides how to surface `CommandFailed` and how to update command availability.

`VectorIcon` renders original stroke geometry into Uno's Skia surface. It retains its paint while loaded and releases it when unloaded. `IsDark` updates the icon without changing its logical size. It has no dependency on a grid or demo record schema.

`GridFilterPanel` can be embedded independently and supplied with `FilterPanelToken` records. It owns an immutable copy of the token list, rejects duplicate identities, and exposes enable/edit/remove/clear request events. The panel does not execute filter expressions itself. Reapplying equal token content or changing only the enabled state does not recreate token controls.

## Grid integration

```csharp
grid.SetColumnFilter("Region", "Europe");
grid.SetColumnValues("Company",
    new DataGridScene.Core.ColumnValueFilter(new object?[] { "Alpine Ski House" }));

// Keep the configured conditions, but temporarily show unfiltered records.
// The search panel remains active independently.
grid.IsFilterEnabled = false;
grid.IsFilterEnabled = true;

// Remove only this column's text or checked-value filter.
grid.RemoveFilterToken("column:Company");

// Clear filter conditions while preserving the current search.
grid.ClearFilterConditions();
```

The base expression is represented by the `criteria` token. A per-column condition uses `column:<canonical field>`, so a field literally named `criteria` cannot collide with the base expression. Complex base AND/OR/NOT structures are represented as one expression rather than being incorrectly split into removable conjuncts. The description is a bounded human-readable label, not executable criteria serialization.

The checkbox suspends the expression; it does not discard checked values or rewrite the conditions. Clicking a column token reopens that column's editor. Clicking the base token or Edit Filter raises `FilterEditorRequested`, which the sample handles by showing its native filter dialog. Keyboard input from the strip is not treated as grid navigation/editing input.

Filter changes that require a pending editor to commit are rejected while that editor is invalid. A control batch delays token updates until the outer scope finishes. Clearing the entire grid's filters through the older `ClearFilters` method still clears search too; `ClearFilterConditions` is the new narrower operation.

## Query, serialization and data sources

`GridQuery.IsFilterEnabled` defaults to true. The view still validates a suspended filter so an invalid expression cannot be hidden inside an apparently valid configuration. It omits that filter from execution and dependency tracking while suspended. Search, sorting, grouping and summaries continue to run over the resulting record set.

Saving suspended filters promotes the layout to **version 4**. Versions 1–3 remain readable with filters enabled, and a document declaring an older version while requesting suspension is rejected. This prevents older readers from silently applying a condition that the author disabled. Checked-value version 3 promotion remains unchanged for enabled filters. Layouts are DataGridScene JSON, not DevExpress XML.

The local page source honors suspension. A remote endpoint consuming `GridQuery` must explicitly implement `IsFilterEnabled` semantics; this is not automatic compatibility with a pre-existing endpoint. The visual control still uses a materialized table rather than seamless server-mode virtualization.

## Performance and correctness changes

The grid renderer retains four immutable disclosure/sort/funnel paths and disposes them with the renderer. These glyphs no longer allocate a new native path for every row/header on every paint. Ascending/descending sort arrows now point up/down rather than reusing the disclosure's right/down directions.

Hover updates only repaint when the hovered row/header identity changes. They do not reshape data, rebuild a viewport frame or rebuild filter tokens. Token descriptions are updated after filter mutations, not during pointer movement, selection or display-only data edits.

Best Fit measures all requested columns first, then publishes one layout. A failing getter leaves the existing widths unchanged. This also avoids building a separate layout/frame after every measured column. Restoring a workspace constructs its view directly with the destination query instead of first shaping an unnecessary unfiltered view.

The shared suite includes 18 new managed/raster contracts (331 total). The chrome browser harness adds 18 checks to the prior 100 and runs 12 new real Uno control API contracts in addition to the existing 10. Counts are inventory, not evidence that an unfinished run passed. See the exact commit's CI reports and the retained public deployment check.

These are bounded-work and correctness improvements, not hardware GPU performance or end-to-end input-latency claims. Browser CI uses ANGLE/SwiftShader. The renderer remains Skia on the host's graphics backend; no independent WebGPU implementation was added by this change.

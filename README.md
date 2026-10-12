# DataGridScene

[Browser demo](https://wieslawsoltes.github.io/DataGridScene/) · [Build and validation](https://github.com/wieslawsoltes/DataGridScene/actions) · [Compatibility matrix](docs/compatibility.md)

Independent .NET data-grid libraries, a reusable Uno Platform control, and a shared desktop/WebAssembly sample. The reference is the publicly documented DevExpress WPF GridControl. No DevExpress source, binaries, branding, or proprietary assets are included.

**This is a working preview, not a complete DevExpress replacement.** API compatibility, pixel-identical rendering, complete accessibility, and the remaining advanced features have not been established. The [compatibility matrix](docs/compatibility.md) distinguishes implemented behavior from incomplete scope.

## Packages

| Package | Responsibility |
|---|---|
| `DataGridScene.Core` | Typed records, criteria, sorting, grouping, summaries, version-checked cell/row edits, undo/redo, row/cell selection and identity-based snapshots. |
| `DataGridScene.Data` | Trim-safe object projection, local and HTTP page sources, bounded asynchronous page cache, cancellation and stale-query suppression. |
| `DataGridScene.Layout` | Variable-height row index, cached column prefix indexes, frozen panes, nested bands, and versioned workspace serialization. |
| `DataGridScene.Rendering.Skia` | Viewport-only drawing, shared paint/hit geometry, HarfBuzz text cache, badges, checkboxes, progress bars and sparklines. No Uno dependency. |
| `DataGridScene.Controls` | Reusable Uno grid, native in-place editors and staged row forms, keyboard/touch input, band manipulation, column chooser and visual filter builder. |
| `DataGridScene.Export` | CSV, TSV clipboard, printable HTML and typed XLSX export. No Uno dependency. |

The sample references these packages; the core libraries do not reference the sample. NuGet packages are produced as CI artifacts. They have **not** been published to nuget.org.

## Build and run

The repository pins Uno.Sdk in `global.json` and targets .NET 10. Node 22+ is used only for browser testing and static-site preparation.

```sh
dotnet run --project tests/DataGridScene.Tests -c Release

# Desktop (install the native dependencies required by your Uno desktop host)
dotnet run --project samples/DataGridScene.Demo -c Release \
  -f net10.0-desktop -p:DataGridSceneTarget=net10.0-desktop

# Browser
dotnet workload install wasm-tools
dotnet publish samples/DataGridScene.Demo -c Release \
  -f net10.0-browserwasm -p:DataGridSceneTarget=net10.0-browserwasm \
  -p:WasmShellWebAppBasePath=/DataGridScene/ -o artifacts/browser
node scripts/prepare-site.mjs
npm install --ignore-scripts
npx playwright install --with-deps chromium
npm run test:browser
```

`DataGridSceneTarget` selects only the Uno projects' target; it does not override the platform-independent libraries' `net10.0` target.

## Try the grid

Click headers to cycle sorting; Shift-click adds a secondary sort. Drag headers to reorder or group, and drag separators to resize. Right-click a header for freeze/hide/filter options. Select **Nested bands** to see hierarchical shared headers; drag a band to reorder it, drag its boundary to resize its visible columns proportionally, or right-click it to freeze/hide/best-fit the whole band.

F2/Enter starts editing, Enter commits, and Escape cancels. Shift extends selection, Ctrl toggles cells or rows, and dragging selects a range. Ctrl+C/V copies/pastes; Ctrl+Z/Y undo/redo cell edits; F4 expands details. The options panel selects row or cell selection modes. **Group summaries** demonstrates nested groups sorted by sales, separate group totals and averages, and group footers. **Configure summaries…** opens the reusable staged editor; right-click a summary to sort by its value. See [summary APIs](docs/summaries.md).

Save/restore includes column order, widths, pinning, band paths, filters, search, groups, summaries, row heights, theme, scroll position, expansion, and optional compressed selection/focus/anchor state. See [workspace APIs](docs/workspace.md).

## Live updates and performance

Select **Live updates** for a 100,000-row sample. Start/stop its synthetic feed, update 100 display values, or batch 1,000 independent quantity edits. The feed pauses during editing and stops when leaving the scenario. The sidebar reports actual shaping and value-only update counts.

Display-only edits reuse shaped rows and geometry when validated dependencies prove that sorting, filtering and summaries are unaffected. `GridView.DeferRefresh()` and `GridControl.BeginDataUpdate()` coalesce related operations. Uniform row geometry uses constant-size storage, custom heights use adaptive sparse/dense indexes, and selected-cell counts are cached. Relevant shaping changes still use the complete refresh path. See [performance APIs, measurements and limits](docs/performance.md).

Viewport frames and summary geometry are cached across display-only updates. The page cache separately bounds completed pages, pending requests and active fetches, and rejects mixed revisions. The correctness review also fixes rapid-click editing, untouched null/timestamp commits and visual-filter semantics. See [review findings, paging APIs and measured workloads](docs/review.md).

## Checked value filters

The column funnel now supports a searchable, virtualized checklist with inclusion/exclusion, all/none, staged Apply/Cancel, and retained choices across searches. Typed selections persist in workspace version 3. The condition entry remains available. Membership predicates use an index and read each field once per tested row. See [APIs, limits and measured results](docs/value-filters.md).

## Command UI and filter strip

The sample now uses a compact Office-style command ribbon with Demo/Themes tabs, original vector icons, searchable examples, full-width view and whole-shell white/dark themes. The grid has flat headers, correct up/down sort arrows, hover feedback, and a native bottom filter strip. Suspend/re-enable conditions without losing checked selections, remove one condition, or clear conditions while preserving search. See [UI reference, reusable controls and filter-panel APIs](docs/chrome.md). This is a visual/functional increment, not pixel-identical or complete DevExpress parity.

## Normal controls inside cells

Select **Native controls** to use real TextBox, NumberBox, ComboBox and CheckBox cells, an edit-only calendar, a progress-to-slider editor and a composite DataTemplate with a bound label and action button. Register ordinary Uno controls with `SetCellTemplate` / `SetCellDataTemplate`, or realize an editor only for the active cell with `SetCellEditTemplate`. Controls use clipped viewport placement, bounded recycling and version-checked edit buffers. The ordinary canvas grid remains available. See [native-cell APIs, lifetime, validation and limitations](docs/native-cells.md).

Rows can choose different normal controls through `SetCellTemplateSelector` and `SetCellEditTemplateSelector`. The **Adaptive cell editors** command demonstrates this, with cached viewport choices and selection/focus decoration that does not rebind input values. See [template selectors and appearance](docs/template-selectors.md).

Native Tab/Shift+Tab order follows visible rows and columns even after control recycling, with frozen panes ordered by their actual position. Composite cells retain their own child TabIndex values and disabled controls are left to the native focus system. Host ranks update without reparenting or replacing controls. The browser handoff between text and other controls preserves keyboard ownership without disabling the accessibility entry. See [native Tab-order geometry, browser integration, tests and boundaries](docs/native-tab-order.md).

## Edit a complete row

Choose **Edit selected row…** below the Editing example. The native popup stages all changes until **Update** or Ctrl+Enter; Cancel or Escape discards them. One successful changed row creates one undo item. Field and complete-row validation retain invalid input, and a changed/replaced record rejects stale updates. Exact decimals, large integers, timestamp offsets and untouched nulls do not pass through double-based editors. An explicit Null checkbox distinguishes null from an empty string.

Use `GridRowEditBuffer` independently in the core package, `RowEditForm` in an application-owned surface, or `await grid.ShowRowEditAsync(recordId, fields)` for grid integration. The form works in both cell modes and realizes one row's fields, not one form per source record. Read-only transitions preserve text; table replacement and unloading close the popup without saving. See [row-form APIs, validation, lifecycle and limits](docs/row-editing.md).

## Native versions of every example

Enable **Native cells** beside search to run every existing example with ordinary Uno cell content while preserving its data, layout, filtering, selection and history. Explicit Native controls and Adaptive cell editors templates remain available. Lightweight native display elements switch to native editors with F2 or a double-click; group/detail/header/summary chrome continues to use the shared Skia renderer.

Native scrolling retains collapsed pooled controls under the same visual parent, reuses the outgoing viewport before trimming idle content, and updates render transforms without rewriting unchanged dimensions. See [the native-mode API, scrolling counters, lifecycle and limits](docs/native-scrolling.md). Scrollbars remain visible in both modes.

## Validation and deployment

The shared C# validation contains **375 contracts** (351 baseline, eight native Tab-order geometry checks and sixteen staged-row contracts), run on .NET and inside the actual published WebAssembly application. The complete browser inventory is **250 checks across 15 harnesses**, including every example in native cell mode, actual painted scrollbar visibility, native input and Tab order, row forms, selection, layouts, summaries, live updates, HiDPI and touch. Five browser harnesses also execute **48 individual real Uno control API contracts**. Tests retain JSON reports, screenshots and Playwright traces. Consult the exact commit's Actions result for pass/fail status; inventory counts alone are not proof of success.

CI builds the desktop target and all six packages. Only a successful **main** validation can trigger Pages deployment, and deployment uses that exact run's browser artifact. The deployment check verifies `build.json`, opens the public site in Chromium, edits/undoes a cell, opens the nested-band and group-summary samples, opens/cancels the summary editor, verifies a display-only update on 100,000 rows without reshaping, and applies a checked column filter using real pointer input. Row forms, native modes, selectors, controls, input, Tab order and painted-scrollbar suites additionally run on the public URL.

The renderer draws into Uno's Skia canvas. Hardware acceleration depends on the host and graphics driver. Hosted browser tests use ANGLE/SwiftShader, so their timings are **software-backend correctness evidence, not hardware GPU benchmarks**. The sample displays CPU paint time, not end-to-end frame or input latency.

See [architecture](docs/architecture.md), [validation](docs/validation.md), and [remaining compatibility work](docs/compatibility.md).

MIT license. This project is not affiliated with DevExpress.

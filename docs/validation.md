# Validation

## Shared managed and geometry contracts

`tests/DataGridScene.Tests` runs the 351-contract `NativeCellValidationSuite`, eight native Tab-order contracts, sixteen row-buffer contracts, ten row-form layout contracts and ten atomic row-batch staging contracts. The combined **395-contract** report is written to `artifacts/core-validation.json`; any failed assertion causes a nonzero exit.

The same contracts run inside the actual published Uno WebAssembly application, invoked by their corresponding browser harnesses. These exercise schema, shaping, selection, version-checked edits, clipboard/export, paging, row-height indexes, bands, workspace state, native buffers, viewport-local focus ranks, transactional row edits, responsive field packing and failure-safe atomic staging.

## Real browser interactions

`npm run test:browser` runs seventeen interaction harnesses and the shared deployment smoke verifier against the actual WASM sample. Diagnostic commands supply fixtures/configuration and read snapshots; user interactions use real pointer, keyboard, clipboard and native text input.

| Harness | Browser checks | Embedded contracts / focus |
|---|---:|---|
| `row-editor-templates.mjs` | 16 | Ten shared batch contracts and sixteen Uno API contracts; templates/composites, multiline keys, callback failures, ownership, nulls, exact values and retained work. |
| `row-form-layout.mjs` | 12 | Ten shared geometry and six Uno API contracts; captions/spans, responsive native editor identity, validation, keyboard order and bounded work. |
| `row-editing.mjs` | 17 | Sixteen shared row-buffer and six Uno API contracts; atomic Update/Cancel, exact values, stale records and undo. |
| `native-tab-order.mjs` | 15 | Eight shared geometry contracts; Tab/Shift+Tab, recycled hosts, composite controls, browser handoff and external focus. |
| `native-cells.mjs` | 24 | Eleven Uno API contracts; native editors, popups, validation, recycled identity and undo. |
| `native-modes.mjs` | 29 | Nine Uno API contracts; every example in both cell modes, preserved state, input and retained-tree scrolling. |
| `native-input-regressions.mjs` | 4 | Recycled double-clicks, outstanding validation and display context menus. |
| `scrollbars.mjs` | 25 | Painted thumb contrast, thirteen examples, dragging, arrow/page/keyboard input, validation and HiDPI touch. |
| `navigation-viewport.mjs` | 4 | Native sidebar input at 960- and 720-pixel window heights. |
| `template-selectors.mjs` | 14 | Row-aware choices, retained dirty editors, null fallbacks, callback errors, appearance-only updates and cache bounds. |
| `run.mjs` | 32 | The 351 shared contracts in WASM; baseline grid, clipboard, exports, HiDPI and touch. |
| `workspace.mjs` | 16 | Bands, selection, expansion, saved geometry, malformed-state rejection and two-way options. |
| `summaries.mjs` | 20 | Group footers, aligned/multiline values, context sorting and staged summary editor. |
| `performance.mjs` | 11 | Live updates, 100,000 records, values-only updates and batched edits. |
| `review.mjs` | 8 | Ten Uno API contracts; cached frames, exact/null values and read-only transitions. |
| `value-filters.mjs` | 13 | Checked inclusion/exclusion, search, persistence and virtualized distinct-value filtering. |
| `chrome.mjs` | 18 | Twelve Uno API contracts; ribbon, themes, full-width view and filter-panel interactions. |

Inventory: **278 interaction checks**, **395 shared managed/geometry contracts**, and **70 individual Uno control API contracts**. Embedded contract invocations are included in the interaction count, not additional independent scenarios. The **14-step deployment smoke** is reported separately; `deployment-smoke.mjs` is the eighteenth test command. Counts alone do not establish that a particular run passed.

The test server uses the same `/DataGridScene/` base path as Pages without cross-origin-isolation headers. Reports, screenshots, exports and Playwright traces are retained. Tests include rejected operations, identity and version invariants. They do not compare a licensed DevExpress build side by side.

## CI artifacts

- `core-validation-and-packages`: source archive, managed JSON report, non-gating performance measurements, Linux raster dependencies and five independent packages.
- `browser-validation`: WASM and interaction reports, screenshots, exports, traces and a separately tagged local-artifact smoke report.
- `browser-application`: static application including `build.json` with the producing commit.
- `uno-control-package`: reusable Uno control package for desktop/browser targets.

Consult the exact head or merge commit's Actions run. Source archives and compiles are not evidence that browser tests passed. The desktop job builds the shared Uno desktop target, not a full Windows/macOS/Linux UI runtime matrix.

## Deployment verification

Only successful main validation triggers Publish Pages. It downloads the exact run's browser artifact rather than rebuilding another source revision. Verification compares the public `build.json` commit and exercises editing/undo, bands, group summaries, the summary editor, live updates, checked filters, the filter panel, whole-shell themes and native cell content.

`tests/browser/deployment-smoke.mjs` executes the same `scripts/verify-deployment.mjs` before merge against the CI artifact on loopback. The explicit `VERIFY_LOCAL_ARTIFACT=1` path accepts only HTTP `127.0.0.1`; reports label this `local-artifact`. Public verification still requires HTTPS, the expected commit and its own successful run. Passing a local-artifact smoke is not evidence of a successful public publication.

The public URL additionally runs the complete custom row editor, row-form layout, row-editing, native Tab-order, template-selector, native-mode, native-cell, native-input and painted-scrollbar harnesses. Navigation commands are clicked only when their centers lie in the actual sidebar viewport. Reports/screenshots are retained as `deployed-site-verification`.

A failed verification does not undo publication. It marks the deployment unsuccessful and must be investigated before claiming the public app is verified.

## Diagnostic integrity and readiness

Clipboard tests first write an unrelated sentinel. Ctrl+C must actually change the clipboard; a prior value cannot accidentally pass.

ComboBox tests wait for the real open popup, realized items and focused item between keys. Pointer completion does not imply Uno has processed queued focus/layout. Fresh scrollbar tests wait for nonzero native parts, then inspect screenshot pixels without hovering or forcing layout.

The shared `clickChromeAction` observes the selected ribbon surface and actual positive on-screen button bounds before clicking. It prevents the deployment-only race where a Themes click had completed but the theme-choice controls still measured zero. No product action, focus or layout mutation is performed from a readiness read. Repeated real theme transitions remain covered and the public verifier's theme assertions are retained.

Focus/layout diagnostics read existing cells, fields, focus, instances and counters. They never assign TabIndex, move focus, force layout, invoke templates or materialize offscreen controls. Row form snapshots return the last applied plan rather than calculating it. Tests compare actual editor identities across resizes and keep invalid values until corrected or cancelled.

Custom editor tests also inject failing captures and permission callbacks, reused roots and throwing cleanup. Model values, staged values, history and ownership must remain correct; a successful screenshot or absence of an exception alone does not satisfy the checks.

## Offline compilation and performance

`scripts/validate-offline.py` uses the repository's Offline validation kit and the real .NET compiler/runtime. It does not execute MSBuild, pack packages, compile generated platform sample code, or validate a browser. Full CI remains required.

`dotnet run --project tests/DataGridScene.Tests -c Release -- --benchmark` reports warmed times/allocations; timing is not a machine-dependent pass threshold. Correctness gates check retained identities, refresh counts, storage bounds, agreement with complete refreshes and results after undo.

Stress checks include 100,000 records, one million indexed row heights, 10,000 columns/bands and compressed selections. Native scrolling counts creations, reparenting, bindings, sizes, translations and focus-rank writes. Forms count packing, placement, editor creation, capture and permission writes separately from the underlying grid/window layout. These are bounded-work tests, not universal throughput guarantees.

See [performance](performance.md), [review findings](review.md), [native Tab order](native-tab-order.md), [row editing](row-editing.md), [row form layout](row-form-layout.md) and [custom row editors](row-editors.md). Hosted Chromium uses ANGLE/SwiftShader. Hardware GPU throughput, power, presentation latency, complete multilingual fallback, all DPI/driver cases and pixel-identical appearance remain unverified.

# Validation

## Shared managed and geometry contracts

`tests/DataGridScene.Tests` runs the 351-contract `NativeCellValidationSuite` and eight
`NativeTabOrderValidation` contracts. The combined 359-contract report is written to
`artifacts/core-validation.json`; any failed assertion causes a nonzero exit.

The same contracts run inside the actual published Uno WebAssembly application: the
baseline browser harness invokes the 351-contract suite and the native Tab-order harness
invokes the eight additional geometry contracts. These exercise schema, shaping,
selection, version-checked edits, clipboard/export, paging, row-height indexes, bands,
workspace state, native buffers and viewport-local focus ranks.

## Real browser interactions

`npm run test:browser` runs all fourteen harnesses against the actual WASM sample.
Diagnostic commands supply fixtures/configuration and read snapshots; user interactions
use real pointer, keyboard, clipboard and native text input.

| Harness | Browser checks | Additional embedded contracts / focus |
|---|---:|---|
| `native-tab-order.mjs` | 11 | Eight shared geometry contracts; actual Tab/Shift+Tab, recycled hosts, frozen panes, composite controls and work counters. |
| `native-cells.mjs` | 24 | Eleven real Uno API contracts; text, number, choice, date, action and slider controls; validation, identity and undo. |
| `native-modes.mjs` | 29 | Nine real Uno API contracts; every example in both modes, preserved state, native input and retained-tree scrolling. |
| `native-input-regressions.mjs` | 4 | Recycled double-clicks, outstanding validation messages and native display context menus. |
| `scrollbars.mjs` | 25 | Actual painted thumb contrast, thirteen examples, drag/arrow/page/keyboard input, validation and HiDPI touch. |
| `navigation-viewport.mjs` | 4 | Actual sidebar wheel input at 960- and 720-pixel window heights. |
| `template-selectors.mjs` | 14 | Row-aware choices, dirty-editor retention, null fallbacks, callback errors, appearance-only updates and cache bounds. |
| `run.mjs` | 32 | The 351 shared contracts in WASM; baseline grid, clipboard, exports, HiDPI and touch. |
| `workspace.mjs` | 16 | Bands, selection, expansion, saved geometry, malformed-state rejection and two-way options. |
| `summaries.mjs` | 20 | Nested group footers, aligned/multiline values, context sorting and staged native summary editor. |
| `performance.mjs` | 11 | Live updates, 100,000 records, value-only updates and batched edits. |
| `review.mjs` | 8 | Ten real Uno API contracts; cached frames, exact/null values and read-only transitions. |
| `value-filters.mjs` | 13 | Checked inclusion/exclusion, search, persistence and virtualized 100,000-record distinct-value filtering. |
| `chrome.mjs` | 18 | Twelve real Uno API contracts; command ribbon, themes, full-width view and filter-panel interactions. |

The inventory is **229 browser checks**, **359 shared managed/geometry contracts**, and
**42 individual real Uno control API contracts**. Embedded invocations are included in
the browser-check count; they are not additional independent browser scenarios. Counts
alone do not show whether any particular run passed.

The static server uses the same `/DataGridScene/` base path as Pages without
cross-origin-isolation headers. Reports, screenshots, exports and Playwright traces
are retained. Tests verify identity/version invariants and rejected operations, not
only happy paths. They do not compare a licensed DevExpress build side by side.

## CI artifacts

- `core-validation-and-packages`: source archive, managed JSON report, non-gating
  performance measurements, Linux raster dependencies and five independent packages.
- `browser-validation`: WASM and interaction reports, screenshots, exports and traces.
- `browser-application`: static application including `build.json` with the producing commit.
- `uno-control-package`: the reusable Uno control package for desktop/browser targets.

Consult the exact head or merge commit's Actions run. Source archives and successful
compiles are not evidence that browser interactions passed. The desktop job builds the
shared Uno desktop target; it is not a full Windows/macOS/Linux native UI runtime matrix.

## Deployment verification

Only successful main validation triggers Publish Pages. The deployment downloads the
exact successful run's browser artifact instead of rebuilding different sources.
Verification compares the public `build.json` commit and exercises actual editing/undo,
bands, group summaries, the summary editor, live updates and checked filters.

The public URL also runs the complete native Tab-order, template-selector, native-mode,
native-cell, native-input and painted-scrollbar harnesses. Navigation commands are
clicked only after their centers lie inside the actual sidebar viewport. Reports and
screenshots are retained in `deployed-site-verification`.

Failed verification does not undo publication. It marks the deployment unsuccessful
and must be investigated before claiming the public app is verified.

## Readiness and diagnostic integrity

Clipboard tests write an unrelated sentinel, issue Ctrl+C, then wait for the clipboard
to change; a previous clipboard value cannot accidentally satisfy the check.

ComboBox tests wait for the actual open popup, realized items and focused item between
keys. Browser pointer completion does not guarantee Uno has processed queued focus and
popup layout. Fresh-start scrollbar tests likewise wait for real, nonzero template
parts, then inspect screenshot-pixel contrast without hover input or forced layout.

Tab-order diagnostics inspect existing hosts and focus. They never assign TabIndex,
move focus, invoke templates or materialize offscreen cells. The composite-cell fixture
explicitly creates real child controls with reversed TabIndex values and an optional
disabled child; actual browser keys must honor those values.

## Offline compiler and performance checks

`scripts/validate-offline.py` uses the repository's Offline validation kit to directly
compile managed libraries, Uno controls and the contract executable with the real .NET
compiler. It does not run MSBuild, pack packages, compile generated platform sample code,
or validate a browser. Full CI remains required.

`dotnet run --project tests/DataGridScene.Tests -c Release -- --benchmark` records warmed
median times and allocations. Timings are reported, not used as machine-dependent pass
thresholds. Correctness gates instead check retained identities, refresh counts, bounded
storage, randomized agreement with full refreshes and results after undo.

Stress checks include 100,000 materialized records, one million indexed row heights,
10,000 indexed columns/bands and compressed selections. Native scrolling checks count
control creation, reparenting, binding, dimension, translation and tab-rank writes.
These are bounded-work checks, not universal throughput guarantees. See
[performance](performance.md), [review findings](review.md), and [native Tab order](native-tab-order.md).

Hosted Chromium uses ANGLE/SwiftShader. Hardware GPU throughput, power, presentation
latency, complete multilingual fallback, all DPI/driver cases and pixel-identical
DevExpress appearance remain unverified.

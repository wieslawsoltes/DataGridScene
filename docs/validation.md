# Validation

## Reproducible layers

The executable `tests/DataGridScene.Tests` runs `NativeCellValidationSuite`, which includes the baseline and selection suites. Its 351 contracts cover schema, data shaping, editing, clipboard/export, paging, variable-height indexing, bands and workspace state. It writes `artifacts/core-validation.json` and exits nonzero on any failed assertion.

The actual Uno WebAssembly application exposes the same suite through a sample-only diagnostic bridge. `tests/browser/run.mjs` invokes it in the browser runtime and also performs 32 interaction checks. `tests/browser/summaries.mjs` performs 20 summary, context-menu and staged-editor interaction checks. `tests/browser/workspace.mjs` performs 16 additional band/workspace checks. Real browser pointer, keyboard, clipboard and native text input are used; diagnostic commands supply fixtures/configuration and snapshots, not synthetic success results.

Clipboard tests first write an unrelated sentinel, issue Ctrl+C, and poll until the browser clipboard actually changes. This avoids accepting pasted input or a prior selection's clipboard contents while an asynchronous copy is still pending.

`tests/browser/performance.mjs` adds 11 live-update and bounded-work checks, including real cell edits, start/stop controls, wheel scrolling and a batch of 1,000 edits.

`tests/browser/review.mjs` adds eight checks for cached frames, edit preservation, read-only transitions and real control APIs. Its control-API check invokes ten individual Uno contracts; these instantiate the controls on the UI thread rather than simulating their behavior. The shared 351-contract suite also includes raster stack restoration and 22 bounded page-scheduling checks.

`tests/browser/value-filters.mjs` adds thirteen checked-filter scenarios, including native ListView virtualization on a 100,000-record field, search across the value limit, checked selection, exclusion, condition entry and persistence.

`tests/browser/chrome.mjs` adds 18 command-ribbon/filter-panel checks and runs 12 additional real Uno API contracts. The chrome and previous browser suites contain 118 checks; their two embedded control suites contain 22 individual API contracts. It captures light/dark, full-width and filter-panel screenshots.

`tests/browser/native-cells.mjs` adds 22 native-cell checks and runs 11 additional real Uno API contracts. The pre-selector inventory is 140 browser checks and 33 embedded control API contracts. Native tests cover actual editors, validation, undo, actions, popup input, recycled identities, 100,000 records and bounded rebinding.

`tests/browser/template-selectors.mjs` adds 14 row-aware-template, native appearance and bounded-work checks, for a total of 154 browser checks before the navigation suite. It verifies per-record choices, null fallbacks, dirty-editor retention, callback failure, 100,000-row cache bounds and no rebinding during repeated no-op commands.

`tests/browser/navigation-viewport.mjs` adds four regressions at 960- and 720-pixel window heights. It scrolls the real native sidebar with wheel input before clicking offscreen commands, then validates live updates and native editing/undo. The full inventory is 158 browser checks.

`npm run test:browser` runs all ten harnesses. The static test server uses the same `/DataGridScene/` base path without cross-origin-isolation headers. HiDPI and touch contexts are included in the baseline harness.

## CI artifacts

The Validate workflow produces:

- `core-validation-and-packages`: source archive, managed JSON report and five independent NuGet packages.
- `browser-validation`: the WASM conformance report, baseline interaction report, workspace interaction report, screenshots, exports and traces.
- `browser-application`: the published static application, including `build.json` with the exact producing commit.
- `uno-control-package`: the reusable Uno control NuGet package.

Consult the exact head/merge commit's Actions run. A generated source archive or successful compile is not evidence that the browser tests passed.

The desktop CI job builds the shared Uno desktop target. It does not currently run a complete Windows/macOS/Linux native UI test matrix.

## Pages

After successful main validation, Publish Pages downloads that run's browser artifact rather than rebuilding different sources. Its deployment verification compares the public `build.json` commit, starts the actual WASM app in Chromium, performs a native edit and undo, opens the nested-band and group-summary scenarios, opens/cancels the summary editor, verifies a display-only update on 100,000 rows without reshaping, and applies a checked column filter using real pointer input. The complete 14-scenario selector browser suite then runs against the public URL. Navigation commands are clicked only after their centers lie within the actual sidebar viewport; failures retain the final application snapshot and screenshot. Verification reports and screenshots are retained as `deployed-site-verification`.

A failed deployment verification does not undo publication; it marks the deployment workflow unsuccessful and must be investigated before claiming the public site is verified.

## Offline compiler check

`scripts/validate-offline.py` can use the compiler/runtime kit produced by the repository's Offline validation kit workflow. It directly compiles the managed libraries, Uno controls and contract executable with the real .NET compiler and runs the tests. It is useful in disconnected environments, but does not run MSBuild targets, pack NuGet packages, compile the sample's generated platform code, or validate a browser. Full CI remains required.

See the [review findings and additional benchmark comparison](review.md) for current limits, paging contracts and measured small-selection clipboard workloads.

## Performance measurements

`dotnet run --project tests/DataGridScene.Tests -c Release -- --benchmark` records medians and allocations from five warmed rounds. Timing is reported, not used as a machine-dependent pass threshold. CI retains `performance-benchmarks.json` with the core artifacts. The [performance document](performance.md) includes a same-harness baseline comparison and explains exactly what is measured.

Correctness gates instead assert retained object identities, exact refresh counts, bounded index storage, randomized agreement with complete refreshes, and the unchanged aggregate results after undo. Browser diagnostics read counters without repairing state.

## Performance and visual limits

Stress assertions cover 100,000 materialized records, one million indexed row heights, 10,000 indexed columns/bands and compressed 100,000-row selections. These are correctness/bounded-visual-work checks, not universal throughput guarantees.

Chromium is launched with ANGLE/SwiftShader in hosted CI. This exercises graphics API paths on a software implementation. Hardware GPU throughput, power, full frame presentation, input latency, complex text fallback and pixel-identical DevExpress appearance remain unverified.

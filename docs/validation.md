# Validation

## Reproducible layers

The executable `tests/DataGridScene.Tests` runs `UpdateValidationSuite`, which includes the baseline and selection suites. Its 252 contracts cover schema, data shaping, editing, clipboard/export, paging, variable-height indexing, bands and workspace state. It writes `artifacts/core-validation.json` and exits nonzero on any failed assertion.

The actual Uno WebAssembly application exposes the same suite through a sample-only diagnostic bridge. `tests/browser/run.mjs` invokes it in the browser runtime and also performs 32 interaction checks. `tests/browser/summaries.mjs` performs 20 summary, context-menu and staged-editor interaction checks. `tests/browser/workspace.mjs` performs 16 additional band/workspace checks. Real browser pointer, keyboard, clipboard and native text input are used; diagnostic commands supply fixtures/configuration and snapshots, not synthetic success results.

Clipboard tests first write an unrelated sentinel, issue Ctrl+C, and poll until the browser clipboard actually changes. This avoids accepting pasted input or a prior selection's clipboard contents while an asynchronous copy is still pending.

`tests/browser/performance.mjs` adds 11 live-update and bounded-work checks, including real cell edits, start/stop controls, wheel scrolling and a batch of 1,000 edits.

`npm run test:browser` runs all four harnesses. The static test server uses the same `/DataGridScene/` base path without cross-origin-isolation headers. HiDPI and touch contexts are included in the baseline harness.

## CI artifacts

The Validate workflow produces:

- `core-validation-and-packages`: source archive, managed JSON report and five independent NuGet packages.
- `browser-validation`: the WASM conformance report, baseline interaction report, workspace interaction report, screenshots, exports and traces.
- `browser-application`: the published static application, including `build.json` with the exact producing commit.
- `uno-control-package`: the reusable Uno control NuGet package.

Consult the exact head/merge commit's Actions run. A generated source archive or successful compile is not evidence that the browser tests passed.

The desktop CI job builds the shared Uno desktop target. It does not currently run a complete Windows/macOS/Linux native UI test matrix.

## Pages

After successful main validation, Publish Pages downloads that run's browser artifact rather than rebuilding different sources. Its deployment verification compares the public `build.json` commit, starts the actual WASM app in Chromium, performs a native edit and undo, opens the nested-band and group-summary scenarios, opens/cancels the summary editor, and verifies a display-only update on 100,000 rows without reshaping. Verification reports and a screenshot are retained as `deployed-site-verification`.

A failed deployment verification does not undo publication; it marks the deployment workflow unsuccessful and must be investigated before claiming the public site is verified.

## Offline compiler check

`scripts/validate-offline.py` can use the compiler/runtime kit produced by the repository's Offline validation kit workflow. It directly compiles the managed libraries, Uno controls and contract executable with the real .NET compiler and runs the tests. It is useful in disconnected environments, but does not run MSBuild targets, pack NuGet packages, compile the sample's generated platform code, or validate a browser. Full CI remains required.

## Performance measurements

`dotnet run --project tests/DataGridScene.Tests -c Release -- --benchmark` records medians and allocations from five warmed rounds. Timing is reported, not used as a machine-dependent pass threshold. CI retains `performance-benchmarks.json` with the core artifacts. The [performance document](performance.md) includes a same-harness baseline comparison and explains exactly what is measured.

Correctness gates instead assert retained object identities, exact refresh counts, bounded index storage, randomized agreement with complete refreshes, and the unchanged aggregate results after undo. Browser diagnostics read counters without repairing state.

## Performance and visual limits

Stress assertions cover 100,000 materialized records, one million indexed row heights, 10,000 indexed columns/bands and compressed 100,000-row selections. These are correctness/bounded-visual-work checks, not universal throughput guarantees.

Chromium is launched with ANGLE/SwiftShader in hosted CI. This exercises graphics API paths on a software implementation. Hardware GPU throughput, power, full frame presentation, input latency, complex text fallback and pixel-identical DevExpress appearance remain unverified.

# Browser validation workers

The browser build is compiled once. Four independent Actions workers download the same `browser-application` artifact and compare its `build.json` commit with their checkout before running any suite. The source/build relationship is identical for pull-request merge refs and main. No worker rebuilds a different revision.

`scripts/run-browser-suites.mjs` partitions all nineteen commands (eighteen interaction harnesses plus deployment smoke) into four disjoint groups: forms, native, viewport and features. Within a worker, each suite is a separate Node process with its own browser; suites run sequentially so their loopback server port cannot collide. Different workers run on different hosted machines, avoiding artificial concurrent SwiftShader load in a single browser/performance test.

The registry test checks that all nineteen expected harnesses occur exactly once. It also checks unknown group rejection, owned plan copies, error/signal/timeout propagation, execution of later suites after a failure, and source/artifact provenance. Its seven Node tests are runner/deployment contracts, not additional browser interactions or Uno API contracts.

## Failure semantics

A failed suite is recorded once and is never retried by this runner. Independent later suites still execute. The matrix uses `fail-fast: false`; a failing worker does not cancel the others. Every worker returns a nonzero exit for any failure, process error or timeout. Ten-minute per-suite process deadlines prevent indefinite hangs; normal suite-specific input readiness deadlines remain unchanged.

The final `browser` job requires the build and the entire browser matrix to succeed and requires all four runner manifests. It gathers per-suite reports/screenshots/traces into the existing combined `browser-validation` artifact. Per-worker artifacts are retained as `browser-validation-forms`, `-native`, `-viewport` and `-features` even when a worker fails. Missing or skipped workers cannot satisfy the gate. A compiled/package artifact by itself is not a passing test result.

Pages continues to depend on the **complete successful main Validate run**, not merely `browser-build`. It downloads that exact validated artifact and performs its own HTTPS/commit and public interaction checks. No Pages permission or publication gate is relaxed.

## Local commands

```sh
npm run test:runner
npm run test:browser                  # all nineteen suites, sequential
node scripts/run-browser-suites.mjs forms
node scripts/run-browser-suites.mjs features
```

The runner accepts only the registered group names or `all`. `SITE_ROOT`, `BROWSER_RESULTS` and the existing `APP_URL` override remain available. A local-artifact result remains distinct from public deployment verification. The independent original `.mjs` harness entry points remain directly runnable.

## Navigation regression review

Validate 38403973140 passed custom editors, smoke, responsive/transactional forms, native Tab/cells/modes/input/scrollbars, sidebar navigation, template selectors, baseline grid and workspace suites. Its summary suite failed 8 of 20 checks: `summaries` had a window-space center at y=984, below the sidebar's y=972 clipping boundary, even though the button's bottom was within the 1000px browser window. The old harness checked only the browser boundary.

Summary and live-update actions now share `clickNavigationAction`, which performs real wheel scrolling before a single pointer click inside the actual native sidebar viewport. Summary cancellation additionally exercises both 1000px and 720px windows, checks the target lies inside the sidebar, and verifies the complete query is unchanged. No product command, control value, layout forcing, blind click retry or enlarged-window workaround substitutes for those interactions.

## Performance interpretation

The worker split removes serial dependency between unrelated suites and keeps native compilation single-pass. Wall-clock improvement depends on runner availability and artifact transfer; consult exact workflow timestamps rather than assuming a speedup factor. It consumes more concurrent CI machines, not fewer total browser assertions. Original operation-count gates and all 278 previous interactions remain in place. Sixteen recent-filter checks raise the interaction inventory to 294.

See [validation](validation.md) and the [download-artifact action's documented multi-artifact behavior](https://github.com/actions/download-artifact/tree/v4). Browser graphics remain ANGLE/SwiftShader, not a hardware GPU benchmark.

## Deployment rerun identity

Public Pages run 38453646538 initially deployed main `1489f2a` successfully, then a CDN HTTP 503 for `System.ComponentModel.iv4cyr9ulo.wasm` prevented the row-editing browser from starting. The earlier public smoke, custom-editor and row-layout suites passed. A single unchanged-job rerun exposed a separate workflow error: artifacts from earlier attempts remain visible, so `deploy-pages` rejected two identically named `github-pages` artifacts before deployment.

Uploads and deployments now use the same `github-pages-${{ github.run_attempt }}` name. The seven-test Node runner suite verifies the matching expressions, distinct attempt identities and unchanged read-only artifact/successful-validation source gates. The failure was reproduced against the old workflow before the change. No artifact deletion, wider permission, conditional test skip, automatic interaction retry or weaker deployment verification is added. Existing failed attempts remain available as evidence.

The supported inputs are documented in the official [upload-pages-artifact](https://github.com/actions/upload-pages-artifact/blob/v3/action.yml) and [deploy-pages](https://github.com/actions/deploy-pages/blob/v4/action.yml) action definitions. Naming is a configuration regression check, not proof that a subsequent deployment or public browser check passed.

# Performance and correctness review

This review covers the six package boundaries and the shared Uno sample. It is not a certification that every feature or every possible application callback is bug-free, and does not establish full DevExpress parity.

## Corrected behavior

| Area | Correction | Evidence |
|---|---|---|
| Core shaping | Preserve row/group/summary identities for proven display-only changes; coalesce relevant edits; cache computed sort keys; construct an initial query in one shaping pass. | Dependency, identity, call-count and randomized differential contracts. |
| Selection and clipboard | Count selected cells in constant time; locate only selected identities when copying small selections; preserve rectangular holes; do not expose mutable selection storage through the enumeration API. | Sparse selection, clipboard order, hidden/collapsed row, bounds and 100,000-row contracts. |
| Layout and batching | Adaptive uniform/sparse/dense height storage; distinguish viewport changes from dirty row geometry inside control batches. | Exact boundaries, storage limits, randomized geometry and real Uno batch contracts. |
| Rendering | Reuse viewport frames and summary geometry; validate invalid dimensions; restore the caller's entire canvas save/clip stack when a callback throws. | Cache invalidation contracts and cached-versus-fresh raster equality, including throwing getters/styles/details. |
| Editing | Preserve untouched nulls, timestamps, precision and record versions; block dirty commits after switching read-only; rapid duplicate presses refocus the current editor instead of closing it. | Actual browser text, keyboard, pointer, undo and double-click regressions. |
| Filter builder | Retain exact typed literals, null versus empty strings, empty OR and negated empty AND semantics, and canonical field identities; reject unsupported application predicates without dropping them. | Ten programmatic contracts instantiate real Uno controls on the browser UI thread, including the viewport-batch contract. |
| Remote page infrastructure | Separate completed LRU entries from pending requests; bound concurrency and queue size; suppress stale data; keep generation cancellation sources alive until active fetches finish; reject mixed revisions/counts. | Twenty-two deterministic page scheduling and metadata contracts, also run in WASM. |

The native suite and browser runtime share 292 contracts, including native raster tests. Five browser harnesses contain 87 checks (32 baseline, 16 workspace, 20 summaries, 11 performance, 8 review). One review check runs the ten additional Uno API contracts and writes their individual results. These counts describe the test inventory; consult the exact commit's CI results for pass/fail status.

## Page-cache API and limits

```csharp
using DataGridScene.Data;

using var pages = new GridPageCache(
    source, pageSize: 128, capacity: 16,
    maximumConcurrency: 4, maximumPendingPages: 256);

pages.SetQuery(query);
GridPage page = await pages.GetPageAsync(0, cancellationToken);
```

The existing three-argument constructor remains available; its defaults are four active source fetches and 4,096 pending pages. Capacity limits completed pages. The queue limit counts current-generation distinct pages, not callers waiting on the same page. Queue overflow is explicit rather than silently dropping requests. An already-cancelled caller schedules no I/O; cancelling one waiter does not cancel shared work.

`PendingPageCount`, `ActiveFetchCount`, `FetchCount`, `CacheHitCount` and `CoalescedReadCount` expose scheduling activity. Completed-page eviction does not remove active requests from the coalescing map. Transient request failures are retryable. A response with a different revision or contradictory known total faults the generation and clears cached pages; call `SetQuery` to start a new generation. A response without a revision cannot silently join revisioned data.

Query replacement and disposal complete old waiters promptly. A source that ignores cancellation retains its active slot until it returns, so the configured concurrency limit still holds across generations. A permanently hung source can therefore stall new work: transports must enforce their own timeout. Cancellation callbacks run outside the cache lock. A faulty source cancellation callback cannot strand other waiters.

Page validation copies the record-list container and rejects mismatched record widths, duplicate IDs within a page, null rows and inconsistent metadata with overflow-safe arithmetic. The source still owns the record values and application objects: they must be immutable snapshots after return. This is not a cross-page global identity index or a deep copy of arbitrary application payloads. The HTTP adapter separately normalizes values against the schema.

**This is reusable paging infrastructure, not seamless server-mode integration into the visual grid.** The grid still binds a materialized local table, and the record-page contract cannot represent server group rows, remote summary results or remote edits.

## Reproducible CPU measurements

The same `PerformanceBenchmarks.cs` source was compiled against prior main `7b2751d` and the reviewed product assemblies at `671b48f`, using .NET 10.0.12 on the same Debian x64 environment. Each operation is warmed, five rounds are measured with garbage collection before each round, and medians cover the **entire operation count**, not one operation. Fixture construction is outside the timed region; allocations are for the executing thread.

| Workload | Operations | Previous median | Reviewed median | Allocated bytes, previous → reviewed |
|---|---:|---:|---:|---:|
| Display edits in 100,000 grouped/sorted records | 20 | 1,682.5395 ms | 0.0904 ms | 854,743,352 → 19,360 |
| Reset one million uniform row heights and query offsets | 10 | 19.4780 ms | 0.0090 ms | 160,000,560 → 0 |
| Read count of 300,000 selected cells | 1,000 | 118.5433 ms | 0.0277 ms | 0 → 0 |
| Reshape 100,000 records sorted by a computed total | 3 | 785.0476 ms | 247.5696 ms | 487,599,216 → 77,504,616 |
| Copy one selected row from 100,000 records | 50 | 77.9757 ms | 0.2012 ms | 40,075,600 → 83,200 |
| Copy one selected cell from 100,000 records | 50 | 291.6323 ms | 0.2897 ms | 520,088,000 → 87,600 |

These are narrowly defined CPU microbenchmarks. The largest reductions eliminate unnecessary full-view work rather than accelerating an equivalent complete sort. Sub-millisecond results are sensitive to timing/JIT overhead. They are not UI frame rate, input latency, network throughput, hardware GPU benchmarks or universal speed multipliers. The harness and allocation/call-count regressions are retained; CI records its own non-gating measurements. Later filter-builder fixes do not alter these measured code paths.

## Remaining scope and review limits

Relevant edits still reshape the local view; unknown application dependencies take the conservative path. Batching is synchronous and does not roll back already committed table writes. A failed shaping callback can leave previous aggregates stale until a successful refresh. Getters, comparers, validators and custom accumulators remain trusted application code rather than a sandbox.

Native desktop compilation is not a full Windows/macOS/Linux runtime test matrix. Browser graphics uses software ANGLE/SwiftShader. Full accessible grid providers, recursive editable master-detail, advanced editors/row transactions, multi-row or merged data cells, data-aware printing and seamless server-mode control integration remain incomplete. See the [compatibility matrix](compatibility.md).

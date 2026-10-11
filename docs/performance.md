# Live updates, batching and performance

These are independently implemented DataGridScene APIs. The public DevExpress WPF [BeginDataUpdate](https://docs.devexpress.com/WPF/DevExpress.Xpf.Grid.DataControlBase.BeginDataUpdate) and [live data shaping](https://docs.devexpress.com/WPF/DevExpress.Xpf.Grid.DataControlBase.AllowLiveDataShaping) documentation supplies behavioral context, not an API-compatibility claim.

## Avoid unnecessary reshaping

A successful `GridTable.Apply` publishes owned cell-change details. The bound view captures which fields affect its sort, filter, grouping and summaries. An edit outside that dependency set updates the existing records and issues a Values notification without recreating rows, groups, ordered records or aggregates. The Uno control repaints without rebuilding row heights or pruning an unchanged selection. Normal cell validation, record versions and undo/redo still apply.

An edit affecting shaping uses the complete refresh path. Insert/remove/reset notifications, revision gaps and missing change details also refresh. Unknown custom predicates, custom accumulators, nonempty searches and undeclared computed-field dependencies are conservative; they do not receive the fast path merely because an edit looks harmless.

Computed fields can declare their complete same-record inputs:

```csharp
var total = new GridColumn("Total", typeof(decimal))
{
    ValueDependencies = new[] { "Quantity", "UnitPrice", "Discount" },
    ValueGetter = record => Math.Round(
        (int)record[0]! * (decimal)record[1]! * (1m - (decimal)record[2]!), 2)
};
```

This example assumes the first three schema columns are Quantity, UnitPrice and Discount. Dependencies are resolved transitively and copied when constructing the schema. Missing, duplicate or cyclic dependencies are rejected. Null means unknown inputs; an empty list promises a constant value. Only annotate deterministic getters whose complete inputs are known. An incorrect application annotation can cause stale shaping; the library does not inspect delegate code or monitor arbitrary external state. Explicitly refresh when external state changes.

## Batch related operations

```csharp
// Standalone Core package; no Uno dependency.
using (view.DeferRefresh())
{
    view.SetQuery(view.RequestedQuery with { Search = "Alpine" });
    view.SetQuery(view.RequestedQuery with
    {
        Sorts = new[] { new SortDescriptor("Quantity", SortDirection.Descending) }
    });
}

// Uno control: also coalesce geometry and control state notifications.
using (grid.BeginDataUpdate())
{
    grid.SetSearch("Alpine");
    grid.SortColumn("Quantity", SortDirection.Descending);
    grid.GroupColumn("Region");
}
```

Nested scopes execute the deferred work once at outer disposal. `Query` continues to describe the published rows; use `RequestedQuery` when composing pending Core configuration. Control methods do this internally. Query input is validated and copied before retention.

These are **not rollback transactions**. Table writes commit and validate immediately; a later exception does not undo earlier writes. A shaping exception leaves the previously published query/row sequence in place, and pending state is cleared so a corrected refresh can proceed. If table values changed, old aggregates can remain stale until that successful refresh. Use the existing single `GridTable.Apply` call for an atomic multi-cell edit, and `EditHistory.Execute` for a single undoable edit group.

Dispose scopes promptly on the owning UI thread. Do not span an `await`, rebind a table, open dialogs, export or save/restore a workspace while work is pending. Table replacement through the control property and workspace save/restore reject an active control batch. Direct dependency-property writes and external disposal/rebinding are not supported within a batch. External live updates invalidate cell-edit undo history as before; batching does not change that policy.

`GridTable.BeginUpdate` retains its older single-Reset notification behavior. Use `GridView.DeferRefresh` or `GridControl.BeginDataUpdate` when individual edit metadata and conditional shaping matter.

## Adaptive row geometry

A uniform `RowHeightIndex` stores only count and default height: reset, total height, prefix offsets and inverse lookup require no per-row arrays. Fractional-height boundary tests ensure that division rounding does not move an exact boundary to the wrong row.

The first custom heights use sparse Fenwick deltas. With one override in a million rows, the regression permits at most 22 stored entries. At higher occupancy the index converts once to dense arrays for lower per-entry overhead. Clearing every override returns to the uniform representation. Sparse/dense prefix lookup and updates are logarithmic; the dense conversion itself is linear. Stored override values are retained rather than reconstructed from rounded deltas. Geometry still uses binary64 floating-point arithmetic, not exact real-number arithmetic.

The control updates a single custom row height using a stable row key instead of resetting the whole index. `ClearRowHeight(key)` restores the current row-kind default. Flat uniform view rebuilding no longer loops through every row. Group and footer heights respect summary-line minimums.

`GridSelection.SelectedCellCount` is maintained during mutations rather than summing all selected rows on every status update. `EnumerateSelectedIds()` avoids an eager full snapshot when a consumer only needs the first few identities. Do not mutate the selection during enumeration; use `SelectedIds` for the previous snapshot API.

## Try the live sample

The **Live updates** example has 100,000 materialized synthetic orders. The start/stop control schedules 100 company-name edits every 250 ms on the UI dispatcher. These names do not affect the default Total sort or aggregates. A single-step button performs the same update. Another button applies 1,000 independent quantity edits inside a data-update scope, resulting in one shaping refresh.

Incoming ticks pause while editing or read-only, and the timer stops on scenario changes, reload or unload. This is a synthetic demonstration, not a network feed, server-mode adapter, guaranteed update rate or background service. Adding a search or relevant filter can legitimately make those name changes require reshaping.

The sidebar reports shaping refreshes, values-only updates, height storage mode, last shaping time and CPU paint time. Counters are cumulative for the current view; view replacement starts new counters. `ValueUpdateCount` counts published notifications, not individual cells.

## Measured core workloads

Same machine, .NET 10.0.12, Debian 13 x64. The same `PerformanceBenchmarks.cs` source was compiled against the previous main (`7b2751d`) and the optimized core. Each workload is warmed, measured five times with collection before each round, and reports the median total for the stated operation count. Allocation is measured on the executing thread. Fixture construction is outside the timed region.

| Workload | Operations per round | Previous median | Optimized median | Allocated bytes, previous → optimized |
|---|---:|---:|---:|---:|
| Display-only edits in 100,000 sorted/grouped rows | 20 | 1,588.352 ms | 0.095 ms | 854,718,200 → 19,360 |
| Reset one million uniform row heights and query offsets | 10 | 26.717 ms | 0.008 ms | 160,000,560 → 0 |
| Read the count of 300,000 selected cells | 1,000 | 114.491 ms | 0.028 ms | 0 → 0 |

The large difference is from removing unnecessary work, **not** faster execution of a full sort or group operation. Tiny optimized timings are sensitive to clock resolution, JIT state and measurement overhead; do not extrapolate a universal speed multiplier. These microbenchmarks do not include Uno, native editors, painting, GPU work, presentation, network latency or the event-loop cost of a live application. Shape-affecting edits still rebuild the materialized view.

Run the current harness with:

```sh
dotnet run --project tests/DataGridScene.Tests -c Release -- --benchmark
```

CI retains its own measurements in `performance-benchmarks.json`; it does not gate on timing thresholds. Browser performance regressions instead verify actual input behavior, exact refresh counts and bounded storage/work, and retain a separately labeled UI batch duration. Hosted browser graphics is ANGLE/SwiftShader, a software implementation, not hardware GPU throughput evidence.

## Remaining performance scope

This increment does not implement incremental sorted/grouped-tree maintenance for relevant changes, fully asynchronous shaping, automatic heterogeneous text measurement, frame-budget scheduling, immutable table snapshots, seamless server paging, hardware profiling or a complete accessible virtualized tree. Those require their own correctness and interaction tests rather than inference from these results.

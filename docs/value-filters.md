# Checked column-value filtering

The header funnel opens the reusable Uno `ColumnFilterEditor`. Choose individual values, **All values**, **No values**, or **Select visible results**. Search discovers matches across the local table and retains previous selections. **Apply** commits the staged value choices; **Cancel** or Escape leaves the grid unchanged. The separate condition box retains contains/comparison filtering through **Apply condition instead**.

```csharp
// Include either value while retaining the base criteria and other columns.
grid.SetColumnValues("Region", new ColumnValueFilter(new object?[] { "Europe", "Asia Pacific" }));
// Exclude a value. Future values remain included unless explicitly excluded.
grid.SetColumnValues("Status", new ColumnValueFilter(new object?[] { "Cancelled" }, Exclude: true));
// Empty inclusion means no records; null clears this column's auto-filter.
grid.SetColumnValues("Region", ColumnValueFilter.None);
grid.SetColumnValues("Region", null);
```

The Core `ColumnValueSelection` stages selection without referencing Uno. `GridDistinctValues.Collect` supplies sorted, comparison-aware values. Both use schema-directed conversion and the column's comparer. Null, empty string, punctuation and tiny floating-point values are not substituted with ambiguous display text. Captures own their value-list containers; caller mutations cannot change retained filters.

Applying a value selection replaces this column's auto-filter text; entering an auto-filter condition replaces its value selection. Neither removes the independent base criteria, other column filters, search, grouping or summaries. Saving a workspace with value filters uses **version 3**, so old readers reject it instead of silently ignoring filtering. Documents without value filters retain version 2; versions 1, 2 and 3 are readable.

## Performance and limits

A popup retains at most 1,024 distinct matches, with native ListView container virtualization. On an all-unique field, the initial lookup stops after 1,025 records rather than building a grouping for every record. Search can reach values outside that first set. Rare matches or few unique values still require scanning all local records; this is not an asynchronous remote distinct-value provider. Truncation is visible and does not imply that undisplayed values were selected or deselected.

Selection retains at most 1,024 inclusions or exclusions. Attempts to exceed the bound are rejected before that batch mutates the selection. An empty exclusion selects all; an empty inclusion selects none. Choices persist across searches, and the visible-results checkbox only changes the displayed matches.

`IN` predicates compile a sorted membership index using comparison equality rather than assuming a custom comparer has a compatible hash. Each tested row reads its field once; membership comparisons are logarithmic in the number of retained candidates. Empty membership does not invoke a getter.

### Reproducible membership measurement

Run `dotnet run --project tests/benchmarks/MembershipBench.csproj -c Release`. To compare an independently built older Core DLL, build/run the same project with `-p:BaselineCore=/absolute/path/DataGridScene.Core.dll`. Build separately for each version to avoid stale outputs. The harness warms the predicate, runs five scans of 10,000 records against 1,024 candidates, and reports elapsed time, thread allocations, getter calls, comparisons and matching count. Fixture/index construction is outside the measured scan.

Same Debian x64 environment and .NET 10.0.12; PR #4 Core package versus the new source assembly:

| Per scan | Prior implementation | Indexed membership |
|---|---:|---:|
| Median CPU time | 232.6635 ms | 6.9369 ms |
| Allocated bytes | 234,087,928 | 240,304 |
| Computed-field reads | 9,716,224 | 10,000 |
| Comparisons | 9,716,224 | 98,990 |
| Matching records | 1,024 | 1,024 |

This instrumented CPU workload includes counters and a custom comparer. It is not a GPU, end-to-end UI, network or universal throughput benchmark. Correctness gates assert operation counts rather than machine-specific timing.

## Remaining filter boundaries

This is an independent checked-list increment, not the complete DevExpress Excel-style filter UI. The values come from the entire local table, not cascaded filters from other columns. Date hierarchy trees, format-condition filters, remote value providers and unrestricted million-value selection remain outside this change. Rendering and other grid compatibility limits are recorded in `compatibility.md`.

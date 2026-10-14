# Recent filters and expanded expressions

The behavioral/UI reference is the public [DevExpress WPF filter panel documentation](https://docs.devexpress.com/WPF/6409/controls-and-libraries/data-grid/visual-elements/common-elements/filter-panel): a bottom filter strip, temporary suspension, a recent-condition dropdown and an expandable expression. DataGridScene implements its own API, retained native controls and original vector icons. It is not an implementation of the vendor's complete API, templates or pixel-identical theme.

## Native UI

The existing filter strip retains its checkbox, individually removable highlighted tokens, Edit Filter and Clear commands. Its new dropdown opens a native Uno `MenuFlyout`; the active condition is checked. Standard popup keyboard navigation, Enter, Escape and pointer activation remain native. Menu items are built only when opened, never while painting, scrolling, changing selection or receiving display-only data updates. Long labels are bounded; tooltips and automation names contain the longer description. The menu inherits the grid's white/dark theme and scrolls within a bounded presenter.

The sample's dropdown beside search opens the same menu after Clear has collapsed the automatic strip. Clearing recent filters does not remove an active condition. Replacing the table clears its history and closes any old popup; stale identities cannot apply to a replacement table.

Expand exposes a selectable, wrapping native expression beneath the retained tokens. It has a 160-pixel maximum height with vertical scrolling and a 16,384-character display budget. Descriptions are not executable criteria and are never parsed for replay. Expanding/collapsing preserves token-control identities and does not reshape data. It is an independent full-expression view, not the vendor's complete highlighted wrap-token template system.

## Control API

```csharp
// Assign a table first. The default capacity is ten; zero clears/disables retention.
grid.FilterHistoryCapacity = 10; // Valid range: 0..100.
grid.SetColumnFilter("Region", "Europe");
long identity = grid.RecentFilters[0].Id;
grid.ClearFilterConditions();
grid.SetSearch("Alpine");
grid.ApplyRecentFilter(identity); // Restores conditions and enables filtering, keeps search.
grid.FilterPanel.IsExpanded = true;

// Optional application-owned toolbar, including when the panel is collapsed:
grid.ShowRecentFilters(toolbarButton);
grid.RemoveRecentFilter(identity);
grid.ClearFilterHistory();
```

A history entry owns base criteria, auto-filter texts and checked-value selections separately. Applying it retains the current search, sort, group, summary, selection, layout, theme and data rather than reverting an entire saved workspace. Existing rows may be hidden by the restored condition; selection identity retention follows the normal grid rules. Current enabled conditions avoid redundant query shaping and row-height reconstruction. Selecting a suspended condition enables it through the ordinary query-validation path.

`FilterHistoryRevision` changes only when the retained entry list changes. `RecentFilters` returns an immutable list snapshot. Repeated exact structural conditions keep their identity and move to the front; an already-current entry neither recreates the list nor increases its revision. Logical equivalence beyond transparent one-child AND wrappers is not inferred. Different typed representations can intentionally have separate identities.

Apply performs ordinary edit validation before attempting replay. Invalid pending edits stay available for correction. `GridView` validates and stages record-dependent predicate execution before the control changes its split metadata or MRU order. Failed predicates preserve the prior published query, filter UI and history. Successful pending edits are separate table commits; a later rejected query does not roll those writes back. Replay is rejected during `BeginDataUpdate`; normal batched filter edits remember only the successful outermost final state. Application callbacks must obey the existing synchronous, non-reentrant ownership contracts.

## Reusable core and precision

`GridFilterState.Capture(schema, criteria, textFilters, valueFilters)` and `GridFilterHistory` live in `DataGridScene.Core`, without Uno dependencies. Capture owns built-in field/group/NOT nodes, dictionaries, child lists and checked-value arrays. It does not enumerate records, invoke record getters, compile application predicates or call custom comparers. Bounded indexed collection reads avoid invoking a custom enumerator during structural comparisons.

Snapshots preserve CLR scalar types, all Int64/UInt64 bits, decimal bits/scale, binary floating-point bits, DateTime kind/ticks and DateTimeOffset ticks/offset. Scalar `JsonElement` values are cloned and survive disposal of their source document. Replay never takes a detour through a rounded/formatted menu label. Typed values, null and empty string remain distinct.

Retention is bounded to 1,024 AST nodes, depth 32, 8,192 literal slots and a 65,536-character typed signature per snapshot, plus the existing checked-value limit. Captures validate structure and schema names; record-dependent validity is checked by the view on application. Histories belong to one schema instance and do not own records or whole queries.

Application-defined predicates, mutable/app-defined literal objects and oversized snapshots are explicitly excluded from automatic history. Those filters remain usable through the normal query API; a history limitation does not make an already-published valid query fail. `FilterHistoryUnavailableReason` describes the exclusion. Capacity zero also releases the cached current snapshot and description cache. History is session/table-local, not persisted by grid workspace JSON; named/shared filters and provider-side history are not implemented here.

## Validation and performance interpretation

`FilterHistoryValidation` adds sixteen shared test cases to the managed and actual WASM suites. `FilterHistoryControlChecks` adds sixteen real Uno API cases, including predicate failures, nested/failed batches, unchanged state, precision boundaries, workspace independence, bounded retention, custom-predicate exclusion and retained native controls.

`tests/browser/filter-history.mjs` runs sixteen checks using actual pointer and keyboard input. It checks restore from both anchors, Home/ArrowDown/Enter and Escape, invalid native edits, narrow-window geometry, white/dark menu screenshots, expanded expressions, live-value updates, table replacement and a 100,000-row current-filter workload. Read-only snapshots expose existing bounds/counters; they do not open popups, force layout or edit values. All previous suites remain enabled in four isolated workers, and the new suite also runs against public Pages after deployment.

Operation-count assertions require no additional view refresh, row-height build or token rebuild for a current filter. This is not a claim of constant-time arbitrary filtering: a changed predicate still shapes the materialized table. Retention work depends on bounded condition size, not record count. Browser graphics use SwiftShader; timings are not hardware GPU throughput or end-to-end input latency. Consult a particular commit's reports before declaring its browser/deployment checks passed.

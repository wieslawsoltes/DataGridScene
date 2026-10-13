# Filter history review regressions

These fixes continue the filter-history implementation without relaxing its validation gate.

## Batched display work and cleared history

Previously every successful outer `BeginDataUpdate` scope called `RememberFilterState`, even when the scope changed only data, selection, search, sorting or filter-enabled state. Consequently an empty or data-only batch could recreate entries after the user explicitly cleared recent filters. The same path allocated and formatted another display snapshot unnecessarily. Intermediate filter mutations also installed renderer display dictionaries even though query publication was deferred.

The control now tracks filter-display and panel-only pending work separately. A successful outer batch publishes one final display and remembers it only when a filter mutation occurred. Failed filter batches restore the original display without retaining rejected or intermediate conditions. Data-only and empty batches do not allocate a new filter display, format descriptions or change MRU order. Enable-only batches refresh the checkbox/panel but do not repopulate history. `FilterDisplayRefreshCount` measures published snapshots; it does not count queued intermediate calls.

The existing sixteen Uno API cases now additionally cover twenty nested data-only batches, clear-history followed by search/sort/value/enable/empty batches, retained renderer display identity during intermediate filter mutations, exactly one final display write, and restoration after failed batches. These are extra assertions within the existing cases, not inflated scenario counts. Correctness and work counts must pass on the exact head; source inspection is not a runtime pass.

## Trimmed WASM diagnostic projection

Validate 38458835597 compiled the desktop/browser targets and passed core contracts, but its native browser worker failed with `ConstructorContainsNullParameterNames, DataGridScene.Controls.FilterPanelHistoryItem`. The newly added snapshot field directly serialized a Controls record. That library is trimmed; the diagnostic sample assembly is already explicitly rooted. Resolving the record's reflective JSON contract failed even for an empty history list, so unrelated existing suites could not obtain snapshots.

History diagnostics now project only the existing scalar ID, description and current flag into the diagnostic assembly, following the existing token/native-cell pattern. No control, selection or model is mutated by this projection. Trimming remains enabled, the Controls assembly is not newly rooted, and all original browser assertions remain. The complete published-WASM suites are required to establish that this fix works; prior non-trimmed offline UI checks did not test it. See the official [System.Text.Json reflection/source-generation guidance](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation).

## Effective predicate ownership

Base criteria, text maps and checked values were owned, but composing their effective AND through the general `GridFilter.And(params ...)` helper exposed its mutable array via `FilterGroup.Children`. A caller could replace a predicate in an allegedly immutable history snapshot without updating its typed identity. The composed root now has a private read-only collection as well. The shared ownership case attempts indexed replacement of both the effective root and base children, plus map mutations, and requires rejection with the original predicate references intact.

These changes do not add persistence, server-mode data, general logical equivalence, arbitrary application predicate retention or full vendor API/UI/accessibility parity. Changed predicates still reshape materialized data; operation counts are not hardware GPU or end-to-end latency measurements.

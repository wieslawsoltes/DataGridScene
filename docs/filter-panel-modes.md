# Filter panel presentation modes

The public [DevExpress WPF ShowFilterPanelMode reference](https://docs.devexpress.com/WPF/DevExpress.Xpf.Grid.DataViewBase.ShowFilterPanelMode) distinguishes conditional, always-visible and hidden filter panels. DataGridScene supplies an independent presentation API with the same three basic policies, not vendor API or pixel-identical compatibility.

## User interface

Open **Recent filters** beside the sample search box, then its **Filter panel** submenu:

- **Show when filtered** is the default. The strip follows the presence of conditions, including suspended conditions. Search alone does not show it.
- **Always show** retains the strip after Clear, displaying “No filter applied”. Edit Filter remains available; the recent-filter button remains available when history contains entries. Enable, Clear and Expand are disabled when there is no condition to act on.
- **Hide panel** removes the strip's occupied space but leaves filtering enabled and its conditions intact. The toolbar remains available to show it again.

The submenu uses native check items and follows the grid's theme. No extra permanent toolbar is added. Hiding/showing the strip does not execute filter, edit, history or data commands. An invalid pending cell value remains pending; selecting a visibility option does not commit or cancel it.

## Reusable API

```csharp
// Can be configured before assigning a GridTable.
grid.FilterPanelMode = FilterPanelDisplayMode.Always;

// The standalone panel exposes the same policy.
panel.DisplayMode = FilterPanelDisplayMode.Hidden;
panel.DisplayModeChanged += (_, mode) => SaveHostPreference(mode);
```

Values are `Automatic`, `Always` and `Hidden`. Invalid enum values are rejected before changing state. Assigning an unchanged mode is a no-op. `DisplayModeChanged` runs after the panel has a consistent new presentation state; application callbacks remain trusted synchronous code.

The policy survives table replacement. Table replacement still clears old filter history and closes stale menus. Workspace JSON and recent-filter replay intentionally do not overwrite the host's presentation preference. Hosts may persist that preference separately; these CLR properties are not a replacement for the complete DevExpress dependency-property/template API.

## Retained work

The panel creates one empty-state label and retains its token controls. Changing policy updates visibility and command availability, not filter snapshots or record shaping. Natural viewport resizing can update visible cell geometry; this is not a claim that changing occupied UI space requires no layout work. Query refresh counts, row-height reconstruction, token identity, history order, model values and undo remain independent of the policy.

## Validation

`FilterPanelModeControlChecks` contains eight real Uno API contracts: defaults, rejected/unchanged values, safe empty commands, non-destructive mode changes, pre-data configuration and table replacement, workspace independence, mode-only batches and history/enable independence.

The existing history browser harness retains its sixteen original checks and adds seven mode checks, including those eight API contracts. It uses actual menu hovering/clicks to change modes, verifies occupied viewport space, retained filters/search/selection, table replacement, invalid native input, and narrow/dark popup bounds. Native menu snapshots report actual focus/check/theme state; they never open menus, focus elements, force layout or edit values. Text replacement waits for both native TextBox selection/clearing and the real browser input, without retries.

The complete inventory becomes 301 interactions across eighteen harnesses, 411 shared managed/WASM contracts and 94 individual Uno API contracts, plus the separate fourteen-step deployment smoke. Inventory is not proof of a passing run; check the exact head's Actions results. Hosted rendering remains SwiftShader rather than hardware GPU performance evidence.

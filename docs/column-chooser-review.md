# Column chooser runtime review

## Evidence, not a successful compile alone

PR #18's initial head `772e6c16ed79c71221606ff96efa4a40e49b8685` passed core conformance (423 contracts), desktop compilation, browser publication and package builds in Validate **38541650715**. Actual chooser interaction validation then passed **15/20**, its embedded Uno API suite passed **11/16**, and all **12/12** new shared filter-execution contracts passed in the real WASM runtime. The features worker's remaining existing suites and separate 14-step artifact smoke passed. The failed chooser was not merged or relabeled successful.

The full failure snapshots, report, screenshots and trace were retained in `browser-validation-features`, artifact **11883447531**, SHA256 `73af5ae0d4180f805506e79eced14a012f9c10333262d3d8827ef09ca861d249`. Large 10,000-column snapshots made inline logs cumbersome. A temporary read-only workflow, **38544915740**, downloaded that exact artifact and printed bounded report/state fields without executing artifact code, editing results or repeating interactions. The temporary workflow was removed after diagnosis. Its successful extraction is not a test pass.

## Product corrections

### Width dirty state belongs to the property change

The original chooser observed `TextChanged`. The native WASM TextBox can deliver that event later than its Text property changes. This had two distinct consequences: a synchronous host setting `WidthText` and calling `CommitWidth` did not see a dirty edit, while a delayed notification for a chooser-owned display write could incorrectly mark untouched text dirty after the `_sync` scope ended.

Five Uno API contracts exposed the first problem. The actual conflict interaction exposed loss of pending text when an external width update arrived. Dirty tracking now listens to `TextBox.TextProperty` through `RegisterPropertyChangedCallback`, so it observes property changes synchronously and ignores our own writes while `_sync` is active. Existing immediate-API validation and conflict assertions remain; browser checks additionally require that opening and visiting an untouched width do not create pending edits.

The framework API is documented by [Microsoft](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.dependencyobject.registerpropertychangedcallback). This correction does not claim behavior on every native OS/runtime; the actual platform builds and interactions remain required.

### Keyboard ownership precedes ListViewItem handling

The initial pointer selection reached C00001, but Space did not change visibility: the item consumed the bubbling key event before the chooser's ListView handler. The chooser now handles Space on `PreviewKeyDown`, whose [documented routing strategy](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.previewkeydown) is tunneling. An event originating in an actual CheckBox remains owned by that native control, avoiding double activation. Arrow navigation stays native.

Width Enter/Escape also use the preview event so validation/cancellation owns those keys before a dialog-level action. The conflict test now uses actual Escape within the width field and requires that the dialog stays open with the current external width restored. The invalid-dismissal case independently retains Cancel-width-button coverage.

### A changed search must reset the old viewport

In the 10,000-column case, only seventeen presenters had been created and one was realized for the final search result. However, that result lay at y=37..77 while the visible list was y=317..597. The recycled native list retained its old scroll offset after the source shrank. A realized item is not necessarily a visible hit target.

The chooser resets its already-existing internal ScrollViewer before publishing a changed result source, with animation disabled. It does not force layout or realize containers. The test still requires an actual pointer click inside both browser and native list clips; no offscreen click, artificial enlargement or diagnostic ScrollIntoView is substituted.

### Late native lifecycle events cannot reacquire subscriptions

Disposal detaches external metadata listeners. Queued Loaded/DataContextChanged callbacks now also check the disposed owner before tracking a realized row or subscribing again. Normal recycled presenters retain stable metadata identities and detach when unloaded. This is a defensive lifecycle review in addition to the initially observed runtime failures.

## Contract correction, not a replay algorithm change

The long OR-filter replay kept every condition and matching row, but the original browser test compared the literal whole AST: initial `SetCriteria` had introduced a one-child AND wrapper which replay did not need. `GridFilterState.FiltersEqual` already documents that exact wrapper as transparent.

The revised assertion strips only outer one-child AND groups, then deeply compares all remaining OR/NOT/field/operator/literal structure. It separately compares filtered count, sampled matching record identities/versions/values, current history identity and all non-filter query options. It does not flatten OR groups, infer general logical equivalence, ignore literal types or change replay behavior.

## Diagnostic and validation boundaries

Native input helpers observe the particular focused TextBox and both native/browser selection state. Enum selection observes the three realized popup choices and focused index between actual keys. Those snapshots do not set focus, assign text, force layout or instantiate offscreen controls.

Timeout logs now contain a bounded state summary; full snapshots remain in files. All twenty chooser scenarios, sixteen Uno API cases, twelve shared execution cases, previous interaction harnesses and failed-operation assertions remain enabled. Current exact-head full validation must pass before merging. A previous source's successful contracts, screenshots or standalone compile do not establish that the corrections passed.

The preceding filter-history/panel-policy work is already merged separately: main `1c0ccf683467bd9cae07054ea53a2b42664f0227` passed Validate **38538465645** and all public Pages verification in **38541075399**. That public success is not evidence that this new chooser increment has deployed.

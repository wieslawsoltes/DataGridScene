# Row-aware templates and native-cell appearance

The reference is the publicly documented [DevExpress WPF CellTemplateSelector](https://docs.devexpress.com/WPF/DevExpress.Xpf.Grid.ColumnBase.CellTemplateSelector) and [separate display/edit templates](https://docs.devexpress.com/WPF/401011/controls-and-libraries/data-grid/data-editing-and-validation/modify-cell-values/assign-an-editor-to-a-cell). This is an independent Uno API, not source/API compatibility or a pixel-parity certification.

## Select ordinary controls by record

```csharp
var slider = GridCellTemplates.Slider();
var progress = GridCellTemplates.ProgressBar();
int statusIndex = grid.Table!.Schema["Progress"].Index;

grid.SetCellTemplate("Progress", progress);
grid.SetCellTemplateSelector("Progress", cell =>
    Convert.ToDouble(cell.Record[statusIndex]) < 100 && !cell.IsReadOnly
        ? slider : null);

var textEditor = GridCellTemplates.TextBox();
grid.SetCellEditTemplateSelector("OrderDate", cell =>
    Convert.ToDouble(cell.Record[statusIndex]) >= 100 ? textEditor : null);
```

The context supplies the actual record and column plus editing/read-only state. Return stable `GridCellTemplate` instances; do not allocate a fresh template on each selector invocation. Factories may create any normal Uno FrameworkElement, including content from `GridCellTemplate.FromDataTemplate`. Passing a null selector removes that selector. A null result falls back to the fixed template registered for that mode, then to the normal display or standard editor.

The **Adaptive cell editors** command beside the Native controls example demonstrates sliders and progress bars in different rows of the same column, and record-dependent date editors. The example leaves the same underlying model values, validation, grouping, summaries, and export behavior intact.

## Cache and lifecycle rules

Selectors run on the owning UI thread, outside painting, and only for viewport candidates or an explicitly activated editor. Results, including null results, are cached by record reference/version, column, mode and read-only state. Edits invalidate the affected visible record's choice; selection, hover and unchanged paints do not rerun selectors. Moving the viewport prunes offscreen choices. The visual pool retains at most 128 inactive controls; empty pool buckets are removed rather than retaining abandoned template objects indefinitely.

Selectors must not mutate the table or control, and must depend only on the supplied context or external configuration that the application explicitly invalidates. Call `InvalidateCellTemplates()` when external configuration changes. It does not imply another full data-shaping pass. Template factories and callbacks remain trusted application code.

A selector exception is surfaced through the grid's existing error reporting; selection of all candidate templates happens before replacing current hosts. A dirty editor retains its current template until commit or cancellation, including when an external row update changes the choice. It is not replaced merely because another template would now be selected. Existing identity/version conflict checks still apply.

`TemplateSelectorCallCount` and `CachedTemplateChoiceCount` expose selector activity. The browser tests use 100,000 records and verify that only viewport choices and controls are retained. These are operation-count and bounded-work contracts, not hardware GPU or end-to-end latency measurements.

## Selection and validation visuals

`GridCellTemplate.UpdateAppearance` is an optional callback receiving `GridCellVisualState`: selection, focused-cell, edit-mode, hover and dark-theme state. It is independent of value binding. A selection or hover change does not push a model value back into a native editor or disturb pending text.

The host paints a hit-test-transparent focus/error border above ordinary child controls, so opaque native content cannot hide the focused-cell outline. This decoration does not change the editor's measured size. Built-in text, number and choice cells use the grid selection tint; text content is vertically aligned with neighboring cells. Custom controls can implement their own appearance callback without changing data.

## Correctness/performance review

Unchanged `GridCellEditBuffer.TryCommit` and `Refresh` no longer emit unconditional PropertyChanged notifications. Repeated grid commands therefore do not rebind every visible native cell even when nothing changed. Identity validation still runs for no-op commits: removed/replaced records are not accepted simply because the buffer is pristine. Read-only and external-value changes notify when their state actually changes.

The shared managed/WASM inventory is 351 contracts. The additional selector browser harness has 14 scenarios, alongside the previous 140; the embedded native API suites still contain 33 contracts. The exact commit's CI reports determine pass/fail status. See [native-cell limitations](native-cells.md) for double-based editors, dates, trusted callbacks, explicit row heights and the remaining editor/automation/platform boundaries.

## Reproducible no-op operation counts

The identical compiled consumer in `tests/benchmarks/NativeBufferWorkload.cs` ran against the CI-built prior Core DLL at `80bbccae` and the reviewed Core source compiled with Roslyn on .NET 10.0.12. The harness performs 1,000 pristine commit/refresh pairs, excludes initial buffer construction from getter counts, and verifies the same stored value and clean state.

| Work | Previous implementation | Reviewed implementation |
|---|---:|---:|
| PropertyChanged notifications | 2,000 | 0 |
| Application value getter calls | 1,000 | 0 |
| Stored value | unchanged | unchanged |

These are deterministic operation counts, not timing estimates. The new contract suite independently checks notification suppression, read-only transitions, changed records and rejected replaced identities. To run the standalone caller, use its source as the Program.cs of a net10.0 console project referencing the Core package to compare. It is outside the main test project's compile glob.

Pages deployment also runs the complete 14-scenario selector browser harness on the actual public URL, after the original deployment marker and interaction checks. Its results and screenshots are retained in the deployed-site-verification artifact.

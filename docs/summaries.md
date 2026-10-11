# Total and group summaries

The behavioral reference is the public [DevExpress WPF group-summary documentation](https://docs.devexpress.com/WPF/6127/controls-and-libraries/data-grid/data-summaries/group-summary). These are independent DataGridScene APIs, not drop-in DevExpress types or a claim of pixel-identical UI.

## Configure independent aggregates

```csharp
using DataGridScene.Core;

var configuration = grid.View!.Query with
{
    Groups = new[] { new GroupDescriptor("Region") },
    Summaries = new[] { new SummaryDescriptor("Total", SummaryKind.Sum, "total-sales") },
    GroupSummaries = new[]
    {
        new SummaryDescriptor("OrderID", SummaryKind.Count, "orders")
        {
            ShowInColumn = "Company",
            ShowInGroupFooter = true,
            DisplayFormat = "Orders: {0:N0}"
        },
        new SummaryDescriptor("Total", SummaryKind.Sum, "sales")
        {
            ShowInGroupFooter = true,
            DisplayFormat = "Sales: {0:C2}"
        },
        new SummaryDescriptor("Total", SummaryKind.Average, "average")
        {
            ShowInGroupFooter = true,
            DisplayFormat = "Average: {0:C2}"
        }
    },
    GroupSummaryDisplay = GroupSummaryDisplayMode.AlignByColumns,
    GroupFooterDisplay = GroupFooterDisplayMode.ExpandedGroups,
    GroupSummarySorts = new[]
    {
        new GroupSummarySortDescriptor("Region", "sales", SortDirection.Descending),
        new GroupSummarySortDescriptor("Region", "orders", SortDirection.Descending)
    }
};
grid.SetQuery(configuration);
```

`Summaries` describes totals across filtered records. `GroupSummaries` describes aggregates computed independently for each grouping node, including parent groups. A null group collection retains legacy inheritance from total summaries; an explicit empty collection disables group summaries. The staged editor materializes group summaries as an independent collection when applied.

Summary keys are `Name` when supplied, otherwise `Field:Kind`. Keys must be unique within each collection. Explicit names allow multiple calculations of the same kind and keep sorting references stable when a field or calculation changes.

Count counts records, including null field values. Other built-in calculations skip nulls. Sum of an empty set is zero; average/minimum/maximum of an empty set is null; distinct count excludes null. Integral and decimal sums use checked decimal arithmetic. Double/float sums use compensated floating-point accumulation. Custom comparers also participate in minimum/maximum, distinct count and grouping equality.

## Presentation and sorting

`ShowInColumn` chooses the display column independently of the source field. `ShowInGroupRow` and `ShowInGroupFooter` control placement. Inline mode appends text to the group label; aligned mode allocates lines under the relevant columns. Multiple items assigned to the same column stack vertically, and shared geometry determines row heights, clipped painting and pointer hits.

`GroupFooterDisplay` accepts Hidden, ExpandedGroups or Always. Always shows a footer immediately under a collapsed group; it does not show invisible descendants' footers. Footers are non-data rows and cannot be selected or edited as records. They do not contribute to aggregates.

`GroupSummarySorts` supports multiple criteria per grouping level. Each entry references a grouped field and a group-summary key. Ties fall back to group-value order. Removing a grouping column through the control removes its now-obsolete summary-sort descriptors. Editing a record recalculates aggregates and ordering; undo/redo follows the same path.

Formats accept `{0}` for the result, `{1}` for the source caption and `{2}` for the calculation kind. Formatting uses the schema culture. Configuration and result formatting are checked during shaping before publishing a new view, not deferred until paint. Collection, format-length, alignment and JSON limits reject excessive input rather than silently truncating it.

## Reusable staged editor

```csharp
// Open the control's summary dialog.
grid.ShowSummaryEditor(groups: true);

// Or embed the reusable editor in another Uno host.
var editor = new DataGridScene.Controls.SummaryEditor(
    grid.Table!.Schema, grid.View!.Query, groups: true);
editor.ApplyRequested += (_, _) => grid.ApplySummaries(editor.BuildQuery());
```

The editor supports total/group scope, adding/removing items, choosing source/calculation/target, formatting, group-row/footer visibility, footer mode and primary sorting per grouping level. Invalid configuration remains in the dialog with an error; Cancel leaves the bound grid unchanged. Applying summary configuration retains the existing base criteria and auto-filter-row state.

The compact sort UI edits the primary criterion. Existing secondary criteria are preserved during formatting/placement edits or primary-direction changes. Selecting a different primary criterion, or group-value order, intentionally replaces that level's previous criteria. Full visual secondary-criteria editing remains outside this increment.

## Custom accumulators

Custom summaries are application code registered on the schema before binding, not evaluated strings. A factory must return a fresh accumulator for every calculation. Do not mutate the registry while a view uses it.

```csharp
sealed class WeightedAverage : IGridSummaryAccumulator
{
    private decimal weightedSum;
    private decimal weightSum;
    public void Add(GridRecord record, object? value)
    {
        if (value is not decimal amount) return;
        decimal weight = Convert.ToDecimal(record[0]); // This schema's weight field.
        weightedSum += amount * weight;
        weightSum += weight;
    }
    public object? Complete() => weightSum == 0 ? null : weightedSum / weightSum;
}

var schema = new GridSchema(new[]
{
    new GridColumn("Weight", typeof(int)),
    new GridColumn("Region", typeof(string)),
    new GridColumn("Amount", typeof(decimal))
})
{
    CustomSummaries = new Dictionary<string, Func<IGridSummaryAccumulator>>
    {
        ["weighted-average"] = () => new WeightedAverage()
    }
};
var summary = new SummaryDescriptor("Amount", SummaryKind.Custom, "weighted")
{
    CustomAggregate = "weighted-average"
};
```

An accumulator implementing `IDisposable` is disposed after completion or failure. Factory IDs, not delegates, are persisted in layout JSON. Restoration requires the same ID to exist in the destination schema. Accumulators are synchronous trusted extensions; their exceptions reject query application, and they are not sandboxed or automatically made thread-safe.

## Current boundaries

The visual control remains backed by a materialized local table. The independent record-page source explicitly rejects group rows, group summaries and footers because its response contract cannot represent them. This work does not establish seamless server-mode integration.

CSV, HTML and XLSX exports now preserve hierarchical record order even when groups are collapsed, but they do not reproduce visual group headers, footer rows or summary styling. Full data-aware printing, running/window aggregates, asynchronous incremental calculations, arbitrary group-row templates, complete grid accessibility and DevExpress API/visual parity remain separate work.

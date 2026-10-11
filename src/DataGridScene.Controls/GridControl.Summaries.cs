using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    public SummaryEditor? ActiveSummaryEditor { get; private set; }
    public void ApplySummaries(GridQuery configuration)
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        // Do not clear base criteria or auto-filter text as SetQuery would.
        View!.SetQuery(View.Query with
        {
            Summaries = configuration.Summaries, GroupSummaries = configuration.GroupSummaries,
            GroupSummarySorts = configuration.GroupSummarySorts,
            GroupSummaryDisplay = configuration.GroupSummaryDisplay, GroupFooterDisplay = configuration.GroupFooterDisplay
        });
        ClearError();
    }
    public async void ShowSummaryEditor(bool groups = true)
    {
        if (ActiveSummaryEditor is not null) return;
        try
        {
            RequireView(); if (!CommitEdit()) return;
            var editor = new SummaryEditor(Table!.Schema, View!.Query, groups);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Summary editor", Content = editor, CloseButtonText = "Cancel" };
            editor.ApplyRequested += (_, _) =>
            {
                try { ApplySummaries(editor.BuildQuery()); dialog.Hide(); }
                catch (Exception error) { editor.ValidationMessage = error.Message; }
            };
            ActiveSummaryEditor = editor; Changed();
            await dialog.ShowAsync();
        }
        catch (Exception error) { Report(error); }
        finally { ActiveSummaryEditor = null; Changed(); }
    }
    public void SortGroupBySummary(string groupField, string? summaryKey, SortDirection direction = SortDirection.Ascending)
    {
        RequireView(); if (!CommitEdit()) return;
        var sorts = View!.Query.GroupSummarySorts.Where(sort => !sort.Field.Equals(groupField, StringComparison.OrdinalIgnoreCase)).ToList();
        if (summaryKey is not null) sorts.Add(new(groupField, summaryKey, direction));
        View.SetQuery(View.Query with { GroupSummarySorts = sorts }); ClearError();
    }
    private bool TrySummaryContext(GridHit hit, Point point)
    {
        if (hit.Region is not (HitRegion.Summary or HitRegion.GroupRow or HitRegion.GroupSummary or HitRegion.GroupFooter)) return false;
        var menu = new MenuFlyout();
        bool grouped = hit.Region != HitRegion.Summary;
        menu.Items.Add(Menu(grouped ? "Edit group summaries…" : "Edit total summaries…", () => ShowSummaryEditor(grouped)));
        if (grouped && hit.RowIndex >= 0)
        {
            var group = View!.Rows[hit.RowIndex].Group!;
            menu.Items.Add(Menu("Sort by group values", () => SortGroupBySummary(group.Field, null)));
            foreach (var summary in View.Query.EffectiveGroupSummaries.Where(item => hit.SummaryKey is null || item.Key == hit.SummaryKey))
            {
                string caption = $"{Table!.Schema[summary.Field].Caption} · {summary.Kind}";
                menu.Items.Add(Menu(caption + " ascending", () => SortGroupBySummary(group.Field, summary.Key)));
                menu.Items.Add(Menu(caption + " descending", () => SortGroupBySummary(group.Field, summary.Key, SortDirection.Descending)));
            }
        }
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = point });
        return true;
    }
}

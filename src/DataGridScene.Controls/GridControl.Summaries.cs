using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    public SummaryEditor? ActiveSummaryEditor { get; private set; }
    private ContentDialog? _summaryDialog;
    private MenuFlyout? _summaryMenu;
    /// <summary>Bounds of real loaded summary controls for automation hosts; no simulated actions.</summary>
    public IReadOnlyDictionary<string, RectD> SummaryActionBounds(UIElement relativeTo)
    {
        var result = ActiveSummaryEditor is null ? new Dictionary<string, RectD>() :
            new Dictionary<string, RectD>(ActiveSummaryEditor.ActionBounds(relativeTo));
        void Add(string key, FrameworkElement element)
        {
            if (!element.IsLoaded || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var point = element.TransformToVisual(relativeTo).TransformPoint(new Point());
            result[key] = new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        void FindPrimary(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button && (button.Name == "PrimaryButton" || Equals(button.Content, "Apply summaries"))) Add("summary-apply", button);
                else FindPrimary(child);
            }
        }
        if (_summaryDialog is not null) FindPrimary(_summaryDialog);
        if (_summaryMenu is not null)
            foreach (var item in _summaryMenu.Items.OfType<MenuFlyoutItem>()) Add("menu-summary-" + item.Text, item);
        return result;
    }
    public void ApplySummaries(GridQuery configuration)
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        // Do not clear base criteria or auto-filter text as SetQuery would.
        View!.SetQuery(View.RequestedQuery with
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
            var editor = new SummaryEditor(Table!.Schema, View!.Query, groups) { ShowApplyButton = false };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Summary editor", Content = editor,
                PrimaryButtonText = "Apply summaries", CloseButtonText = "Cancel" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { ApplySummaries(editor.BuildQuery()); }
                catch (Exception error) { args.Cancel = true; editor.ValidationMessage = error.Message; }
            };
            _summaryDialog = dialog; ActiveSummaryEditor = editor; Changed();
            await dialog.ShowAsync();
        }
        catch (Exception error) { Report(error); }
        finally { _summaryDialog = null; ActiveSummaryEditor = null; Changed(); }
    }
    public void SortGroupBySummary(string groupField, string? summaryKey, SortDirection direction = SortDirection.Ascending)
    {
        RequireView(); if (!CommitEdit()) return;
        var sorts = View!.RequestedQuery.GroupSummarySorts.Where(sort => !sort.Field.Equals(groupField, StringComparison.OrdinalIgnoreCase)).ToList();
        if (summaryKey is not null) sorts.Add(new(groupField, summaryKey, direction));
        View.SetQuery(View.RequestedQuery with { GroupSummarySorts = sorts }); ClearError();
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
        _summaryMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_summaryMenu, menu)) _summaryMenu = null; };
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = point });
        return true;
    }
}

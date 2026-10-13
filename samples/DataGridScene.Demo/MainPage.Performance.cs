using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _liveTick;
    private long _controlEvents;
    public bool LiveUpdatesRunning => _liveTimer.IsEnabled;
    public int LiveTick => _liveTick;
    public long ControlEvents => _controlEvents;

    private void InitializePerformanceUI()
    {
        if (_navigation.Child is not ScrollViewer { Content: StackPanel navigation }) return;
        var scenario = Button("scenario-Live updates", "↻   Live updates", ApplyLiveScenario);
        scenario.Height = scenario.MinHeight = scenario.MaxHeight = 40;
        scenario.Padding = new Thickness(12,4,8,4);
        scenario.HorizontalAlignment = HorizontalAlignment.Stretch;
        scenario.HorizontalContentAlignment = HorizontalAlignment.Left;
        navigation.Children.Insert(navigation.Children.IndexOf(_actions["scenario-Performance"]), scenario);
        int index = navigation.Children.IndexOf(_actions["summaries"]) + 1;
        foreach (var button in new[] {
            Button("live-toggle", "Start / stop live updates", ToggleLiveUpdates),
            Button("live-once", "Update 100 display values", ApplyLiveTick),
            Button("live-batch", "Batch 1,000 value edits", ApplyValueBatch) })
        {
            button.Height = button.MinHeight = button.MaxHeight = 32;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            navigation.Children.Insert(index++, button);
        }
        _liveTimer.Tick += (_, _) => Run(ApplyLiveTick);
        DataGrid.StateChanged += (_, _) => _controlEvents++;
        Unloaded += (_, _) => StopLiveUpdates();
    }
    private void StopLiveUpdates() => _liveTimer.Stop();
    private void ApplyLiveScenario()
    {
        if (!DataGrid.CommitEdit()) return;
        Reload(100000); Scenario = "Live updates"; _liveTick = 0;
        using (DataGrid.BeginDataUpdate())
        {
            DataGrid.ShowBands = false;
            foreach (string field in new[] { "Salesperson", "OrderDate", "UnitPrice", "Discount", "Progress", "Verified", "Trend" })
                DataGrid.ColumnLayout!.SetVisible(field, false);
            DataGrid.SetQuery(DefaultQuery() with { Sorts = [new("Total", SortDirection.Descending)] });
        }
        SetMessage("Live updates ready: display edits retain sort/summary state. Batch edits refresh shaping once.");
    }
    private void ToggleLiveUpdates()
    {
        if (_liveTimer.IsEnabled) { StopLiveUpdates(); SetMessage("Live updates paused."); return; }
        if (Scenario != "Live updates") ApplyLiveScenario();
        if (Scenario == "Live updates" && !DataGrid.IsReadOnly)
        { _liveTimer.Start(); SetMessage("Live updates active. Editing pauses incoming ticks; leaving the scenario stops them."); }
    }
    private void ApplyLiveTick()
    {
        if (DataGrid.IsEditing || DataGrid.ActiveRowEditForm is not null || DataGrid.IsReadOnly || DataGrid.Table is null) return;
        _liveTick++;
        var edits = DataGrid.View!.OrderedRecords.Take(100).Select(record =>
            new CellEdit(record.Id, "Company", $"Live order {record.Id} · tick {_liveTick}")).ToArray();
        DataGrid.Table.Apply(edits);
        RefreshMetrics();
    }
    private void ApplyValueBatch()
    {
        if (DataGrid.IsReadOnly || !DataGrid.CommitEdit() || DataGrid.Table is null) return;
        StopLiveUpdates();
        long before = DataGrid.View!.RefreshCount;
        var records = DataGrid.Table.Records.Take(1000).ToArray();
        using (DataGrid.BeginDataUpdate())
            foreach (var record in records)
                DataGrid.Table.Apply([new(record.Id, "Quantity", (int)record[6]! % 100000 + 1)]);
        SetMessage($"Applied {records.Length:N0} independent edits with {DataGrid.View.RefreshCount - before} shaping refresh.");
        RefreshMetrics();
    }
}

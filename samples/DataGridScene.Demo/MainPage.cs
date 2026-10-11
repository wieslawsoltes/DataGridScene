using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Export;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;

namespace DataGridScene.Demo;

public sealed partial class MainPage : Page
{
    public GridControl DataGrid { get; } = new();
    private readonly Grid _shell = new();
    private readonly Border _navigation;
    private readonly Border _properties;
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _metrics = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Brush("#777777") };
    private readonly TextBlock _filterDescription = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { PlaceholderText = "Search all columns…", Width = 240, MinHeight = 30, Padding = new Thickness(10, 3, 10, 3), FontSize = 13 };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, FrameworkElement> _actions = new(StringComparer.Ordinal);
    private string? _savedLayout;
    private bool _loading;
    private bool _bridgeInstalled;
    private bool _initializing;
    public string Scenario { get; private set; } = "Overview";
    public bool Ready { get; private set; }
    public string? Message { get; private set; }
    public int RecordCount => DataGrid.Table?.Records.Count ?? 0;
    private static readonly Dictionary<string, SolidColorBrush> ChromeBrushes = new(StringComparer.OrdinalIgnoreCase);
    public static SolidColorBrush Brush(string hex)
    {
        if (!ChromeBrushes.TryGetValue(hex, out var brush))
        {
            uint value = Convert.ToUInt32(hex.TrimStart('#'), 16);
            ChromeBrushes.Add(hex, brush = new(Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)));
        }
        return brush;
    }
    public MainPage()
    {
        RequestedTheme = ElementTheme.Light; Background = Brush("#ffffff");
        Resources["ControlCornerRadius"] = new CornerRadius(1);
        Resources["OverlayCornerRadius"] = new CornerRadius(2);
        _shell.RowDefinitions.Add(new() { Height = new GridLength(32) });
        _shell.RowDefinitions.Add(new() { Height = new GridLength(132) });
        _shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _shell.RowDefinitions.Add(new() { Height = new GridLength(28) });
        _shell.ColumnDefinitions.Add(new() { Width = new GridLength(214) });
        _shell.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _shell.ColumnDefinitions.Add(new() { Width = new GridLength(222) });
        BuildTitleBar(); BuildMenu();
        _navigation = BuildNavigation(); Grid.SetRow(_navigation, 2); _shell.Children.Add(_navigation);
        var work = BuildWorkArea(); Grid.SetRow(work, 2); Grid.SetColumn(work, 1); _shell.Children.Add(work);
        _properties = BuildProperties(); Grid.SetRow(_properties, 2); Grid.SetColumn(_properties, 2); _shell.Children.Add(_properties);
        var statusBar = new Border { Background = Brush("#f3f3f3"), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush("#dedede"), Padding = new Thickness(14, 0, 14, 0), Child = _status };
        Grid.SetRow(statusBar, 3); Grid.SetColumnSpan(statusBar, 3); _shell.Children.Add(statusBar); Content = _shell;
        DataGrid.DetailProvider = OrderData.Details;
        InitializeSummaryUI(); InitializePerformanceUI(); DecorateNavigation();
        DataGrid.FilterEditorRequested += (_, _) => ShowFilters();
        DataGrid.StateChanged += (_, _) => RefreshStatus();
        DataGrid.Error += (_, message) => { Message = message; RefreshStatus(); };
        DataGrid.SearchRequested += (_, _) => _search.Focus(FocusState.Keyboard);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Run(() => DataGrid.SetSearch(_search.Text)); };
        _search.TextChanged += (_, _) => { if (!_loading) { _searchTimer.Stop(); _searchTimer.Start(); } };
        _search.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { _searchTimer.Stop(); Run(() => DataGrid.SetSearch(_search.Text)); args.Handled = true; } };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        SizeChanged += (_, args) =>
        {
            bool narrow = _fullWidth || args.NewSize.Width < 1000; bool compact = _fullWidth || args.NewSize.Width < 760;
            _shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 214);
            _navigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _shell.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 222);
            _properties.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            _search.Width = compact ? 170 : 240;
        };
        Loaded += async (_, _) =>
        {
            if (_initializing) return;
            _initializing = true;
            try
            {
                if (!Ready)
                {
                    await DataGrid.LoadTypefaceAsync(new Uri("ms-appx:///Uno.Fonts.OpenSans/Fonts/OpenSans-Regular.ttf"));
                    Reload(10000);
                }
                _metricsTimer.Start();
            }
            catch (Exception error) { SetMessage("Initialization failed: " + error.Message); }
            finally
            {
                _initializing = false;
                if (!_bridgeInstalled) { _bridgeInstalled = true; InstallBrowserBridge(); }
            }
        };
        Unloaded += (_, _) => { _metricsTimer.Stop(); _searchTimer.Stop(); };
    }
    private void BuildTitleBar()
    {
        var title = new Grid { Background = Brush("#f5f5f5"), Padding = new Thickness(10, 0, 12, 0) };
        title.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title.Children.Add(new VectorIcon(SceneGlyph.Table, 20) { VerticalAlignment = VerticalAlignment.Center });
        var caption = new TextBlock { Text = "DataGridScene — Data Grid", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(caption, 1); title.Children.Add(caption);
        var version = new TextBlock { Text = "Uno Platform · independent implementation", FontSize = 10, Foreground = Brush("#777777"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(version, 2); title.Children.Add(version); Grid.SetColumnSpan(title, 3); _shell.Children.Add(title);
    }
    private Button Button(string name, string text, Action action, bool compact = true)
    {
        var button = new Button { Content = text, Padding = new Thickness(compact ? 10 : 14, 4, compact ? 10 : 14, 4), MinHeight = 29, FontSize = 12, BorderThickness = new Thickness(0), Background = Brush("#f5f5f5") };
        AutomationProperties.SetAutomationId(button, name); AutomationProperties.SetName(button, text);
        button.Click += (_, _) => Run(action); _actions[name] = button; return button;
    }
    private void BuildMenu() => BuildCommandChrome();
    private Border BuildNavigation()
    {
        var navigation = new StackPanel { Spacing = 4, Padding = new Thickness(10, 18, 10, 12) };
        navigation.Children.Add(new TextBlock { Text = "DATA GRID", Margin = new Thickness(12, 0, 0, 14), FontSize = 11, CharacterSpacing = 80, Foreground = Brush("#777777"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var (name, glyph) in new[] { ("Overview", "▦"), ("Grouping", "▤"), ("Filtering", "⌕"), ("Editing", "✎"), ("Cell selection", "▧"), ("Conditional formatting", "◐"), ("Master-detail", "⊞"), ("Column bands", "▥"), ("Nested bands", "▥"), ("Performance", "↗") })
        {
            var button = Button("scenario-" + name, glyph + "   " + name, () => ApplyScenario(name), false);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Background = name == "Overview" ? Brush("#e4eef7") : Brush("#f7f7f7"); button.Padding = new Thickness(12, 10, 8, 10); navigation.Children.Add(button);
        }
        navigation.Children.Add(new Border { Height = 1, Background = Brush("#dedede"), Margin = new Thickness(10, 16, 10, 12) });
        navigation.Children.Add(new TextBlock { Text = "REUSABLE PACKAGES", Margin = new Thickness(12, 0, 0, 6), FontSize = 10, CharacterSpacing = 70, Foreground = Brush("#777777") });
        navigation.Children.Add(new TextBlock { Text = "Core & data operations\nVirtualized layout\nSkia rendering\nUno controls\nData-aware exports", Margin = new Thickness(12, 0, 0, 0), FontSize = 12, Foreground = Brush("#777777"), LineHeight = 25 });
        return new Border { Background = Brush("#f7f7f7"), BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Brush("#dedede"), Child = navigation };
    }
    private Grid BuildWorkArea()
    {
        var area = new Grid { Padding = new Thickness(6, 5, 6, 6), RowSpacing = 5 };
        area.RowDefinitions.Add(new() { Height = new GridLength(38) });
        area.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        area.RowDefinitions.Add(new() { Height = new GridLength(24) });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _subtitle.FontSize = 12; _subtitle.VerticalAlignment = VerticalAlignment.Center; _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.Margin = new Thickness(6, 0, 6, 0); heading.Children.Add(_subtitle);
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _search.Height = 28; _search.MinHeight = 28; _search.CornerRadius = new CornerRadius(1); searchPanel.Children.Add(_search);
        var clear = Button("clear", "×", ClearFilters); clear.Width = 30; clear.Padding = new Thickness(3); AutomationProperties.SetName(clear, "Clear filters and search"); searchPanel.Children.Add(clear);
        Grid.SetColumn(searchPanel, 1); heading.Children.Add(searchPanel); _actions["search"] = _search; area.Children.Add(heading);
        var border = new Border { BorderThickness = new Thickness(1), BorderBrush = Brush("#cfcfcf"), Child = DataGrid };
        Grid.SetRow(border, 1); area.Children.Add(border);
        Grid.SetRow(_filterDescription, 2); area.Children.Add(_filterDescription); return area;
    }
    private Border BuildProperties()
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(16, 18, 16, 12) };
        panel.Children.Add(new TextBlock { Text = "OPTIONS", FontSize = 11, CharacterSpacing = 80, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("#777777"), Margin = new Thickness(0, 0, 0, 10) });
        void Toggle(string name, bool initial, Action<bool> action)
        {
            var check = new CheckBox { Content = name, IsChecked = initial, FontSize = 12, MinHeight = 29 };
            check.Click += (_, _) => Run(() => action(check.IsChecked == true)); panel.Children.Add(check);
            _optionChecks[name] = check; _actions["option-" + name] = check;
        }
        Toggle("Group panel", true, value => DataGrid.ShowGroupPanel = value);
        Toggle("Auto-filter row", true, value => DataGrid.ShowFilterRow = value);
        Toggle("Column bands", false, value => DataGrid.ShowBands = value);
        Toggle("Read-only", false, value => DataGrid.IsReadOnly = value);
        var selectionMode = new ComboBox { ItemsSource = Enum.GetValues<GridSelectionMode>(), SelectedItem = GridSelectionMode.MultipleRows, HorizontalAlignment = HorizontalAlignment.Stretch };
        selectionMode.SelectionChanged += (_, _) => { if (!_syncingOptions && selectionMode.SelectedItem is GridSelectionMode mode) DataGrid.SelectionMode = mode; };
        panel.Children.Add(new TextBlock { Text = "Selection mode", FontSize = 12 }); panel.Children.Add(selectionMode);
        _selectionOptions = selectionMode; _actions["selection-mode"] = selectionMode;
        Toggle("Compact rows", true, value => DataGrid.RowHeight = value ? 28 : 36);
        Toggle("Dark grid", false, value => { DataGrid.DarkTheme = value; });
        panel.Children.Add(new TextBlock { Text = "DATASET SIZE", FontSize = 10, CharacterSpacing = 70, Margin = new Thickness(0, 16, 0, 3), Foreground = Brush("#777777") });
        var size = new ComboBox { ItemsSource = new[] { "1,000 records", "10,000 records", "100,000 records" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        size.SelectionChanged += (_, _) => { if (!_loading && !_syncingOptions && size.SelectedIndex >= 0) Run(() => Reload(size.SelectedIndex == 0 ? 1000 : size.SelectedIndex == 1 ? 10000 : 100000)); }; panel.Children.Add(size);
        _sizeOptions = size; _actions["dataset-size"] = size;
        panel.Children.Add(Button("save-layout", "Save layout", SaveLayout)); panel.Children.Add(Button("restore-layout", "Restore layout", RestoreLayout));
        panel.Children.Add(new Border { Height = 1, Background = Brush("#dedede"), Margin = new Thickness(0, 12, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "RENDERING", FontSize = 10, CharacterSpacing = 70, Foreground = Brush("#777777") }); panel.Children.Add(_metrics);
        panel.Children.Add(new TextBlock { Text = "Arrow keys: navigate\nF2 / Enter: edit cell\nF4: expand detail\nShift: range selection\nCtrl+C / V: copy / paste\nCtrl+Z / Y: undo / redo", FontSize = 11, Foreground = Brush("#777777"), LineHeight = 20, Margin = new Thickness(0, 12, 0, 0) });
        return new Border { Background = Brush("#fafafa"), BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Brush("#dedede"), Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }
    private GridQuery DefaultQuery() => new() { Summaries = [new("OrderID", SummaryKind.Count), new("Quantity", SummaryKind.Sum), new("Total", SummaryKind.Sum)] };
    public void Reload(int count)
    {
        StopLiveUpdates();
        if (!DataGrid.CommitEdit()) return;
        _searchTimer.Stop();
        _loading = true; Ready = false;
        try
        {
            DataGrid.SelectionMode = GridSelectionMode.MultipleRows;
            DataGrid.Table = OrderData.Create(count); DataGrid.ColumnLayout!.SetPin("OrderID", ColumnPin.Left);
            DataGrid.ColumnLayout.SetPin("Status", ColumnPin.Right); DataGrid.ColumnLayout.SetVisible("Notes", false);
            DataGrid.Renderer.ConditionalFormats.Clear();
            DataGrid.Renderer.ConditionalFormats.Add(new((record, column) => column.Field == "Total" && (decimal)column.GetValue(record)! > 20000m, new(SKColor.Parse("#fff1eb"), SKColor.Parse("#ac4a20"))));
            DataGrid.SetQuery(DefaultQuery()); _search.Text = ""; Scenario = "Overview"; Message = null; Ready = true;
        }
        finally { _loading = false; RefreshStatus(); }
    }
    public void ApplyScenario(string name)
    {
        StopLiveUpdates();
        if (!DataGrid.CommitEdit()) return;
        _searchTimer.Stop();
        Scenario = name; Message = null;
        _loading = true; _search.Text = ""; _loading = false;
        if (name == "Performance") { Reload(100000); Scenario = name; }
        DataGrid.ShowBands = name is "Column bands" or "Nested bands";
        DataGrid.IsReadOnly = false;
        DataGrid.SelectionMode = name == "Cell selection" ? GridSelectionMode.Cells : GridSelectionMode.MultipleRows;
        var query = DefaultQuery();
        if (name == "Grouping") query = query with { Groups = [new("Region"), new("Status")] };
        if (name == "Filtering") query = query with { Filter = GridFilter.And(new FieldFilter("Total", FilterOperator.GreaterOrEqual, 5000m), new FieldFilter("Status", FilterOperator.NotEqual, "Cancelled")), Sorts = [new("Total", SortDirection.Descending)] };
        DataGrid.SetQuery(query);
        if (name == "Nested bands")
        {
            foreach (var column in DataGrid.Table!.Schema.Columns)
            {
                string section = column.Field is "Company" or "Salesperson" ? "Contact" : "Location";
                if (column.Band == "Customer") DataGrid.ColumnLayout!.SetBandPath(column.Field, "Customer", section);
                else DataGrid.ColumnLayout!.SetBandPath(column.Field, column.Band ?? "General");
            }
        }
        else foreach (var column in DataGrid.Table!.Schema.Columns)
            DataGrid.ColumnLayout!.SetBandPath(column.Field, column.Band ?? "General");
        if (name == "Master-detail") foreach (var row in DataGrid.View!.FilteredRecords.Take(2)) if (!DataGrid.View.IsDetailExpanded(row.Id)) DataGrid.View.ToggleDetail(row.Id);
        if (name == "Conditional formatting") { if (DataGrid.View!.FilteredRecords.FirstOrDefault() is { } first) DataGrid.EnsureVisible(new(first.Id, "Total")); }
        foreach (var (key, element) in _actions) if (key.StartsWith("scenario-") && element is Button button) button.Background = key == "scenario-" + name ? Brush("#e4eef7") : Brush("#f7f7f7");
        RefreshStatus();
    }
    private void AddRecord()
    {
        if (!DataGrid.CommitEdit() || DataGrid.IsReadOnly) return;
        int index = checked((int)(DataGrid.Table!.Records.Select(record => record.Id).DefaultIfEmpty(10000).Max() - 10001 + 1));
        var row = OrderData.CreateRecord(index); DataGrid.Table.Add(row); ClearFilters();
        var cell = new CellAddress(row.Id, "Company"); DataGrid.Selection.Select(DataGrid.View!, cell); DataGrid.EnsureVisible(cell); DataGrid.BeginEdit(row.Id, "Company");
    }
    private async void DeleteSelected()
    {
        try
        {
            if (DataGrid.Selection.Count == 0 || DataGrid.IsReadOnly || !DataGrid.CommitEdit()) return;
            var ids = DataGrid.Selection.SelectedIds.ToArray();
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"Delete {ids.Length:N0} selected records?", Content = "This removes the records from the sample. Record deletion is not part of cell-edit undo history.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) DataGrid.Table!.Remove(ids);
        }
        catch (Exception error) { SetMessage(error.Message); }
    }
    public void ClearFilters() { if (!DataGrid.CommitEdit()) return; _searchTimer.Stop(); DataGrid.ClearFilters(); _loading = true; _search.Text = ""; _loading = false; Message = null; RefreshStatus(); }
    private void ToggleTheme() => DataGrid.DarkTheme = !DataGrid.DarkTheme;
    private void SaveLayout() { FlushPendingSearch(); _savedLayout = DataGrid.SaveLayout(); StoreLayout(_savedLayout); SetMessage("Layout saved, including columns, filters, groups and summaries."); }
    private void RestoreLayout()
    {
        string? layout = _savedLayout ?? ReadLayout();
        if (layout is null) { SetMessage("No saved layout yet."); return; }
        _searchTimer.Stop(); DataGrid.RestoreLayout(layout); SetMessage("Saved layout restored.");
    }
    private async void ShowColumns()
    {
        try { var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Column chooser", Content = new ColumnChooser(DataGrid.ColumnLayout!, DataGrid.Table!.Schema), CloseButtonText = "Done" }; await dialog.ShowAsync(); }
        catch (Exception error) { SetMessage(error.Message); }
    }
    private async void ShowFilters()
    {
        try
        {
            var builder = new FilterBuilder(DataGrid.Table!.Schema, DataGrid.View!.Query.Filter);
            var criteria = new TextBox { PlaceholderText = "[Total] >= 5000 AND [Status] <> 'Cancelled'", AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap, MinWidth = 580 };
            var tabs = new Pivot(); tabs.Items.Add(new PivotItem { Header = "Visual builder", Content = builder });
            var textPanel = new StackPanel { Spacing = 12 }; textPanel.Children.Add(criteria); textPanel.Children.Add(new TextBlock { Text = "Use AND / OR / NOT, comparisons, IN, BETWEEN, IS NULL, Contains, StartsWith and EndsWith.\nFields use [brackets]. Text values use 'quotes'.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            tabs.Items.Add(new PivotItem { Header = "Criteria expression", Content = textPanel });
            var errorText = new TextBlock { Foreground = Brush("#bb3333"), TextWrapping = TextWrapping.Wrap }; var content = new StackPanel { Spacing = 8 }; content.Children.Add(tabs); content.Children.Add(errorText);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Filter builder", Content = content, PrimaryButtonText = "Apply", SecondaryButtonText = "Clear", CloseButtonText = "Cancel" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { if (tabs.SelectedIndex == 1) DataGrid.SetCriteria(criteria.Text); else DataGrid.SetQuery(DataGrid.View.Query with { Filter = builder.BuildFilter() }); }
                catch (Exception error) { args.Cancel = true; errorText.Text = error.Message; }
            };
            dialog.SecondaryButtonClick += (_, _) => ClearFilters();
            _filterDialog = dialog;
            try { await dialog.ShowAsync(); } finally { _filterDialog = null; }
        }
        catch (Exception error) { SetMessage(error.Message); }
    }
    private async void ShowHelp()
    {
        try { await new ContentDialog { XamlRoot = XamlRoot, Title = "DataGridScene", CloseButtonText = "Close", Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Header click cycles ascending / descending / unsorted. Shift-click adds another sort column. Drag headers to reorder or into the group panel. Drag header separators to resize; double-click a header for best fit. Right-click headers for freezing, grouping and filtering.\n\nSelect cells with arrows and Shift. Ctrl+A selects all filtered rows. F2 or Enter edits; Enter commits; Escape cancels. F4 expands detail. Ctrl+C/V copies and pastes. Ctrl+Z/Y undo and redo cell edits.\n\nThis is an independent implementation. Full DevExpress behavior, API, visual and accessibility parity has not been established; see the repository compatibility matrix." } }.ShowAsync(); }
        catch (Exception error) { SetMessage(error.Message); }
    }
    private async void RunValidation()
    {
        try { var report = await DataGridScene.Validation.FilterPresentationValidationSuite.RunAsync(); SetMessage($"Conformance: {report.PassedCount}/{report.Results.Count} passed in {report.Milliseconds:N0} ms."); }
        catch (Exception error) { SetMessage(error.Message); }
    }
    public void Export(string format)
    {
        FlushPendingSearch();
        if (!DataGrid.CommitEdit()) return;
        var options = new ExportOptions { Fields = DataGrid.ColumnLayout!.DisplayColumns().Where(column => column.Field != "Trend").Select(column => column.Field).ToArray() };
        if (format == "xlsx") SaveFile("orders.xlsx", GridExporter.ToXlsx(DataGrid.View!, options), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        else if (format == "html") SaveFile("orders.html", System.Text.Encoding.UTF8.GetBytes(GridExporter.ToHtml(DataGrid.View!, options with { UseDisplayFormatting = true })), "text/html");
        else SaveFile("orders.csv", System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(GridExporter.ToCsv(DataGrid.View!, options))).ToArray(), "text/csv");
    }
    private void Run(Action action) { try { action(); } catch (Exception error) { SetMessage(error.Message); } finally { RefreshStatus(); } }
    public void SetMessage(string message) { Message = message; RefreshStatus(); }
    private void RefreshStatus()
    {
        if (DataGrid.View is null) return;
        RefreshOptions(); RefreshChrome();
        _subtitle.Text = $"{RecordCount:N0} orders · {Scenario} · virtualized rows and columns";
        _status.Text = Message ?? DataGrid.LastError ?? $"{DataGrid.View.FilteredRecords.Count:N0} of {RecordCount:N0} records     |     {(DataGrid.SelectionMode == GridSelectionMode.Cells ? $"{DataGrid.Selection.SelectedCellCount:N0} cells" : $"{DataGrid.Selection.Count:N0} rows")} selected     |     Skia canvas · Uno Platform";
        _filterDescription.Text = DataGrid.View.Query.Filter is null && DataGrid.View.Query.Search.Length == 0 ? "No filter applied     ·     Right-click a column header for sorting, grouping and layout options" : $"Filter active   ·   {DataGrid.View.FilteredRecords.Count:N0} matching records   ·   Use Clear to remove filters";
        if (_actions.TryGetValue("undo", out var undo)) ((Button)undo).IsEnabled = !DataGrid.IsReadOnly && DataGrid.History?.CanUndo == true;
        if (_actions.TryGetValue("redo", out var redo)) ((Button)redo).IsEnabled = !DataGrid.IsReadOnly && DataGrid.History?.CanRedo == true;
    }
    private void RefreshMetrics()
    {
        var renderer = DataGrid.Renderer;
        string text = $"Skia / HarfBuzz\n{renderer.RowsDrawn:N0} visible rows\n{renderer.CellsDrawn:N0} painted cells\n{renderer.CachedTextRuns:N0} cached text runs\n{renderer.LastDrawMilliseconds:F2} ms last CPU paint\nHardware depends on host\n{DataGrid.View?.RefreshCount:N0} shaping refreshes\n{DataGrid.View?.ValueUpdateCount:N0} values-only updates\nHeights: {DataGrid.HeightIndex.StorageMode}\n{DataGrid.View?.LastRefreshMilliseconds:F2} ms last shaping";
        if (_metrics.Text != text) _metrics.Text = text;
    }
    public IReadOnlyDictionary<string, RectD> ActionBounds()
    {
        var output = new Dictionary<string, RectD>();
        foreach (var (name, element) in _actions.Concat(DataGrid.FilterPanel.Actions))
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility != Visibility.Visible) continue;
            var point = element.TransformToVisual(this).TransformPoint(new Point(0, 0)); output[name] = new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        return output;
    }
    partial void InstallBrowserBridge();
    partial void StoreLayout(string json);
    private partial string? ReadLayout();
    private partial void SaveFile(string name, byte[] bytes, string mime);
}

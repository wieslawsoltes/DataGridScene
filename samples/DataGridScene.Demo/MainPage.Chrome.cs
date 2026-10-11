using DataGridScene.Controls;
using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private readonly CommandRibbon _ribbon = new();
    private readonly StackPanel _themeChoices = new() { Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(16, 8, 16, 8), Visibility = Visibility.Collapsed };
    private bool _themesVisible, _fullWidth, _shellDark;
    private ContentDialog? _filterDialog;
    private readonly List<VectorIcon> _navigationIcons = [];
    private readonly TextBox _exampleSearch = new() { PlaceholderText = "Search examples", MinHeight = 28, Height = 28, FontSize = 12, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(2, 0, 2, 12), CornerRadius = new CornerRadius(1) };
    private static readonly string[] ScenarioOrder = ["Overview", "Grouping", "Filtering", "Editing", "Cell selection", "Conditional formatting", "Master-detail", "Column bands", "Nested bands", "Group summaries", "Live updates", "Performance"];
    private void BuildCommandChrome()
    {
        var area = new Grid { Padding = new Thickness(5, 0, 5, 4), Background = Brush("#f5f5f5") };
        area.RowDefinitions.Add(new() { Height = new GridLength(28) }); area.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        void Menu(string caption, params (string Text, Action Run)[] actions)
        {
            var flyout = new MenuFlyout();
            foreach (var (text, action) in actions)
            {
                var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => Run(action); flyout.Items.Add(item);
            }
            tabs.Children.Add(new Button { Content = caption, Flyout = flyout, FontSize = 12, Padding = new Thickness(14, 2, 14, 2), BorderThickness = new Thickness(0), MinHeight = 26, Height = 26, Background = Brush("#f5f5f5") });
        }
        Menu("File", ("Export CSV", () => Export("csv")), ("Export Excel workbook", () => Export("xlsx")), ("Export printable HTML", () => Export("html")),
            ("Save layout file", () => SaveFile("grid-layout.json", System.Text.Encoding.UTF8.GetBytes(DataGrid.SaveLayout()), "application/json")));
        tabs.Children.Add(Button("tab-demo", "Demo", () => ShowRibbonTab(false)));
        tabs.Children.Add(Button("tab-themes", "Themes", () => ShowRibbonTab(true)));
        Menu("View", ("Column chooser", ShowColumns), ("Filter editor", ShowFilters), ("Toggle full-width workspace", ToggleFullWidth), ("Best fit all columns", () => DataGrid.BestFit()),
            ("Expand all groups", () => DataGrid.View?.ExpandAll()), ("Collapse all groups", () => DataGrid.View?.CollapseAll()));
        Menu("Help", ("Keyboard shortcuts and compatibility", ShowHelp), ("Run conformance checks", RunValidation));
        area.Children.Add(tabs);
        RibbonCommand Command(string id, string label, SceneGlyph icon, Action action) => new(id, label, icon, () => Run(action));
        _ribbon.AddGroup("Navigation", Command("previous-example", "Previous", SceneGlyph.Previous, () => NavigateScenario(-1)), Command("next-example", "Next", SceneGlyph.Next, () => NavigateScenario(1)));
        _ribbon.AddGroup("Clipboard", Command("undo", "Undo", SceneGlyph.Undo, () => { if (!DataGrid.IsReadOnly && DataGrid.CommitEdit()) DataGrid.History?.Undo(); }), Command("redo", "Redo", SceneGlyph.Redo, () => { if (!DataGrid.IsReadOnly && DataGrid.CommitEdit()) DataGrid.History?.Redo(); }),
            Command("copy", "Copy", SceneGlyph.Copy, DataGrid.CopySelection), Command("paste", "Paste", SceneGlyph.Paste, DataGrid.PasteClipboard));
        _ribbon.AddGroup("Data", Command("add", "New row", SceneGlyph.Add, AddRecord), Command("delete", "Delete", SceneGlyph.Delete, DeleteSelected), Command("filter", "Edit filter", SceneGlyph.Filter, ShowFilters), Command("columns", "Column\nchooser", SceneGlyph.Columns, ShowColumns));
        _ribbon.AddGroup("View", Command("best-fit", "Best fit", SceneGlyph.BestFit, () => DataGrid.BestFit()), Command("ribbon-bands", "Band\nheaders", SceneGlyph.Bands, () => DataGrid.ShowBands = !DataGrid.ShowBands), Command("full-width", "Full-width\nview", SceneGlyph.Expand, ToggleFullWidth));
        _ribbon.AddGroup("Export", Command("export", "CSV", SceneGlyph.Export, () => Export("csv")), Command("export-xlsx", "Excel", SceneGlyph.Table, () => Export("xlsx")), Command("export-html", "HTML", SceneGlyph.Export, () => Export("html")));
        _ribbon.AddGroup("Layout", Command("ribbon-save", "Save\nlayout", SceneGlyph.Save, SaveLayout), Command("ribbon-restore", "Restore\nlayout", SceneGlyph.Restore, RestoreLayout));
        _ribbon.CommandFailed += (_, error) => SetMessage(error.Message);
        foreach (var (id, button) in _ribbon.Commands) _actions[id] = button;
        Grid.SetRow(_ribbon, 1); area.Children.Add(_ribbon);
        void ThemeChoice(string id, string label, bool dark)
        {
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(new VectorIcon(SceneGlyph.Theme, 32) { HorizontalAlignment = HorizontalAlignment.Center, IsDark = dark });
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, TextAlignment = TextAlignment.Center });
            var button = Button(id, "", () => { DataGrid.DarkTheme = dark; RefreshChrome(); });
            button.Content = panel; button.Width = 142; button.Height = 76;
            button.Background = Brush(dark ? "#303030" : "#ffffff");
            if (dark) button.Foreground = Brush("#eeeeee");
            AutomationProperties.SetName(button, label); _themeChoices.Children.Add(button);
        }
        ThemeChoice("theme-white", "Office-style White", false); ThemeChoice("theme-dark", "Office-style Dark", true);
        _themeChoices.Children.Add(Button("density-compact", "Compact rows · 22 px", () => DataGrid.RowHeight = 22));
        _themeChoices.Children.Add(Button("density-standard", "Standard rows · 28 px", () => DataGrid.RowHeight = 28));
        Grid.SetRow(_themeChoices, 1); area.Children.Add(_themeChoices);
        Grid.SetRow(area, 1); Grid.SetColumnSpan(area, 3); _shell.Children.Add(area);
        ShowRibbonTab(false);
    }
    private void ShowRibbonTab(bool themes)
    {
        _themesVisible = themes;
        _themeChoices.Visibility = themes ? Visibility.Visible : Visibility.Collapsed;
        _ribbon.Visibility = themes ? Visibility.Collapsed : Visibility.Visible;
        foreach (string tab in new[] { "tab-demo", "tab-themes" })
        {
            var button = (Button)_actions[tab];
            button.BorderThickness = new Thickness(0, 0, 0, (tab == "tab-themes") == themes ? 2 : 0);
            button.BorderBrush = Brush("#1673b8"); button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
    }
    private void NavigateScenario(int delta)
    {
        int index = Array.IndexOf(ScenarioOrder, Scenario);
        ActivateScenario(ScenarioOrder[(Math.Max(0, index) + delta + ScenarioOrder.Length) % ScenarioOrder.Length]);
    }
    private void ActivateScenario(string scenario)
    {
        if (scenario == "Group summaries") ApplySummaryScenario();
        else if (scenario == "Live updates") ApplyLiveScenario();
        else ApplyScenario(scenario);
    }
    private void DecorateNavigation()
    {
        if (_navigation.Child is not ScrollViewer { Content: StackPanel navigation }) return;
        _actions["example-search"] = _exampleSearch;
        navigation.Children.Insert(0, _exampleSearch);
        _exampleSearch.TextChanged += (_, _) =>
        {
            foreach (string scenario in ScenarioOrder)
                _actions["scenario-" + scenario].Visibility = scenario.Contains(_exampleSearch.Text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        foreach (string scenario in ScenarioOrder)
        {
            var button = (Button)_actions["scenario-" + scenario];
            var icon = new VectorIcon(scenario switch { "Grouping" or "Group summaries" => SceneGlyph.Group, "Filtering" => SceneGlyph.Filter, "Editing" => SceneGlyph.Edit,
                "Column bands" or "Nested bands" => SceneGlyph.Bands, "Live updates" or "Performance" => SceneGlyph.Live, _ => SceneGlyph.Table }, 16);
            _navigationIcons.Add(icon);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(icon); panel.Children.Add(new TextBlock { Text = scenario, FontSize = 12, MaxWidth = 151, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            button.Content = panel; AutomationProperties.SetName(button, scenario);
            button.Padding = new Thickness(7, 4, 5, 4); button.CornerRadius = new CornerRadius(0);
            button.BorderBrush = Brush("#1673b8");
        }
        _navigation.Background = Brush("#f7f7f7");
    }
    private void ToggleFullWidth()
    {
        _fullWidth = !_fullWidth;
        bool compact = _fullWidth || ActualWidth < 760, narrow = _fullWidth || ActualWidth < 1000;
        _shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 214);
        _shell.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 222);
        _navigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _properties.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshChrome()
    {
        foreach (string scenario in ScenarioOrder)
            if (_actions.TryGetValue("scenario-" + scenario, out var element) && element is Button button)
                button.BorderThickness = new Thickness(Scenario == scenario ? 3 : 0, 0, 0, 0);
        bool dark = DataGrid.DarkTheme;
        if (_shellDark == dark) return;
        _shellDark = dark; RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        // Update shared sample brushes, including dialogs already using those brushes.
        // Controls remain native and inherit the requested theme for their own visual states.
        var darkColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["#ffffff"]="#202020", ["#f3f3f3"]="#282828", ["#f5f5f5"]="#292929", ["#f7f7f7"]="#252525", ["#fafafa"]="#272727",
            ["#777777"]="#bdbdbd", ["#dedede"]="#484848", ["#cfcfcf"]="#505050", ["#e4eef7"]="#304b60", ["#1673b8"]="#68b9ee"
        };
        foreach (var (original, brush) in ChromeBrushes)
        {
            string hex = dark ? darkColors.GetValueOrDefault(original, original) : original;
            uint color = Convert.ToUInt32(hex.TrimStart('#'), 16);
            brush.Color = Windows.UI.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        }
        _ribbon.SetTheme(dark); foreach (var icon in _navigationIcons) icon.IsDark = dark;
    }
    public object ChromeSnapshot() => new {
        ThemesVisible = _themesVisible, FullWidth = _fullWidth, ShellDark = _shellDark, ShellTheme = RequestedTheme.ToString(), FilterDialogOpen = _filterDialog is { IsLoaded: true },
        ExampleSearch = _exampleSearch.Text,
        VisibleExamples = ScenarioOrder.Where(name => _actions["scenario-" + name].Visibility == Visibility.Visible).ToArray(),
        CommandCount = _ribbon.Commands.Count
    };
}

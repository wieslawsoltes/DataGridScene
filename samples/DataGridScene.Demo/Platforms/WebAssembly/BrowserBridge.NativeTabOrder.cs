using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace DataGridScene.Demo;

public static partial class BrowserBridge
{
    [JSExport]
    public static string ValidateNativeTabOrder()
    {
        var results = NativeTabOrderValidation.Run();
        return JsonSerializer.Serialize(new ValidationReport(results, results.Sum(result => result.Milliseconds)), Json);
    }

    [JSExport]
    public static string SnapshotNativeTabOrder()
    {
        var grid = Page.DataGrid;
        var focused = grid.XamlRoot is null ? null : FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject;
        static bool Within(DependencyObject? child, DependencyObject parent)
        {
            for (; child is not null; child = VisualTreeHelper.GetParent(child))
                if (ReferenceEquals(child, parent)) return true;
            return false;
        }
        // Only inspect existing elements. This diagnostic never realizes, focuses,
        // rearranges or assigns TabIndex, even if a template has not loaded yet.
        var cells = grid.GetNativeCells().Select(info => {
            var element = grid.GetCellControl(info.Address.RecordId, info.Address.Field)!;
            var body = VisualTreeHelper.GetParent(element);
            var host = body is null ? null : VisualTreeHelper.GetParent(body) as UIElement;
            return new { address = info.Address, info.ControlType, info.Bounds, info.ClipBounds,
                tabIndex = host?.TabIndex, focused = Within(focused, element) };
        }).ToArray();
        return JsonSerializer.Serialize(new { cells, focusedCell = cells.FirstOrDefault(cell => cell.focused)?.address,
            focusedType = focused?.GetType().Name,
            focusedId = focused is null ? null : AutomationProperties.GetAutomationId(focused),
            writes = grid.CellControlTabIndexWriteCount,
            attached = grid.AttachedCellControlCount, created = grid.CreatedCellControlCount,
            attaches = grid.CellControlTreeAttachCount, detaches = grid.CellControlTreeDetachCount,
            binds = grid.CellControlBindCount, sizes = grid.CellControlSizeWriteCount,
            pooled = grid.PooledCellControlCount, realized = grid.RealizedCellControlCount,
            grid.HasPendingCellEdits, grid.LastError, diagnostics = NativeFocusDiagnostics() }, Json);
    }

    // Installed only when the browser harness requests diagnostics, never by the app.
    // Bounded read-only observations distinguish a bad candidate from duplicate input
    // or a focus redirect. They deliberately do not handle events or repair controls.
    private static readonly Queue<string> NativeFocusEvents = new();
    private static bool _nativeFocusTraceInstalled;
    private static string DescribeNativeFocus(DependencyObject? element)
    {
        if (element is null) return "null";
        var result = element.GetType().Name + "#" + AutomationProperties.GetAutomationId(element);
        if (element is UIElement ui)
            result += $"[tab={ui.TabIndex},stop={ui.IsTabStop},nav={ui.TabFocusNavigation},visible={ui.Visibility}]";
        if (element is FrameworkElement { DataContext: GridCellEditBuffer buffer })
            result += $"@{buffer.RecordId}/{buffer.Field}";
        return result;
    }
    private static void TraceNativeFocus(string text)
    {
        while (NativeFocusEvents.Count >= 32) NativeFocusEvents.Dequeue();
        NativeFocusEvents.Enqueue(text);
    }
    private static object NativeFocusDiagnostics()
    {
        var grid = Page.DataGrid;
        if (!_nativeFocusTraceInstalled)
        {
            _nativeFocusTraceInstalled = true;
            grid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
                TraceNativeFocus($"down {args.Key} handled={args.Handled} source={DescribeNativeFocus(args.OriginalSource as DependencyObject)}")), true);
            grid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
                TraceNativeFocus($"up {args.Key} handled={args.Handled} source={DescribeNativeFocus(args.OriginalSource as DependencyObject)}")), true);
            grid.GettingFocus += (_, args) => TraceNativeFocus($"getting {args.Direction}: {DescribeNativeFocus(args.OldFocusedElement)} -> {DescribeNativeFocus(args.NewFocusedElement)}");
            grid.LosingFocus += (_, args) => TraceNativeFocus($"losing {args.Direction}: {DescribeNativeFocus(args.OldFocusedElement)} -> {DescribeNativeFocus(args.NewFocusedElement)}");
        }
        string Candidate(FocusNavigationDirection direction)
        {
            try { return DescribeNativeFocus(FocusManager.FindNextElement(direction, new FindNextElementOptions { SearchRoot = grid })); }
            catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
        }
        var ancestry = new List<string>();
        for (var element = grid.XamlRoot is null ? null : FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject;
            element is not null && ancestry.Count < 16; element = VisualTreeHelper.GetParent(element))
            ancestry.Add(DescribeNativeFocus(element));
        var trees = new List<string>();
        void ReadTree(DependencyObject element, int depth)
        {
            if (depth > 10 || trees.Count >= 80) return;
            trees.Add(new string(' ', depth) + DescribeNativeFocus(element));
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ReadTree(VisualTreeHelper.GetChild(element, i), depth + 1);
        }
        foreach (var cell in grid.GetNativeCells().OrderBy(cell => cell.Bounds.Y).ThenBy(cell => cell.Bounds.X).Take(2))
            if (grid.GetCellControl(cell.Address.RecordId, cell.Address.Field) is { } element) ReadTree(element, 0);
        return new { next = Candidate(FocusNavigationDirection.Next), previous = Candidate(FocusNavigationDirection.Previous),
            ancestry, trees, events = NativeFocusEvents.ToArray() };
    }

    [JSExport]
    public static string ConfigureNativeTabTemplate(bool reverse, bool disableSecond)
    {
        try
        {
            Page.DataGrid.SetCellTemplate("Notes", new GridCellTemplate(() => {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var first = new Button { Content = "First", Width = 72, MinHeight = 0, Height = 28, Padding = new Thickness(2), TabIndex = reverse ? 20 : 10 };
                var second = new Button { Content = "Second", Width = 72, MinHeight = 0, Height = 28, Padding = new Thickness(2), TabIndex = reverse ? 10 : 20, IsEnabled = !disableSecond };
                AutomationProperties.SetAutomationId(first, "Composite-first");
                AutomationProperties.SetAutomationId(second, "Composite-second");
                panel.Children.Add(first); panel.Children.Add(second); return panel;
            }));
            return JsonSerializer.Serialize(new { ok = true }, Json);
        }
        catch (Exception error) { return JsonSerializer.Serialize(new { ok = false, error = error.Message }, Json); }
    }
}

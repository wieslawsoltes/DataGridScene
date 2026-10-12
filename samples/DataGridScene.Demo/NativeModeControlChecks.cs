using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Globalization;

namespace DataGridScene.Demo;

internal static class NativeModeControlChecks
{
    internal static ValidationReport Run()
    {
        var results = new List<ValidationResult>(); var total = Stopwatch.StartNew();
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Native mode contract failed."); }
        void Test(string name, Action run)
        {
            var start = Stopwatch.GetTimestamp();
            try { run(); results.Add(new(name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static TextBlock Text(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock text) return text;
                try { return Text(child); } catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("Native text element is missing.");
        }
        Test("native-mode.configuration-before-data-and-idempotence", () => {
            using var grid = new GridControl { CellRenderingMode = GridCellRenderingMode.NativeControls, Table = ValidationSuite.Fixture() };
            long refresh = grid.View!.RefreshCount; var rows = grid.View.Rows;
            for (int i = 0; i < 10; i++) grid.CellRenderingMode = GridCellRenderingMode.NativeControls;
            Check(grid.View.RefreshCount == refresh && ReferenceEquals(rows, grid.View.Rows));
        });
        Test("native-mode.switch-preserves-query-selection-and-history", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            grid.SetColumnFilter("Name", "Alpha"); grid.Selection.Select(grid.View!, new(1, "Amount"));
            grid.History!.Execute([new(1, "Amount", 123m)]); var view = grid.View; var query = view!.Query;
            grid.CellRenderingMode = GridCellRenderingMode.NativeControls; grid.CellRenderingMode = GridCellRenderingMode.Skia;
            Check(ReferenceEquals(view, grid.View) && ReferenceEquals(query, view.Query) && grid.History.CanUndo);
            Check(grid.Selection.FocusedCell == new CellAddress(1, "Amount"));
        });
        Test("native-mode.invalid-mode-rejected-without-changing-configuration", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture() };
            try { grid.CellRenderingMode = (GridCellRenderingMode)999; }
            catch (ArgumentOutOfRangeException) { Check(grid.CellRenderingMode == GridCellRenderingMode.Skia); return; }
            throw new InvalidOperationException("Invalid mode accepted.");
        });
        Test("native-mode.disposed-control-rejects-mode-change", () => {
            var grid = new GridControl(); grid.Dispose();
            try { grid.CellRenderingMode = GridCellRenderingMode.NativeControls; }
            catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("Disposed mode change accepted.");
        });
        Test("native-mode.formatted-text-is-native-and-does-not-write-data", () => {
            var table = new GridTable(new GridSchema([new("Value", typeof(decimal)) { Format = "N2" }], CultureInfo.GetCultureInfo("pl-PL")));
            table.Add(new(1, new object?[] { 123456.123456789m })); var history = new EditHistory(table);
            using var buffer = new GridCellEditBuffer(table, history, 1, "Value"); using var renderer = new GridCanvasRenderer();
            var template = NativeCellPresenters.Create(ColumnPresentation.Text, renderer); var element = template.Create();
            template.Bind!(element, buffer); Check(template.IsDisplayOnly && Text(element).Text.EndsWith(",12"));
            Check(!buffer.IsDirty && !history.CanUndo && Equals(buffer.Value, 123456.123456789m));
            template.Unbind!(element, buffer); Check(Text(element).Text == "");
        });
        Test("native-mode.presentations-create-fresh-native-trees", () => {
            using var renderer = new GridCanvasRenderer();
            foreach (var kind in new[] { ColumnPresentation.Text, ColumnPresentation.Badge, ColumnPresentation.Progress, ColumnPresentation.Sparkline })
            {
                var template = NativeCellPresenters.Create(kind, renderer); var first = template.Create(); var second = template.Create();
                Check(!ReferenceEquals(first, second) && first is Grid && VisualTreeHelper.GetChildrenCount(first) > 0);
                Check(template.IsDisplayOnly && !template.CommitOnLostFocus);
            }
        });
        Test("native-mode.style-and-theme-preserve-model-format", () => {
            var table = ValidationSuite.Fixture(); using var buffer = new GridCellEditBuffer(table, new(table), 1, "Amount");
            using var renderer = new GridCanvasRenderer(); var template = NativeCellPresenters.Create(ColumnPresentation.Text, renderer);
            var element = template.Create(); template.Bind!(element, buffer); var text = Text(element); string original = text.Text;
            renderer.Palette = GridPalette.Dark; template.UpdateAppearance!(element, new(false, false, false, false, true));
            Check(text.Text == original && ((SolidColorBrush)text.Foreground).Color.R == renderer.Palette.Foreground.Red);
            Check(!buffer.IsDirty);
        });
        Test("native-mode.text-flush-reads-value-before-delayed-change-notification", () => {
            var table = ValidationSuite.Fixture(); var history = new EditHistory(table);
            using var buffer = new GridCellEditBuffer(table, history, 1, "Name");
            var template = GridCellTemplates.TextBox(); var control = (TextBox)template.Create();
            buffer.SynchronizeBinding(() => { control.DataContext = buffer; template.Bind!(control, buffer); });
            buffer.SynchronizeBinding(() => control.Text = "Latest native text");
            Check(!buffer.IsDirty); template.Flush!(control, buffer);
            Check(buffer.IsDirty && buffer.TryCommit() && Equals(table.Schema["Name"].GetValue(table[1]), "Latest native text"));
            Check(history.CanUndo);
        });
        Test("native-mode.idle-pool-is-empty-after-switch-with-empty-data", () => {
            using var grid = new GridControl { Table = ValidationSuite.Fixture(), CellRenderingMode = GridCellRenderingMode.NativeControls };
            grid.Table.Clear(); grid.CellRenderingMode = GridCellRenderingMode.Skia;
            Check(grid.RealizedCellControlCount == 0 && grid.AttachedCellControlCount == 0 && grid.PooledCellControlCount == 0);
        });
        return new(results.AsReadOnly(), total.Elapsed.TotalMilliseconds);
    }
}

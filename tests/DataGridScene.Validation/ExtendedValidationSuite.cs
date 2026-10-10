using DataGridScene.Core;
using DataGridScene.Export;
using System.Diagnostics;
using System.Globalization;

namespace DataGridScene.Validation;

/// <summary>Additional parity contracts, executed unchanged on .NET and in the published browser application.</summary>
public static class ExtendedValidationSuite
{
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
    public static async Task<ValidationReport> RunAsync(bool stress = true)
    {
        var total = Stopwatch.StartNew();
        var baseline = await ValidationSuite.RunAsync(stress);
        var results = baseline.Results.ToList();
        void Test(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); results.Add(new(name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, watch.Elapsed.TotalMilliseconds, error.ToString())); }
        }
        Test("selection.single-row-modifiers", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection { Mode = GridSelectionMode.SingleRow };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(3, "Name"), extend: true, toggle: true);
            Check(selection.Count == 1 && selection.Contains(3)); selection.SelectAll(view); Check(selection.Count == 1);
        });
        Test("selection.cells-rectangle", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(3, "Amount"), extend: true);
            Check(selection.Count == 3 && selection.SelectedCellCount == 9);
            Check(selection.ContainsCell(2, "Region") && !selection.ContainsCell(2, "Code"));
        });
        Test("selection.cells-reordered-columns", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            string[] order = ["Amount", "Name", "Active"];
            selection.Select(view, new(1, "Amount"), columnOrder: order);
            selection.Select(view, new(2, "Active"), extend: true, columnOrder: order);
            Check(selection.SelectedCellCount == 6 && !selection.ContainsCell(1, "Region") && selection.ContainsCell(1, "Name"));
        });
        Test("selection.cells-case-insensitive-toggle", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "NAME")); selection.Select(view, new(1, "amount"), toggle: true);
            Check(selection.SelectedCellCount == 2); selection.Select(view, new(1, "name"), toggle: true);
            Check(selection.SelectedCellCount == 1 && selection.ContainsCell(1, "AMOUNT"));
        });
        Test("selection.cells-group-and-detail-boundaries", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(new() { Groups = [new("Region")] }); view.ToggleDetail(1);
            var selection = new GridSelection { Mode = GridSelectionMode.Cells }; string[] fields = ["Name", "Amount"];
            selection.Select(view, new(2, "Name"), columnOrder: fields);
            selection.Select(view, new(3, "Amount"), extend: true, columnOrder: fields);
            Check(selection.SelectedCellCount == 6 && selection.Count == 3);
        });
        Test("selection.hidden-anchor-falls-back-to-destination", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection();
            selection.Select(view, new(1, "Name")); view.SetQuery(new() { Filter = new FieldFilter("Code", FilterOperator.Greater, 1) });
            selection.Select(view, new(3, "Name"), extend: true);
            Check(selection.Count == 1 && selection.Contains(3));
        });
        Test("selection.hidden-anchor-column", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(2, "Amount"), extend: true, columnOrder: ["Amount", "Active"]);
            Check(selection.SelectedCellCount == 2 && selection.ContainsCell(1, "Amount") && !selection.ContainsCell(1, "Name"));
        });
        Test("selection.cells-retain-identity-after-sort", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); view.SetQuery(new() { Sorts = [new("Name", SortDirection.Descending)] });
            Check(selection.ContainsCell(1, "Name") && !selection.ContainsCell(2, "Name"));
        });
        Test("selection.cells-prune-deleted-records", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.SelectAll(view, ["Name", "Amount"]); view.Table.Remove([1, 2]); selection.Prune(view.Table);
            Check(selection.Count == 2 && selection.SelectedCellCount == 4);
        });
        Test("selection.batch-notification", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection(); int events = 0;
            selection.Changed += (_, _) => events++;
            using (selection.BeginUpdate()) { selection.Select(view, new(1, "Name")); using (selection.BeginUpdate()) selection.Select(view, new(2, "Name"), toggle: true); }
            Check(events == 1 && selection.Count == 2);
        });
        Test("selection.focus-independent-of-selected-cells", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Focus(view, new(3, "Amount"));
            Check(selection.SelectedCellCount == 1 && selection.ContainsCell(1, "Name") && selection.FocusedCell!.Value.RecordId == 3);
        });
        Test("selection.invalid-mode-and-column-order", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name"));
            Reject<ArgumentOutOfRangeException>(() => selection.Mode = (GridSelectionMode)999);
            Reject<ArgumentException>(() => selection.Select(view, new(1, "Name"), columnOrder: ["Name", "name"]));
            Reject<ArgumentException>(() => selection.Select(view, new(1, "Name"), columnOrder: ["Amount"]));
            Check(selection.SelectedCellCount == 1);
        });
        Test("clipboard.cells-sparse-holes-preserved", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Name")); selection.Select(view, new(3, "Amount"), toggle: true);
            var rows = DelimitedText.Parse(GridClipboard.Copy(view, selection, ["Name", "Region", "Amount"]), '\t');
            Check(rows.Count == 3 && rows.All(row => row.Count == 3));
            Check(rows[0][0] == "Alpha" && rows[0][2] == "" && rows[1].All(value => value == "") && rows[2][2] == "30");
        });
        Test("clipboard.respects-group-display-order", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(new() { Groups = [new("Region")] });
            var selection = new GridSelection(); selection.SelectAll(view);
            var rows = DelimitedText.Parse(GridClipboard.Copy(view, selection, ["Name"]), '\t');
            Check(string.Join(",", rows.Select(row => row[0])) == "Delta,Beta,Alpha,Alpine");
        });
        Test("clipboard.hidden-columns-not-copied", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection(); selection.Select(view, new(1, "Name"));
            var rows = DelimitedText.Parse(GridClipboard.Copy(view, selection, ["Amount", "Name"], includeHeaders: true), '\t');
            Check(rows.Count == 2 && rows[0][0] == "Amount" && rows[1][0] == "10" && rows[1][1] == "Alpha");
        });
        Test("clipboard.scalar-fills-only-selected-cells", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1, "Amount")); selection.Select(view, new(3, "Amount"), toggle: true);
            var history = new EditHistory(view.Table); history.Execute(GridClipboard.CreatePaste(view, selection, ["Name", "Amount"], "125.50"));
            Check(Equals(view.Table[1][3], 125.50m) && Equals(view.Table[2][3], 20m) && Equals(view.Table[3][3], 125.50m));
            Check(history.Undo() && Equals(view.Table[1][3], 10m) && Equals(view.Table[3][3], 30m));
        });
        Test("clipboard.paste-validation-is-atomic", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection(); selection.Select(view, new(1, "Name"));
            var edits = GridClipboard.CreatePaste(view, selection, ["Name", "Amount"], "Changed\t12\r\nChanged2\t-1");
            Reject<GridValidationException>(() => new EditHistory(view.Table).Execute(edits));
            Check(Equals(view.Table[1][1], "Alpha") && Equals(view.Table[2][3], 20m));
        });
        Test("clipboard.paste-rejects-readonly-and-overflow", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection(); selection.Select(view, new(1, "Code"));
            Reject<InvalidOperationException>(() => GridClipboard.CreatePaste(view, selection, ["Code", "Name"], "9\tChanged"));
            selection.Select(view, new(4, "Name"));
            Reject<InvalidOperationException>(() => GridClipboard.CreatePaste(view, selection, ["Name"], "a\r\nb"));
            Reject<InvalidOperationException>(() => GridClipboard.CreatePaste(view, selection, ["Name"], "a\tb"));
            Check(view.Table[1].Version == 0);
        });
        Test("clipboard.paste-hidden-focus-rejected", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection(); selection.Select(view, new(1, "Name"));
            view.SetQuery(new() { Filter = new FieldFilter("Code", FilterOperator.Greater, 1) });
            Reject<InvalidOperationException>(() => GridClipboard.CreatePaste(view, selection, ["Name"], "Changed"));
        });
        Test("clipboard.empty-selection-and-limits", () =>
        {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            Check(GridClipboard.Copy(view, selection, ["Name"]) == ""); selection.SelectAll(view);
            Reject<InvalidOperationException>(() => GridClipboard.Copy(view, selection, ["Name", "Amount"], maximumCells: 4));
        });
        foreach (var (culture, value, expected) in new[] { ("pl-PL", "12,50", 12.50m), ("de-DE", "1.234,50", 1234.50m), ("fr-FR", "1234,50", 1234.50m), ("en-US", "1,234.50", 1234.50m) })
        {
            Test("globalization.explicit-decimal-" + culture, () =>
            {
                var column = new GridColumn("Amount", typeof(decimal));
                Check(column.TryConvert(value, CultureInfo.GetCultureInfo(culture), out var actual, out var error), error ?? "Conversion failed");
                Check(Equals(actual, expected), $"{culture}: expected {expected}, received {actual}");
            });
        }
        if (stress) Test("selection.large-cell-block-shared-field-sets", () =>
        {
            var table = new GridTable(new GridSchema([new("A", typeof(int)), new("B", typeof(int))]));
            table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { i, i })));
            using var view = new GridView(table); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.SelectAll(view); Check(selection.Count == 100000 && selection.SelectedCellCount == 200000);
            Check(selection.ContainsCell(99999, "A") && selection.ContainsCell(0, "B"));
        });
        return new(results.AsReadOnly(), total.Elapsed.TotalMilliseconds);
    }
}

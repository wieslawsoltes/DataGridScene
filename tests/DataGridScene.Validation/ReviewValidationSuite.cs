using DataGridScene.Core;
using DataGridScene.Export;
using System.Diagnostics;

namespace DataGridScene.Validation;

/// <summary>Review regressions plus selected-range operations that must not scan unrelated records.</summary>
public static class ReviewValidationSuite
{
    private static void Check(bool ok, string message = "Assertion failed.")
    { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static ValidationReport Append(ValidationReport previous, bool stress = true)
    {
        var clock = Stopwatch.StartNew(); var results = previous.Results.ToList();
        void Test(string name, Action action)
        {
            var started = Stopwatch.GetTimestamp();
            try { action(); results.Add(new(name, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, error.ToString())); }
        }
        Test("review.selection-enumeration-does-not-expose-mutable-storage", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection();
            selection.SelectAll(view);
            Check(selection.EnumerateSelectedIds() is not ICollection<long>);
            Check(selection.EnumerateSelectedIds().Order().SequenceEqual(new long[] { 1, 2, 3, 4 }));
            selection.Mode = GridSelectionMode.Cells; selection.SelectAll(view, ["Name", "Amount"]);
            Check(selection.EnumerateSelectedIds() is not ICollection<long>);
            Check(selection.SelectedCellCount == 8);
        });
        Test("review.clipboard-sparse-rows-use-visible-order", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            view.SetQuery(new() { Sorts = [new("Amount", SortDirection.Descending)] });
            var selection = new GridSelection(); selection.Select(view, new(1,"Name")); selection.Select(view, new(3,"Name"), toggle:true);
            Check(GridClipboard.Copy(view, selection, ["Name"]) == "Alpine\r\nAlpha\r\n");
        });
        Test("review.clipboard-collapsed-selected-rows-excluded", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(new() { Groups = [new("Region")] });
            var selection = new GridSelection(); selection.SelectAll(view);
            view.ToggleGroup(view.Groups.Single(g => Equals(g.Value,"West")).Key);
            Check(GridClipboard.Copy(view, selection, ["Name"]) == "Delta\r\nBeta\r\n");
        });
        Test("review.clipboard-cell-holes-skip-detail-and-footer-rows", () => {
            using var view = new GridView(ValidationSuite.Fixture()); view.SetQuery(new() { Groups = [new("Region")], GroupFooterDisplay = GroupFooterDisplayMode.Always });
            view.ToggleDetail(2); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(2,"Name")); selection.Select(view, new(3,"Amount"), toggle:true);
            var rows = DelimitedText.Parse(GridClipboard.Copy(view, selection, ["Amount","Name"]), '\t');
            Check(rows.Count == 3 && rows[0][1] == "Beta" && rows[1].All(v=>v=="") && rows[2][0] == "30");
        });
        Test("review.clipboard-scalar-fill-visible-selected-cells-only", () => {
            using var view = new GridView(ValidationSuite.Fixture());
            var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view, new(1,"Name")); selection.Select(view, new(2,"Amount"), toggle:true);
            selection.Select(view, new(3,"Region"), toggle:true); selection.Focus(view, new(2,"Amount"));
            view.SetQuery(new() { Filter = new FieldFilter("Code", FilterOperator.Greater, 1) });
            var edits = GridClipboard.CreatePaste(view, selection, ["Amount","Name"], "100");
            Check(edits.Count == 1 && edits[0].RecordId == 2 && edits[0].Field == "Amount");
        });
        Test("review.clipboard-scalar-fill-canonical-order-and-readonly-rejection", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection { Mode = GridSelectionMode.Cells };
            selection.Select(view,new(3,"Name")); selection.Select(view,new(1,"Amount"),toggle:true); selection.Select(view,new(1,"Name"),toggle:true);
            var edits = GridClipboard.CreatePaste(view,selection,["Amount","Name"],"5");
            Check(edits.Count == 3 && edits[0].RecordId == 1 && edits[0].Field == "Amount" && edits[1].Field == "Name" && edits[2].RecordId == 3);
            selection.Select(view,new(2,"Code"),toggle:true);
            Reject<InvalidOperationException>(()=>GridClipboard.CreatePaste(view,selection,["Code","Amount","Name"],"5"));
        });
        Test("review.clipboard-limits-checked-before-getters", () => {
            int calls=0;
            var table = new GridTable(new GridSchema([new("Name",typeof(string)) { ValueGetter = _ => {calls++; return "X";} }]));
            table.AddRange(Enumerable.Range(1,20).Select(i=>new GridRecord(i,new object?[] {null})));
            using var view = new GridView(table); var selection = new GridSelection(); selection.SelectAll(view);
            Reject<InvalidOperationException>(()=>GridClipboard.Copy(view,selection,["Name"],maximumCells:5)); Check(calls==0);
            selection.Mode=GridSelectionMode.Cells; selection.Select(view,new(1,"Name")); selection.Select(view,new(20,"Name"),toggle:true);
            Reject<InvalidOperationException>(()=>GridClipboard.Copy(view,selection,["Name"],maximumCells:5)); Check(calls==0);
        });
        Test("review.clipboard-empty-and-header-only", () => {
            using var view = new GridView(ValidationSuite.Fixture()); var selection = new GridSelection();
            Check(GridClipboard.Copy(view,selection,["Name"],includeHeaders:true)=="Name\r\n");
            selection.Mode=GridSelectionMode.Cells;
            Check(GridClipboard.Copy(view,selection,["Name"],includeHeaders:true)=="");
        });
        if (stress) Test("review.clipboard-single-selection-in-100000-records", () => {
            var table = new GridTable(new GridSchema([new("Value",typeof(int)),new("Note",typeof(string))]));
            table.AddRange(Enumerable.Range(0,100000).Select(i=>new GridRecord(i,new object?[] {i,"Row "+i})));
            using var view = new GridView(table); var selection = new GridSelection(); selection.Select(view,new(99999,"Note"));
            Check(GridClipboard.Copy(view,selection,["Note","Value"])=="Row 99999\t99999\r\n");
            selection.Mode=GridSelectionMode.Cells; selection.Select(view,new(99999,"Note"));
            Check(GridClipboard.Copy(view,selection,["Note","Value"])=="Row 99999\r\n");
        });
        return RenderReviewValidation.Append(new(results.AsReadOnly(), previous.Milliseconds + clock.Elapsed.TotalMilliseconds));
    }
}

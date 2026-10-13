using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Validation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace DataGridScene.Demo;

public static class RowEditLayoutControlChecks
{
    public static ValidationReport Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Row layout control contract failed."); }
        void Test(string name, Action run)
        {
            var start = Stopwatch.GetTimestamp();
            try { run(); results.Add(new(name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new(name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table()
        {
            var table = new GridTable(new GridSchema([new("Name", typeof(string)) { AllowNull = false },
                new("Exact", typeof(decimal)), new("Hidden", typeof(string))]));
            table.Add(new(1, new object?[] { "Original", 1.2345678901234567890123456789m, "Unchanged" })); return table;
        }
        Test("row-layout-ui.default-constructor-keeps-all-fields", () => {
            var table = Table(); using var form = new RowEditForm(new(table, new(table), 1));
            Check(form.Editors.Count == 3 && form.LayoutPlan.ColumnCount == 1 && form.LayoutPlan.RowCount == 3);
        });
        Test("row-layout-ui.owned-order-caption-and-hidden-field", () => {
            var table = Table(); var fields = new List<RowEditFieldLayout> { new("Name") { Caption = "Customer", Order = -1, ColumnSpan = 2 }, new("Hidden") { IsVisible = false } };
            using var form = new RowEditForm(new(table, new(table), 1), new() { MaximumColumnCount = 3, MaximumWidth = 960, Fields = fields });
            fields.Clear(); Check(form.Editors.Count == 2 && !form.Editors.ContainsKey("Hidden"));
            Check(form.LayoutPlan.Fields[0].Field == "Name" && form.LayoutPlan.Fields[0].ColumnSpan == 2);
            Check(AutomationProperties.GetName(form.Editors["Name"]) == "Customer");
        });
        Test("row-layout-ui.hidden-value-survives-visible-update-and-undo", () => {
            var table = Table(); var history = new EditHistory(table);
            using var form = new RowEditForm(new(table, history, 1), new() { Fields = [new("Hidden") { IsVisible = false }] });
            ((TextBox)form.Editors["Name"]).Text = "Changed";
            Check(form.TryCommit() && Equals(table[1][2], "Unchanged") && Equals(table[1][1], 1.2345678901234567890123456789m));
            Check(history.Undo() && Equals(table[1][0], "Original") && !history.CanUndo);
        });
        Test("row-layout-ui.hidden-staged-values-still-validate", () => {
            var table = Table(); var history = new EditHistory(table); var buffer = new GridRowEditBuffer(table, history, 1);
            buffer.SetValue("Exact", "invalid");
            using var form = new RowEditForm(buffer, new() { Fields = [new("Exact") { IsVisible = false }] });
            Check(!form.TryCommit() && form.ValidationMessage is not null && !history.CanUndo && Equals(table[1][0], "Original"));
        });
        Test("row-layout-ui.bad-options-reject-before-buffer-mutation", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); int rejected = 0;
            foreach (var options in new RowEditFormOptions[] { new() { MaximumWidth = double.NaN }, new() { Fields = [new("Missing")] }, new() { CaptionPlacement = (RowEditCaptionPlacement)123 } })
                try { using var form = new RowEditForm(buffer, options); } catch (ArgumentException) { rejected++; }
            Check(rejected == 3 && buffer.IsActive && !buffer.IsDirty && table[1].Version == 0);
        });
        Test("row-layout-ui.caption-placement-does-not-change-control-types", () => {
            var table = Table(); using var form = new RowEditForm(new(table, new(table), 1), new() { CaptionPlacement = RowEditCaptionPlacement.Beside });
            Check(form.Editors.Values.All(editor => editor is TextBox) && form.FieldPanels.Count == 3);
            long reflows = form.LayoutReflowCount, writes = form.FieldPlacementWriteCount;
            for (int i = 0; i < 30; i++) form.RefreshReadOnly();
            Check(form.LayoutReflowCount == reflows && form.FieldPlacementWriteCount == writes && !form.Buffer.IsDirty);
        });
        return new(results.AsReadOnly(), results.Sum(r => r.Milliseconds));
    }
}

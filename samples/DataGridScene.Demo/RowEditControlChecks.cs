using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Validation;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace DataGridScene.Demo;

public static class RowEditControlChecks
{
    public static ValidationReport Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Row form control contract failed."); }
        void Test(string name, Action test)
        {
            var start = Stopwatch.GetTimestamp();
            try { test(); results.Add(new("row-form-api." + name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("row-form-api." + name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table()
        {
            var table = new GridTable(new GridSchema([new("Name", typeof(string)) { AllowNull = false },
                new("Optional", typeof(string)), new("Exact", typeof(decimal)), new("Locked", typeof(string)) { ReadOnly = true },
                new("Check", typeof(bool)), new("Choice", typeof(string)) { Choices = ["A", "B"] }]));
            table.Add(new(1, new object?[] { "Original", null, 1.2345678901234567890123456789m, "Locked", null, "A" })); return table;
        }
        Test("editors-only-stage-until-update", () => {
            var table = Table(); var history = new EditHistory(table); using var form = new RowEditForm(new(table, history, 1));
            ((TextBox)form.Editors["Name"]).Text = "Changed";
            Check(Equals(table[1][0], "Original") && !history.CanUndo && form.TryCommit());
            Check(Equals(table[1][0], "Changed") && history.Undo() && Equals(table[1][0], "Original"));
        });
        Test("untouched-values-stay-exact", () => {
            var table = Table(); var values = table[1].CopyValues(); using var form = new RowEditForm(new(table, new(table), 1));
            Check(form.TryCommit() && values.SequenceEqual(table[1].CopyValues()) && table[1].Version == 0);
        });
        Test("null-and-empty-are-explicitly-distinct", () => {
            var table = Table(); using var form = new RowEditForm(new(table, new(table), 1));
            form.NullToggles["Optional"].IsChecked = false; form.RefreshReadOnly();
            Check(form.TryCommit() && Equals(table[1][1], ""));
        });
        Test("readonly-fields-and-state-transitions", () => {
            var table = Table(); bool editable = true; using var form = new RowEditForm(new(table, new(table), 1, canEdit: () => editable));
            Check(((TextBox)form.Editors["Locked"]).IsReadOnly);
            ((TextBox)form.Editors["Name"]).Text = "Staged"; editable = false; form.RefreshReadOnly();
            Check(((TextBox)form.Editors["Name"]).IsReadOnly && !form.TryCommit() && Equals(table[1][0], "Original"));
            editable = true; form.RefreshReadOnly(); Check(form.TryCommit() && Equals(table[1][0], "Staged"));
        });
        Test("validation-retains-native-input", () => {
            var table = Table(); using var form = new RowEditForm(new(table, new(table), 1));
            ((TextBox)form.Editors["Name"]).Text = "";
            Check(!form.TryCommit() && form.ValidationMessage is not null && ((TextBox)form.Editors["Name"]).Text == "");
            ((TextBox)form.Editors["Name"]).Text = "Corrected"; Check(form.TryCommit());
        });
        Test("dispose-cancels-without-history", () => {
            var table = Table(); var history = new EditHistory(table); var form = new RowEditForm(new(table, history, 1));
            ((TextBox)form.Editors["Name"]).Text = "Cancelled"; form.Dispose(); form.Dispose();
            Check(!form.TryCommit() && !history.CanUndo && Equals(table[1][0], "Original"));
        });
        return new(results, results.Sum(result => result.Milliseconds));
    }
}

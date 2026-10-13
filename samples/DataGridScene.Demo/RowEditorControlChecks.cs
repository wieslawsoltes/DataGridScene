using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Validation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;

namespace DataGridScene.Demo;

public static class RowEditorControlChecks
{
    public static ValidationReport Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Custom row editor contract failed."); }
        static void Reject<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        void Test(string name, Action run)
        {
            var start = Stopwatch.GetTimestamp();
            try { run(); results.Add(new("row-editor-api." + name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("row-editor-api." + name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table()
        {
            var table = new GridTable(new GridSchema([new("Name", typeof(string)) { AllowNull = false },
                new("Count", typeof(int)) { AllowNull = false }, new("Notes", typeof(string)),
                new("Exact", typeof(decimal)), new("Locked", typeof(string)) { ReadOnly = true }]));
            table.Add(new(1, new object?[] { "Original", 2, null, 1.2345678901234567890123456789m, "Fixed" })); return table;
        }
        static RowEditFormOptions Options(string field, Func<RowEditEditorContext, RowEditEditor?> factory) => new() {
            EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> { [field] = factory }
        };
        Test("null-factory-result-keeps-default-exact-editors", () => {
            var table = Table(); var history = new EditHistory(table); var original = table[1].CopyValues();
            using var form = new RowEditForm(new(table, history, 1), Options("Exact", _ => null));
            Check(form.Editors["Exact"] is TextBox && form.TryCommit() && table[1].CopyValues().SequenceEqual(original) && !history.CanUndo);
        });
        Test("invalid-metadata-rejects-before-any-factory-runs", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); int calls = 0;
            Func<RowEditEditorContext, RowEditEditor?> factory = c => { calls++; return RowEditEditors.Text(c); };
            foreach (var map in new[] {
                new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> { ["Name"] = factory, ["Missing"] = factory },
                new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> { ["Name"] = factory, ["name"] = factory },
                new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> { ["Name"] = null! }
            }) Reject<ArgumentException>(() => { using var form = new RowEditForm(buffer, new() { EditorFactories = map }); });
            Check(calls == 0 && buffer.IsActive && !buffer.IsDirty);
        });
        Test("factory-map-is-owned-and-context-is-a-snapshot", () => {
            var table = Table(); RowEditEditorContext? captured = null;
            var factories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
                ["name"] = c => { captured = c; return RowEditEditors.Text(c); }
            };
            using var form = new RowEditForm(new(table, new(table), 1), new() { EditorFactories = factories }); factories.Clear();
            Check(captured?.Field == "Name" && Equals(captured.InitialValue, "Original"));
            ((TextBox)form.Editors["Name"]).Text = "Owned"; Check(form.TryCommit() && Equals(captured!.InitialValue, "Original") && Equals(table[1][0], "Owned"));
        });
        Test("hidden-field-never-runs-factory", () => {
            var table = Table(); int calls = 0;
            using var form = new RowEditForm(new(table, new(table), 1), Options("Name", c => { calls++; return RowEditEditors.Text(c); }) with {
                Fields = [new RowEditFieldLayout("Name") { IsVisible = false }]
            });
            Check(calls == 0 && !form.Editors.ContainsKey("Name") && form.TryCommit() && Equals(table[1][0], "Original"));
        });
        Test("capture-failure-does-not-partially-stage-earlier-editors", () => {
            var table = Table(); var history = new EditHistory(table); bool fail = true;
            using var form = new RowEditForm(new(table, history, 1), Options("Count", _ => new(new TextBox { Text = "3" },
                () => fail ? throw new FormatException("Reader unavailable") : 3, _ => { })));
            ((TextBox)form.Editors["Name"]).Text = "Pending";
            Check(!form.TryCommit() && Equals(form.Buffer.GetValue("Name"), "Original") && !history.CanUndo && table[1].Version == 0);
            Check(form.ValidationErrors.Single().Field == "Count" && ((TextBox)form.Editors["Name"]).Text == "Pending");
            fail = false; Check(form.TryCommit() && Equals(table[1][0], "Pending") && Equals(table[1][1], 3));
        });
        Test("unchanged-permissions-do-not-invoke-adapters-or-recreate", () => {
            var table = Table(); int calls = 0;
            using var form = new RowEditForm(new(table, new(table), 1), Options("Name", c => new(new TextBox(), () => c.InitialValue, _ => calls++)));
            long writes = form.ReadOnlyUpdateCount; var editor = form.Editors["Name"];
            for (int i = 0; i < 50; i++) form.RefreshReadOnly();
            Check(calls == 1 && form.ReadOnlyUpdateCount == writes && form.EditorCreationCount == 5 && ReferenceEquals(editor, form.Editors["Name"]));
        });
        Test("partially-failed-permission-update-is-retried-on-recovery", () => {
            var table = Table(); bool editable = true, fail = false; var text = new TextBox { Text = "Pending" };
            using var form = new RowEditForm(new(table, new(table), 1, canEdit: () => editable), Options("Name", _ => new(text, () => text.Text,
                readOnly => { text.IsReadOnly = readOnly; if (fail) throw new InvalidOperationException("Permission adapter failed"); })));
            editable = false; fail = true; form.RefreshReadOnly(); Check(!form.TryCommit() && form.ValidationMessage is not null && table[1].Version == 0);
            editable = true; fail = false; form.RefreshReadOnly(); Check(!text.IsReadOnly && form.TryCommit() && Equals(table[1][0], "Pending"));
        });
        Test("duplicate-elements-clean-only-acquired-editors", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); var shared = new TextBox(); int released = 0;
            var options = new RowEditFormOptions { EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
                ["Name"] = _ => new(shared, () => "Original", _ => { }, () => released++),
                ["Count"] = _ => new(shared, () => 2, _ => { }, () => released += 100)
            } };
            Reject<InvalidOperationException>(() => { using var form = new RowEditForm(buffer, options); });
            Check(released == 1 && buffer.IsActive && !buffer.IsDirty);
        });
        Test("editor-owned-by-another-form-is-not-disposed", () => {
            var table = Table(); int released = 0; var editor = new RowEditEditor(new TextBox(), () => "First owner", _ => { }, () => released++);
            using var first = new RowEditForm(new(table, new(table), 1), Options("Name", _ => editor));
            using var otherBuffer = new GridRowEditBuffer(table, new(table), 1);
            Reject<InvalidOperationException>(() => { using var second = new RowEditForm(otherBuffer, Options("Name", _ => editor)); });
            Check(released == 0 && first.TryCommit()); first.Dispose(); Check(released == 1 && Equals(table[1][0], "First owner"));
        });
        Test("late-factory-failure-releases-earlier-adapters", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); int released = 0;
            var options = new RowEditFormOptions { EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
                ["Name"] = c => new(new TextBox(), () => c.InitialValue, _ => { }, () => released++),
                ["Count"] = _ => throw new InvalidOperationException("Factory failed")
            } };
            Reject<InvalidOperationException>(() => { using var form = new RowEditForm(buffer, options); });
            Check(released == 1 && buffer.IsActive && !buffer.IsDirty);
        });
        Test("cleanup-errors-do-not-prevent-other-releases-or-cancel", () => {
            var table = Table(); var history = new EditHistory(table); int released = 0;
            var options = new RowEditFormOptions { EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
                ["Name"] = c => new(new TextBox(), () => c.InitialValue, _ => { }, () => released++),
                ["Count"] = c => new(new TextBox(), () => c.InitialValue, _ => { }, () => { released++; throw new InvalidOperationException("Cleanup failed"); })
            } };
            var form = new RowEditForm(new(table, history, 1), options); form.Dispose(); form.Dispose();
            Check(released == 2 && form.CleanupErrors.Count == 1 && !form.TryCommit() && !history.CanUndo && table[1].Version == 0);
        });
        Test("data-template-produces-fresh-roots-with-field-context", () => {
            var table = Table(); var template = new DataTemplate(() => new TextBox());
            var options = Options("Name", c => RowEditEditors.FromTemplate(template, c, root => ((TextBox)root).Text,
                (root, readOnly) => ((TextBox)root).IsReadOnly = readOnly));
            using var a = new RowEditForm(new(table, new(table), 1), options);
            using var b = new RowEditForm(new(table, new(table), 1), options);
            Check(!ReferenceEquals(a.Editors["Name"], b.Editors["Name"]) && a.Editors["Name"].DataContext is RowEditEditorContext { Field: "Name", InitialValue: "Original" });
            var firstRoot = a.Editors["Name"]; var firstContext = firstRoot.DataContext;
            var brokenTemplate = new DataTemplate(() => firstRoot); int invalidReleases = 0;
            using var secondBuffer = new GridRowEditBuffer(table, new(table), 1);
            Reject<InvalidOperationException>(() => {
                using var rejected = new RowEditForm(secondBuffer, Options("Name", c =>
                    RowEditEditors.FromTemplate(brokenTemplate, c, _ => "Wrong owner", (_, _) => { }, _ => invalidReleases++)));
            });
            Check(ReferenceEquals(firstRoot.DataContext, firstContext) && secondBuffer.IsActive && invalidReleases == 0);
            ((TextBox)a.Editors["Name"]).Text = "Template saved"; Check(a.TryCommit() && Equals(table[1][0], "Template saved"));
        });
        Test("multiline-null-empty-and-pending-value-remain-distinct", () => {
            RowEditorTextChecks.VerifyUntouchedLineEndings();
            var table = Table(); var options = Options("Notes", c => RowEditEditors.Text(c, true, 88));
            using (var form = new RowEditForm(new(table, new(table), 1), options)) {
                Check(((TextBox)form.Editors["Notes"]).AcceptsReturn); Check(form.TryCommit() && table[1][2] is null);
            }
            using (var form = new RowEditForm(new(table, new(table), 1), options)) {
                form.NullToggles["Notes"].IsChecked = false; form.RefreshReadOnly();
                ((TextBox)form.Editors["Notes"]).Text = "One\nTwo"; Check(form.TryCommit() && Equals(table[1][2], "One\nTwo"));
            }
            using (var form = new RowEditForm(new(table, new(table), 1), options)) {
                ((TextBox)form.Editors["Notes"]).Text = ""; Check(form.TryCommit() && Equals(table[1][2], ""));
            }
        });
        Test("reader-cannot-reenter-commit-or-dispose", () => {
            var table = Table(); RowEditForm? form = null;
            form = new RowEditForm(new(table, new(table), 1), Options("Name", _ => new(new TextBox(), () => {
                Check(!form!.TryCommit()); Reject<InvalidOperationException>(() => form.Dispose()); return "Outer update";
            }, _ => { })));
            using (form) Check(form.TryCommit() && table[1].Version == 1 && Equals(table[1][0], "Outer update"));
        });
        Test("readonly-fields-never-call-custom-readers", () => {
            var table = Table(); bool locked = false;
            using var form = new RowEditForm(new(table, new(table), 1), Options("Locked", _ => new(new TextBox(),
                () => throw new InvalidOperationException("Must not read"), readOnly => locked = readOnly)));
            Check(locked && form.TryCommit() && Equals(table[1][4], "Fixed"));
        });
        Test("field-error-border-and-message-clear-after-valid-update", () => {
            var table = Table(); using var form = new RowEditForm(new(table, new(table), 1));
            ((TextBox)form.Editors["Count"]).Text = "bad"; Check(!form.TryCommit());
            var border = ((Grid)form.FieldPanels["Count"]).Children.OfType<Border>().Single();
            var color = ((SolidColorBrush)border.BorderBrush).Color;
            Check(form.ValidationErrors.Count == 1 && form.ValidationMessage is not null && color.A == 255 && color.R > color.G);
            ((TextBox)form.Editors["Count"]).Text = "3";
            Check(form.TryCommit() && form.ValidationMessage is null && ((SolidColorBrush)border.BorderBrush).Color.A == 0);
        });
        return new(results.AsReadOnly(), results.Sum(r => r.Milliseconds));
    }
}

using DataGridScene.Core;
using System.Diagnostics;
using System.Globalization;

namespace DataGridScene.Validation;

public static class RowEditValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Row edit contract failed."); }
        static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
        void Test(string name, Action action)
        {
            var start = Stopwatch.GetTimestamp();
            try { action(); results.Add(new("row-edit." + name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("row-edit." + name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table(CultureInfo? culture = null)
        {
            var schema = new GridSchema([
                new("Name", typeof(string)) { AllowNull = false },
                new("Low", typeof(int)) { AllowNull = false }, new("High", typeof(int)) { AllowNull = false },
                new("Amount", typeof(decimal)), new("Key", typeof(long)), new("When", typeof(DateTimeOffset)),
                new("Optional", typeof(string)), new("Locked", typeof(string)) { ReadOnly = true }
            ], culture) { RowValidator = values => (int)values[1]! > (int)values[2]! ? "Low must not exceed High." : null };
            var table = new GridTable(schema);
            table.Add(new(1, new object?[] { "One", 2, 5, 1.2345678901234567890123456789m, 9007199254740993L,
                DateTimeOffset.Parse("2026-10-12T10:20:30.1234567+02:00", CultureInfo.InvariantCulture), null, "Fixed" }));
            return table;
        }
        Test("staging-is-silent-and-atomic-with-one-undo", () => {
            var table = Table(); var history = new EditHistory(table); int notifications = 0;
            table.Changed += (_, _) => notifications++;
            using var buffer = new GridRowEditBuffer(table, history, 1);
            buffer.SetValue("Low", "8"); buffer.SetValue("High", "9");
            Check(notifications == 0 && (int)table[1][1]! == 2 && !history.CanUndo);
            Check(buffer.TryCommit() && notifications == 1 && table[1].Version == 1 && buffer.CommittedChange!.Changes.Count == 2);
            Check(history.Undo() && (int)table[1][1]! == 2 && (int)table[1][2]! == 5 && !history.CanUndo);
            Check(history.Redo() && (int)table[1][1]! == 8 && (int)table[1][2]! == 9);
        });
        Test("row-validation-retains-all-staged-values", () => {
            var table = Table(); var history = new EditHistory(table); using var buffer = new GridRowEditBuffer(table, history, 1);
            buffer.SetValue("Low", "8"); Check(!buffer.TryCommit());
            Check(buffer.IsActive && buffer.IsDirty && buffer.Errors.Any(e => e.Field is null) && Equals(buffer.GetValue("Low"), "8") && !history.CanUndo);
            buffer.SetValue("High", "9"); Check(buffer.TryCommit());
        });
        Test("multiple-field-errors-do-not-partially-apply", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); long revision = table.Revision;
            buffer.SetValue("Name", ""); buffer.SetValue("Low", "not a number"); buffer.SetValue("High", "10");
            Check(!buffer.TryCommit() && table.Revision == revision && buffer.Errors.Count == 2);
            Check(buffer.Errors.Any(e => e.Field == "Name") && buffer.Errors.Any(e => e.Field == "Low") && (int)table[1][2]! == 5);
        });
        Test("untouched-null-decimal-long-and-offset-stay-exact", () => {
            var table = Table(); var before = table[1].CopyValues(); var history = new EditHistory(table);
            using var buffer = new GridRowEditBuffer(table, history, 1); buffer.SetValue("Name", "Changed"); Check(buffer.TryCommit());
            Check(before.Skip(1).SequenceEqual(table[1].CopyValues().Skip(1)) && buffer.CommittedChange!.Changes.Count == 1);
        });
        Test("exact-culture-conversion-is-not-double-based", () => {
            var table = Table(CultureInfo.GetCultureInfo("pl-PL")); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            buffer.SetValue("Amount", "7,2345678901234567890123456789"); buffer.SetValue("Key", "9223372036854775807");
            Check(buffer.TryCommit() && Equals(table[1][3], 7.2345678901234567890123456789m) && Equals(table[1][4], long.MaxValue));
        });
        Test("cancel-never-publishes-or-invalidates-history", () => {
            var table = Table(); var history = new EditHistory(table); history.Execute([new(1, "Name", "Previous")]);
            var buffer = new GridRowEditBuffer(table, history, 1); buffer.SetValue("Name", "Cancelled"); buffer.Dispose(); buffer.Dispose();
            Check(Equals(table[1][0], "Previous") && history.CanUndo && !buffer.IsActive && !buffer.TryCommit());
            Throws<ObjectDisposedException>(() => buffer.SetValue("Name", "Late"));
        });
        Test("noop-does-not-increment-revision-or-history", () => {
            var table = Table(); var history = new EditHistory(table); long revision = table.Revision;
            using var buffer = new GridRowEditBuffer(table, history, 1); buffer.SetValue("Name", "Temporary"); buffer.SetValue("name", "One");
            Check(!buffer.IsDirty && buffer.TryCommit() && table.Revision == revision && !history.CanUndo && buffer.CommittedChange!.Changes.Count == 0);
        });
        Test("changed-other-field-conflicts-and-retains-input", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1, ["Name"]);
            buffer.SetValue("Name", "Staged"); table.Apply([new(1, "High", 10)]);
            Check(!buffer.TryCommit() && buffer.Error!.Contains("changed") && Equals(buffer.GetValue("Name"), "Staged") && Equals(table[1][0], "One"));
        });
        Test("noop-still-checks-record-version", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); table.Apply([new(1, "High", 10)]);
            Check(!buffer.TryCommit() && buffer.IsActive);
        });
        Test("replacement-identity-cannot-receive-staged-edits", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); buffer.SetValue("Name", "Staged");
            var values = table[1].CopyValues(); table.Remove([1]); table.Add(new(1, values));
            Check(!buffer.TryCommit() && buffer.Error!.Contains("replaced") && Equals(table[1][0], "One"));
        });
        Test("unrelated-row-write-does-not-conflict", () => {
            var table = Table(); table.Add(new(2, table[1].CopyValues())); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            buffer.SetValue("Name", "Staged"); table.Apply([new(2, "Name", "Other")]); Check(buffer.TryCommit());
        });
        Test("read-only-transition-retains-staged-values", () => {
            var table = Table(); bool editable = true; using var buffer = new GridRowEditBuffer(table, new(table), 1, canEdit: () => editable);
            buffer.SetValue("Name", "Staged"); editable = false; Check(!buffer.TryCommit() && Equals(buffer.GetValue("Name"), "Staged"));
            editable = true; Check(buffer.TryCommit());
        });
        Test("field-subset-is-owned-and-readonly-enforced", () => {
            var table = Table(); var fields = new[] { "name", "Locked" }; using var buffer = new GridRowEditBuffer(table, new(table), 1, fields);
            fields[0] = "High"; Check(buffer.Columns[0].Field == "Name");
            Throws<InvalidOperationException>(() => buffer.SetValue("Locked", "Bad"));
            Throws<ArgumentException>(() => buffer.SetValue("High", 9));
            buffer.SetValue("NAME", "Allowed"); Check(buffer.TryCommit());
        });
        Test("constructor-rejects-invalid-ownership-and-fields", () => {
            var table = Table(); Throws<ArgumentException>(() => new GridRowEditBuffer(table, new(Table()), 1));
            Throws<ArgumentException>(() => new GridRowEditBuffer(table, new(table), 1, ["Name", "name"]));
            Throws<ArgumentException>(() => new GridRowEditBuffer(table, new(table), 1, Array.Empty<string>()));
        });
        Test("reentrant-commit-is-rejected-with-one-publication", () => {
            var table = Table(); var history = new EditHistory(table); using var buffer = new GridRowEditBuffer(table, history, 1); int notifications = 0;
            table.Changed += (_, _) => { notifications++; Check(!buffer.TryCommit()); };
            buffer.SetValue("Name", "Staged"); Check(buffer.TryCommit() && notifications == 1 && history.CanUndo);
        });
        Test("100000-records-only-snapshot-the-chosen-row", () => {
            int reads = 0; var table = new GridTable(new GridSchema([new("Name", typeof(string)),
                new("Computed", typeof(string)) { ValueGetter = r => { reads++; return r[0]; } }]));
            table.AddRange(Enumerable.Range(0, 100000).Select(i => new GridRecord(i, new object?[] { "R" + i, null })));
            using var buffer = new GridRowEditBuffer(table, new(table), 90000); Check(reads == 1 && buffer.Columns.Count == 2);
            buffer.SetValue("Name", "Only this row"); Check(buffer.TryCommit() && reads == 1 && Equals(table[90000][0], "Only this row"));
        });
        return results;
    }
}

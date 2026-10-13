using DataGridScene.Core;
using System.Diagnostics;

namespace DataGridScene.Validation;

public static class RowBatchStagingValidation
{
    public static IReadOnlyList<ValidationResult> Run()
    {
        var results = new List<ValidationResult>();
        static void Check(bool value) { if (!value) throw new InvalidOperationException("Row batch staging contract failed."); }
        static void Reject<T>(Action run) where T : Exception
        { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        void Test(string name, Action run)
        {
            var start = Stopwatch.GetTimestamp();
            try { run(); results.Add(new("row-batch." + name, true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null)); }
            catch (Exception error) { results.Add(new("row-batch." + name, false, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString())); }
        }
        static GridTable Table()
        {
            var table = new GridTable(new GridSchema([new("Name", typeof(string)) { AllowNull = false },
                new("Count", typeof(int)) { AllowNull = false }, new("Locked", typeof(string)) { ReadOnly = true }]));
            table.Add(new(1, new object?[] { "Original", 2, "Fixed" })); return table;
        }
        Test("stages-without-model-writes-and-commits-one-undo", () => {
            var table = Table(); var history = new EditHistory(table); using var buffer = new GridRowEditBuffer(table, history, 1);
            long revision = table.Revision;
            buffer.SetValues([new("Name", "Changed"), new("Count", "3")]);
            Check(Equals(buffer.GetValue("Count"), "3") && Equals(table[1][0], "Original") && !history.CanUndo && table.Revision == revision);
            Check(buffer.TryCommit() && table[1].Version == 1 && history.Undo() && !history.CanUndo && Equals(table[1][0], "Original"));
        });
        Test("unknown-field-rejects-the-whole-batch", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            Reject<ArgumentException>(() => buffer.SetValues([new("Name", "Changed"), new("Missing", 3)]));
            Check(!buffer.IsDirty && Equals(buffer.GetValue("Name"), "Original"));
        });
        Test("case-insensitive-duplicates-are-atomic", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            Reject<ArgumentException>(() => buffer.SetValues([new("Name", "One"), new("name", "Two")]));
            Check(!buffer.IsDirty);
        });
        Test("readonly-field-rejects-earlier-writable-field", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            Reject<InvalidOperationException>(() => buffer.SetValues([new("Name", "Changed"), new("Locked", "No")]));
            Check(!buffer.IsDirty);
        });
        Test("enumeration-failure-keeps-existing-pending-input-and-errors", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            buffer.SetValue("Count", "invalid"); Check(!buffer.TryCommit()); string? error = buffer.Error;
            IEnumerable<KeyValuePair<string, object?>> Input() { yield return new("Name", "Not staged"); throw new FormatException("Reader failed"); }
            Reject<FormatException>(() => buffer.SetValues(Input()));
            Check(Equals(buffer.GetValue("Name"), "Original") && Equals(buffer.GetValue("Count"), "invalid") && buffer.Error == error);
        });
        Test("permission-change-during-enumeration-is-atomic", () => {
            var table = Table(); bool editable = true; using var buffer = new GridRowEditBuffer(table, new(table), 1, canEdit: () => editable);
            IEnumerable<KeyValuePair<string, object?>> Input() { yield return new("Name", "Not staged"); editable = false; yield return new("Count", 3); }
            Reject<InvalidOperationException>(() => buffer.SetValues(Input())); Check(!buffer.IsDirty);
            editable = true; buffer.SetValues([new("Name", "Recovered")]); Check(buffer.TryCommit());
        });
        Test("nested-stage-commit-and-dispose-cannot-publish", () => {
            var table = Table(); var history = new EditHistory(table); using var buffer = new GridRowEditBuffer(table, history, 1);
            IEnumerable<KeyValuePair<string, object?>> Input() {
                Reject<InvalidOperationException>(() => buffer.SetValue("Name", "Nested"));
                Reject<InvalidOperationException>(() => buffer.SetValues([]));
                Reject<InvalidOperationException>(() => buffer.Dispose());
                Check(!buffer.TryCommit()); yield return new("Name", "Outer");
            }
            buffer.SetValues(Input()); Check(!history.CanUndo && Equals(buffer.GetValue("Name"), "Outer") && buffer.TryCommit());
        });
        Test("raw-invalid-values-validate-only-at-commit", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1);
            buffer.SetValues([new("Name", ""), new("Count", "bad")]);
            Check(buffer.Error is null && !buffer.TryCommit() && buffer.Errors.Count == 2 && table[1].Version == 0);
        });
        Test("subset-and-empty-batches-preserve-untouched-values", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1, ["Name"]);
            buffer.SetValues([]); Check(!buffer.IsDirty);
            Reject<ArgumentException>(() => buffer.SetValues([new("Count", 3)]));
            buffer.SetValues([new("name", "Subset")]); Check(buffer.TryCommit() && Equals(table[1][1], 2) && Equals(table[1][2], "Fixed"));
        });
        Test("consumes-input-once-and-rejects-after-dispose", () => {
            var table = Table(); using var buffer = new GridRowEditBuffer(table, new(table), 1); int enumerations = 0;
            IEnumerable<KeyValuePair<string, object?>> Input() { enumerations++; yield return new("Name", "Once"); }
            buffer.SetValues(Input()); Check(enumerations == 1); buffer.Dispose();
            Reject<ObjectDisposedException>(() => buffer.SetValues(Input())); Check(enumerations == 1);
        });
        return results.AsReadOnly();
    }
}

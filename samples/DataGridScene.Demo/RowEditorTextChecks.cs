using DataGridScene.Controls;
using DataGridScene.Core;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Demo;

/// <summary>Additional assertions within the existing multiline API contract.</summary>
internal static class RowEditorTextChecks
{
    internal static void VerifyUntouchedLineEndings()
    {
        static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string original in new[] { "First\nSecond", "First\r\nSecond", "First\rSecond", "\r\nFirst\n\rSecond\r\n" })
        {
            var table = new GridTable(new GridSchema([new("Notes", typeof(string)) { AllowNull = false }]));
            table.Add(new(1, new object?[] { original }));
            var history = new EditHistory(table);
            var options = new RowEditFormOptions { EditorFactories = new Dictionary<string, Func<RowEditEditorContext, RowEditEditor?>> {
                ["Notes"] = c => RowEditEditors.Text(c, multiline: true)
            } };
            using (var form = new RowEditForm(new(table, history, 1), options))
            {
                string displayed = ((TextBox)form.Editors["Notes"]).Text;
                if (Normalize(displayed) != Normalize(original)) throw new InvalidOperationException("Initial multiline text was truncated by native initialization.");
                if (!form.TryCommit() || !Equals(table[1][0], original) || history.CanUndo || table[1].Version != 0)
                    throw new InvalidOperationException("An untouched multiline form changed original line-ending bytes or created undo.");
            }
            // The default single-line representation must not silently save its
            // truncated/display-normalized text when another field is untouched.
            using (var form = new RowEditForm(new(table, history, 1)))
                if (!form.TryCommit() || !Equals(table[1][0], original) || history.CanUndo || table[1].Version != 0)
                    throw new InvalidOperationException("An untouched default editor changed the original multiline model value.");
            using (var form = new RowEditForm(new(table, history, 1), options))
            {
                ((TextBox)form.Editors["Notes"]).Text = "Changed\r\nwith\rtrailing\n";
                if (!form.TryCommit() || !Equals(table[1][0], "Changed\nwith\ntrailing\n"))
                    throw new InvalidOperationException("Changed multiline text did not use the documented LF representation.");
                if (!history.Undo() || history.CanUndo || !Equals(table[1][0], original))
                    throw new InvalidOperationException("Undo did not restore exact original line-ending bytes.");
            }
        }
    }
}

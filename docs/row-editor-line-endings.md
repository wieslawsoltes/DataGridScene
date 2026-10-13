# Native text normalization and exact row values

The row editor adapter distinguishes an editor's native display representation from the original model value. A native multiline TextBox may normalize LF and CRLF into CR. A default single-line TextBox may truncate the displayed text at a line break. Neither operation is an intentional model edit.

`RowEditEditors.Text` configures `AcceptsReturn` before assigning the initial text, so the multiline editor displays every line. It records the native display representation once. On Update, an unchanged representation returns the exact original typed model value rather than serializing the control's normalization back to the table. This applies to the single-line default helper as well as multiline fields.

For multiline string fields, comparison maps CRLF and CR to LF. If there is a real content change, the helper returns LF-delimited text. Untouched input is **not** rewritten to LF: its exact original LF, CRLF, CR or mixed sequence is retained. Undo restores that exact original value. Trailing line breaks and empty lines are preserved. Null and empty string remain distinct through the explicit Null toggle.

Normalization runs only once during initialization and on explicit capture, not during keys, paint, scrolling or resize. Strings without CR reuse the existing string rather than allocating a normalized copy. Application-supplied adapters can implement another representation policy; this contract belongs to the built-in text helper.

## Regression evidence and tests

The first full custom-editor run passed all real input scenarios but exposed a failing API contract requiring the exact value `One\nTwo`: the native editor supplied CR instead. The fix changes the adapter, not the expected model value.

The existing multiline API contract now additionally checks unedited LF, CRLF, CR and mixed input, initial full-line display, no version/undo changes after an untouched Update, LF output after actual edits, and exact undo. The browser suite verifies both initial lines are visible through the real native text value, no-op Update retains model values/versions/history, and Ctrl+Enter commits exactly two LF-separated lines without an extra newline. It does not replace model assertions with normalized comparisons; normalization is used only when reading the framework's native display text.

The upstream [Uno TextBox tests](https://github.com/unoplatform/uno/blob/7d7d769916028bd6057edfb9f55d4666b66e2ce7/src/Uno.UI.UnitTests/Windows_UI_XAML_Controls/TextBoxTests/Given_TextBox.cs) document native CR normalization. DataGridScene's adapter has its own portable model policy and is tested against the repository's pinned Uno dependencies. Consult the exact Actions run for pass/fail status.

See [custom row editors](row-editors.md) for factory/capture/lifetime APIs and [validation](validation.md) for the full test inventory.

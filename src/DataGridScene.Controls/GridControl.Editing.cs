using DataGridScene.Core;
using DataGridScene.Export;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private Control? _editor;
    private Border? _editorBorder;
    private EditTransaction? _transaction;
    private string? _editField;
    private long _editRecordId;
    private bool _filterEditor;
    private bool _committing;
    public bool IsEditing => _editor is not null;
    public bool IsEditorReady => _editor is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } && _editor.FocusState != FocusState.Unfocused;
    public string? EditorText => _editor is TextBox text ? text.Text : _editor is ComboBox combo ? Convert.ToString(combo.SelectedItem) : null;
    public string? EditingField => _editField;
    public long? EditingRecordId => _editor is not null && !_filterEditor ? _editRecordId : null;

    public void BeginEdit(long recordId, string field)
    {
        RequireView(); if (IsReadOnly || !CommitEdit()) return;
        var column = Table!.Schema[field]; if (column.IsReadOnly) return;
        var record = Table[recordId];
        Selection.Select(View!, new(recordId, field)); EnsureVisible(new(recordId, field));
        _editRecordId = recordId; _editField = field; _filterEditor = false; _transaction = History!.Begin(recordId);
        if (column.Choices.Count > 0)
        {
            _editor = new ComboBox { ItemsSource = column.Choices, SelectedItem = Convert.ToString(column.GetValue(record), Table.Schema.Culture), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        }
        else
        {
            string value = column.GetValue(record) switch
            {
                DateTime date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset date => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                var data => Convert.ToString(data, Table.Schema.Culture) ?? ""
            };
            _editor = new TextBox { Text = value, Padding = new Thickness(6, 2, 6, 2), MinHeight = 0, MinWidth = 0, AcceptsReturn = false, FontSize = 13 };
        }
        ShowEditor(column.Caption);
    }
    private void BeginFilterEdit(string field)
    {
        if (!CommitEdit()) return;
        _filterEditor = true; _editField = field; _transaction = null;
        _editor = new TextBox { Text = _filterTexts.GetValueOrDefault(field) ?? "", PlaceholderText = "Filter…", Padding = new Thickness(6, 2, 6, 2), MinHeight = 0, MinWidth = 0, FontSize = 13 };
        ShowEditor("Filter " + Table!.Schema[field].Caption);
    }
    private void ShowEditor(string label)
    {
        _editorBorder = new Border { Child = _editor, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue) };
        // Focus before a TextBox is loaded does not create Uno's browser input host.
        // Reapply it on Loaded so IME, insertText and paste reach the real editor.
        _editor!.Loaded += EditorLoaded;
        _overlay.Children.Add(_editorBorder);
        AutomationProperties.SetName(_editor!, label);
        AutomationProperties.SetAutomationId(_editor!, "GridCellEditor");
        _editor!.KeyDown += EditorKeyDown;
        PositionEditor();
        _editor.Focus(FocusState.Programmatic);
        if (_editor is TextBox text) text.SelectAll();
        ClearError(); Changed();
    }
    private void EditorLoaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, _editor))
        {
            _editor!.Focus(FocusState.Programmatic);
            if (_editor is TextBox text) text.SelectAll();
            Changed();
        }
    }
    private void PositionEditor()
    {
        if (_editorBorder is null || Frame is null || _editField is null || View is null) return;
        var bounds = _filterEditor ? Frame.HitTest(Frame.Columns.Slots.FirstOrDefault(slot => slot.Field == _editField)?.ClipLeft + 2 ?? -1, Frame.FilterTop + 2).Bounds : Frame.CellBounds(View.IndexOfRecord(_editRecordId), _editField);
        _editorBorder.Visibility = bounds.Width > 0 && bounds.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_editorBorder, bounds.X); Canvas.SetTop(_editorBorder, bounds.Y);
        _editorBorder.Width = Math.Max(1, bounds.Width); _editorBorder.Height = Math.Max(1, bounds.Height);
    }
    public bool CommitEdit()
    {
        if (_committing || _editor is null) return true;
        _committing = true;
        try
        {
            object? value = _editor switch { TextBox text => text.Text, ComboBox combo => combo.SelectedItem, _ => null };
            if (_filterEditor) SetColumnFilter(_editField!, Convert.ToString(value) ?? "");
            else { _transaction!.Set(_editField!, value); _transaction.Commit(); }
            CloseEditor(); ClearError(); return true;
        }
        catch (Exception exception)
        {
            if (_editorBorder is not null) _editorBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
            if (_editor is not null) { ToolTipService.SetToolTip(_editor, exception.Message); _editor.Focus(FocusState.Programmatic); }
            Report(exception); return false;
        }
        finally { _committing = false; }
    }
    public void CancelEdit() { _transaction?.Cancel(); CloseEditor(); ClearError(); }
    private void CloseEditor()
    {
        if (_editor is not null) { _editor.KeyDown -= EditorKeyDown; _editor.Loaded -= EditorLoaded; }
        if (_editorBorder is not null) { _overlay.Children.Remove(_editorBorder); _editorBorder.Child = null; }
        _editor = null; _editorBorder = null; _transaction = null; _editField = null; _filterEditor = false;
        Redraw(); Changed();
    }
    private void EditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { CancelEdit(); Focus(FocusState.Keyboard); args.Handled = true; }
        else if (args.Key == VirtualKey.Enter) { if (CommitEdit()) Focus(FocusState.Keyboard); args.Handled = true; }
        else if (args.Key == VirtualKey.Tab)
        {
            bool shift = Down(VirtualKey.Shift);
            if (CommitEdit()) Navigate(VirtualKey.Tab, shift);
            args.Handled = true;
        }
    }
    public string CopySelectionText(bool includeHeaders = false)
    {
        RequireView();
        return GridClipboard.Copy(View!, Selection, SelectionFields, includeHeaders);
    }
    public void CopySelection()
    {
        if (!CommitEdit()) return;
        var content = new DataPackage(); content.SetText(CopySelectionText()); Clipboard.SetContent(content);
    }
    public async void PasteClipboard()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) PasteText(await content.GetTextAsync());
        }
        catch (Exception exception) { Report(exception); }
    }
    public void PasteText(string text)
    {
        RequireView(); if (IsReadOnly) throw new InvalidOperationException("This grid is read-only.");
        if (!CommitEdit()) return;
        var edits = GridClipboard.CreatePaste(View!, Selection, SelectionFields, text);
        History!.Execute(edits); ClearError();
    }
}

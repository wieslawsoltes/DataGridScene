using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private ContentDialog? _rowEditDialog;
    public RowEditForm? ActiveRowEditForm { get; private set; }
    /// <summary>Show a cached popup form for an existing record. One Update creates at
    /// most one undo item. False means cancelled, already open, or blocked by editing.</summary>
    public Task<bool> ShowRowEditAsync(long recordId, IEnumerable<string>? fields = null) => ShowRowEditAsync(recordId, fields, null);

    /// <summary>Show an optionally customized, responsive form. Options and metadata are
    /// validated before opening; resizing never reconstructs the native field editors.</summary>
    public async Task<bool> ShowRowEditAsync(long recordId, IEnumerable<string>? fields, RowEditFormOptions? options)
    {
        RequireView();
        if (ActiveRowEditForm is not null || !CommitEdit()) return false;
        if (IsReadOnly) throw new InvalidOperationException("This grid is read-only.");
        if (XamlRoot is null || !IsLoaded) throw new InvalidOperationException("Load the grid before opening a row form.");
        var table = Table!; var history = History!; var root = XamlRoot;
        using var buffer = new GridRowEditBuffer(table, history, recordId, fields,
            () => !_disposed && ReferenceEquals(Table, table) && ReferenceEquals(History, history) && !IsReadOnly);
        using var form = new RowEditForm(buffer, options);
        var dialog = new ContentDialog { XamlRoot = root, Title = options?.Title ?? "Edit row", Content = form,
            PrimaryButtonText = "Update", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.None,
            RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light };
        bool saved = false;
        if (options is not null) dialog.Resources["ContentDialogMaxWidth"] = options.MaximumWidth + 48;
        void UpdateFormSize()
        {
            if (options is null) return;
            double width = Math.Max(0, Math.Min(options.MaximumWidth, root.Size.Width - 96));
            form.MinWidth = Math.Min(240, width);
            if (form.Width != width) form.Width = width;
            double height = Math.Min(options.MaximumFieldHeight, Math.Max(80, root.Size.Height - 240));
            if (form.FieldsViewport.MaxHeight != height) form.FieldsViewport.MaxHeight = height;
        }
        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateFormSize();
        dialog.GettingFocus += (_, args) => PrepareNativeKeyboardHost(dialog, args);
        dialog.PrimaryButtonClick += (_, args) => { saved = form.TryCommit(); args.Cancel = !saved; };
        form.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !Down(VirtualKey.Control)) return;
            args.Handled = true;
            if (form.TryCommit()) { saved = true; dialog.Hide(); }
        };
        void OwnerChanged(object? sender, EventArgs args)
        {
            if (_disposed || !ReferenceEquals(Table, table) || !ReferenceEquals(History, history)) { dialog.Hide(); return; }
            dialog.IsPrimaryButtonEnabled = !IsReadOnly;
            form.RefreshReadOnly();
        }
        void OwnerUnloaded(object sender, RoutedEventArgs args) => dialog.Hide();
        try
        {
            ActiveRowEditForm = form; _rowEditDialog = dialog;
            StateChanged += OwnerChanged; Unloaded += OwnerUnloaded;
            if (options is not null) { root.Changed += RootChanged; UpdateFormSize(); }
            Changed(); await dialog.ShowAsync();
            if (saved && !_disposed) ClearError();
            return saved;
        }
        finally
        {
            StateChanged -= OwnerChanged; Unloaded -= OwnerUnloaded;
            if (options is not null) root.Changed -= RootChanged;
            CancelNativeKeyboardHost(dialog);
            if (ReferenceEquals(ActiveRowEditForm, form)) { ActiveRowEditForm = null; _rowEditDialog = null; }
            if (!_disposed) Changed();
        }
    }
    /// <summary>Read actual loaded row-form bounds. Never realizes fields, changes focus
    /// or forces layout. Hidden fields are absent from both editor and action maps.</summary>
    public IReadOnlyDictionary<string, RectD> RowEditActionBounds(UIElement relativeTo)
    {
        var result = new Dictionary<string, RectD>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, FrameworkElement element)
        {
            if (!element.IsLoaded || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var point = element.TransformToVisual(relativeTo).TransformPoint(new Point());
            result[key] = new(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        if (ActiveRowEditForm is { } form)
        {
            foreach (var (field, editor) in form.Editors) Add("field-" + field, editor);
            foreach (var (field, editor) in form.NullToggles) Add("null-" + field, editor);
            foreach (var (field, panel) in form.FieldPanels) Add("panel-" + field, panel);
            Add("viewport", form.FieldsViewport);
        }
        void Visit(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button && button.Name is "PrimaryButton" or "CloseButton")
                    Add(button.Name == "PrimaryButton" ? "update" : "cancel", button);
                else Visit(child);
            }
        }
        if (_rowEditDialog is not null) Visit(_rowEditDialog);
        return result;
    }
}

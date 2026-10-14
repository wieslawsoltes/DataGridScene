using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private Action? _closeColumnChooser;
    public ColumnChooser? ActiveColumnChooser { get; private set; }
    /// <summary>Open a native, live column editor after validating pending cells. Invalid width
    /// input blocks normal dismissal; table replacement/unloading closes without applying it.
    /// Returns false when a pending cell rejects opening. Layout writes are not cell-edit undo.</summary>
    public async Task<bool> ShowColumnChooserAsync()
    {
        RequireView();
        if (_dataUpdateDepth > 0 || ActiveColumnChooser is not null || ActiveRowEditForm is not null)
            throw new InvalidOperationException("Finish the current editor or data update before opening column customization.");
        if (!CommitEdit()) return false;
        if (XamlRoot is null) throw new InvalidOperationException("The grid must be attached to a window before opening column customization.");
        var table = Table!; var layout = ColumnLayout!;
        using var chooser = new ColumnChooser(layout, table.Schema);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Column chooser", Content = chooser,
            CloseButtonText = "Done", RequestedTheme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light };
        bool forced = false;
        void ForceClose()
        {
            if (forced) return;
            forced = true;
            // Detach before Hide can deliver LostFocus to an old width editor.
            chooser.Dispose(); dialog.Hide();
        }
        chooser.ValidateChange = () =>
        {
            if (forced || _disposed || !ReferenceEquals(Table, table) || !ReferenceEquals(ColumnLayout, layout))
                throw new InvalidOperationException("The column editor belongs to a previous grid layout.");
            if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        };
        void StateUpdated(object? sender, EventArgs args)
        {
            if (_disposed || !ReferenceEquals(Table, table) || !ReferenceEquals(ColumnLayout, layout)) { ForceClose(); return; }
            var theme = DarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            if (dialog.RequestedTheme != theme) dialog.RequestedTheme = theme;
        }
        void OwnerUnloaded(object sender, RoutedEventArgs args) => ForceClose();
        dialog.Closing += (_, args) => { if (!forced && !chooser.CommitWidth()) args.Cancel = true; };
        ActiveColumnChooser = chooser; _closeColumnChooser = ForceClose;
        StateChanged += StateUpdated; Unloaded += OwnerUnloaded;
        long tableToken = RegisterPropertyChangedCallback(TableProperty, (_, _) => ForceClose());
        try { await dialog.ShowAsync(); return !forced; }
        finally
        {
            StateChanged -= StateUpdated; Unloaded -= OwnerUnloaded;
            UnregisterPropertyChangedCallback(TableProperty, tableToken);
            chooser.Dispose();
            if (ReferenceEquals(ActiveColumnChooser, chooser)) { ActiveColumnChooser = null; _closeColumnChooser = null; }
        }
    }
    /// <summary>Dismiss host-owned column customization without posting unfinished width text.
    /// Previously applied visibility, pin and width changes remain live.</summary>
    public void CloseColumnChooser() => _closeColumnChooser?.Invoke();
}

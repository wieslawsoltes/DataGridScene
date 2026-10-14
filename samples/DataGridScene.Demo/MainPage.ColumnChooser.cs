using DataGridScene.Controls;
using DataGridScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    /// <summary>Read existing native chooser state only. Do not materialize offscreen items,
    /// run layout, set focus, or mutate controls while reporting diagnostics.</summary>
    public object ColumnChooserSnapshot()
    {
        if (DataGrid.ActiveColumnChooser is not { } chooser) return new { open = false };
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as TextBox;
        return new
        {
            open = true, chooser.SelectedField, chooser.SearchText, scope = chooser.FilterScope.ToString(), chooser.WidthText,
            chooser.HasPendingWidth, chooser.ValidationMessage,
            totalCount = chooser.Entries.Count, resultCount = chooser.Results.Count,
            matchingFields = chooser.Results.Take(100).Select(entry => entry.Field).ToArray(),
            entries = chooser.Entries.Where(entry => entry.Field == chooser.SelectedField).Select(entry => new { entry.Field, entry.Caption, entry.Width, entry.Visible, pin = entry.Pin.ToString() }).ToArray(),
            chooser.MetadataRefreshCount, chooser.SearchPassCount, chooser.ListPublicationCount, chooser.CreatedRowCount, chooser.RowBindCount, chooser.RealizedRowCount,
            // Export anonymous scalar DTOs from the rooted sample, not trimmed Controls records.
            rows = chooser.RealizedRows(this).Select(row => new { row.Identity, row.Field, row.IsChecked, row.Theme, row.Bounds }).ToArray(),
            actions = DataGrid.ColumnChooserActionBounds(this),
            focusedText = focused?.Text, selectionStart = focused?.SelectionStart, selectionLength = focused?.SelectionLength
        };
    }
    // Explicit diagnostic fixture. Uses ordinary public grid APIs; not an interaction substitute.
    public void ConfigureColumnChooserFixture(int columns)
    {
        if (columns is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(columns));
        StopLiveUpdates(); DataGrid.CloseColumnChooser(); DataGrid.CancelEdit();
        DataGrid.ClearCellTemplates(); DataGrid.DetailProvider = null; DataGrid.Renderer.ConditionalFormats.Clear();
        var schema = new GridSchema(Enumerable.Range(0, columns).Select(index => new GridColumn($"C{index:D5}", typeof(int))
        { Caption = $"Column {index:D5}", Band = index % 2 == 0 ? "Even fields" : "Odd fields" }).ToArray());
        var table = new GridTable(schema); table.Add(new GridRecord(1, Enumerable.Range(0, columns).Select(index => (object?)index).ToArray()));
        DataGrid.Table = table; DataGrid.ShowBands = false; DataGrid.ColumnLayout!.SetWidth("C00000", 123.456789012345);
    }
}

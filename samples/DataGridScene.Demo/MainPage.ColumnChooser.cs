using DataGridScene.Controls;
using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    /// <summary>Read existing native chooser state only. Do not materialize offscreen items,
    /// run layout, set focus, or mutate controls while reporting diagnostics.</summary>
    public object ColumnChooserSnapshot()
    {
        if (DataGrid.ActiveColumnChooser is not { } chooser) return new { open = false };
        var focusedElement = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var focused = focusedElement as TextBox;
        bool Within(DependencyObject parent)
        {
            for (var current = focusedElement; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, parent)) return true;
            return false;
        }
        object Combo(ComboBox combo)
        {
            var items = new List<object>(); int focusedIndex = -1;
            // Only three enum choices; ContainerFromIndex reads existing realization,
            // never ScrollIntoView/UpdateLayout or a walk over all schema columns.
            for (int index = 0; index < combo.Items.Count; index++)
                if (combo.ContainerFromIndex(index) is ComboBoxItem { IsLoaded: true } item && item.ActualWidth > 0 && item.ActualHeight > 0)
                {
                    if (Within(item)) focusedIndex = index;
                    var point = item.TransformToVisual(this).TransformPoint(new Point());
                    items.Add(new { index, text = item.Content?.ToString(), bounds = new RectD(point.X, point.Y, item.ActualWidth, item.ActualHeight) });
                }
            return new { isOpen = combo.IsDropDownOpen, combo.SelectedIndex, focusedIndex, focusWithin = Within(combo), items };
        }
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
            focusedAction = chooser.Actions.FirstOrDefault(pair => ReferenceEquals(focused, pair.Value)).Key,
            focusedText = focused?.Text, selectionStart = focused?.SelectionStart, selectionLength = focused?.SelectionLength,
            scopePopup = Combo((ComboBox)chooser.Actions["scope"]), pinPopup = Combo((ComboBox)chooser.Actions["pin"])
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

using DataGridScene.Controls;
using DataGridScene.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private void InitializeRowEditUI()
    {
        if (!_actions.TryGetValue("scenario-Editing", out var anchor) || VisualTreeHelper.GetParent(anchor) is not StackPanel navigation) return;
        void Add(string id, string title, Action run, int offset)
        {
            var button = Button(id, title, run);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Background = Brush("#f7f7f7");
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            var icon = new VectorIcon(SceneGlyph.Edit, 16); _navigationIcons.Add(icon);
            caption.Children.Add(icon); caption.Children.Add(new TextBlock { Text = title, FontSize = 12 }); button.Content = caption;
            ToolTipService.SetToolTip(button, "Edit fields of the focused row in one validated, undoable transaction");
            navigation.Children.Insert(navigation.Children.IndexOf(anchor) + offset, button);
        }
        Add("edit-row", "Edit selected row…", ShowRowEditor, 1);
        Add("edit-row-layout", "Row form layout…", () => ShowRowEditor(CreateRowLayoutOptions()), 2);
    }
    private RowEditFormOptions CreateRowLayoutOptions()
    {
        RowEditFieldLayout[] fields = [
            new("OrderID") { Order = -10, Caption = "Order number" },
            new("Company") { Order = 0, Caption = "Customer", ColumnSpan = 2 },
            new("Quantity") { Order = 10, Caption = "Units" }, new("Limit") { Order = 20 },
            new("Verified") { Order = 30 }, new("Status") { Order = 40 },
            new("Amount") { Order = 50, Caption = "Exact amount", ColumnSpan = 2, StartNewRow = true },
            new("Key") { Order = 60 }, new("Stamp") { Order = 70, ColumnSpan = 2, Caption = "Timestamp" },
            new("Optional") { Order = 80 }, new("Locked") { IsVisible = false },
            new("Notes") { Order = 100, ColumnSpan = 3, RowSpan = 2, StartNewRow = true, Caption = "Order notes" }
        ];
        return new() { Title = "Edit row — custom layout", MaximumColumnCount = 3, MinimumColumnWidth = 280,
            MaximumWidth = 960, MaximumFieldHeight = 420, CaptionPlacement = RowEditCaptionPlacement.Beside,
            Fields = fields.Where(f => DataGrid.Table!.Schema.TryGetColumn(f.Field, out _)).ToArray() };
    }
    private void ShowRowEditor() => ShowRowEditor(null);
    private async void ShowRowEditor(RowEditFormOptions? options)
    {
        try
        {
            var focused = DataGrid.Selection.FocusedCell;
            long? id = focused?.RecordId ?? DataGrid.View?.FilteredRecords.FirstOrDefault()?.Id;
            if (id is null) { SetMessage("Select an existing row to edit."); return; }
            if (await DataGrid.ShowRowEditAsync(id.Value, null, options)) SetMessage($"Updated row {id.Value}. Undo restores the complete row edit.");
        }
        catch (Exception error) { SetMessage(error.Message); }
    }
}

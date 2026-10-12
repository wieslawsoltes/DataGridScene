using DataGridScene.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    private void InitializeRowEditUI()
    {
        if (!_actions.TryGetValue("scenario-Editing", out var anchor) || VisualTreeHelper.GetParent(anchor) is not StackPanel navigation) return;
        var button = Button("edit-row", "Edit selected row…", ShowRowEditor);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Background = Brush("#f7f7f7");
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        var icon = new VectorIcon(SceneGlyph.Edit, 16); _navigationIcons.Add(icon);
        caption.Children.Add(icon); caption.Children.Add(new TextBlock { Text = "Edit selected row…", FontSize = 12 }); button.Content = caption;
        ToolTipService.SetToolTip(button, "Edit every field of the focused row in one validated, undoable transaction");
        navigation.Children.Insert(navigation.Children.IndexOf(anchor) + 1, button);
    }
    private async void ShowRowEditor()
    {
        try
        {
            var focused = DataGrid.Selection.FocusedCell;
            long? id = focused?.RecordId ?? DataGrid.View?.FilteredRecords.FirstOrDefault()?.Id;
            if (id is null) { SetMessage("Select an existing row to edit."); return; }
            if (await DataGrid.ShowRowEditAsync(id.Value)) SetMessage($"Updated row {id.Value}. Undo restores the complete row edit.");
        }
        catch (Exception error) { SetMessage(error.Message); }
    }
}

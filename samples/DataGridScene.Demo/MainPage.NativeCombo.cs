using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DataGridScene.Demo;

public sealed partial class MainPage
{
    // Observe only already-realized native controls. Do not focus, open a popup,
    // apply a template or generate item containers from a diagnostic read.
    private object? NativeComboSnapshot()
    {
        if (XamlRoot is null || DataGrid.Selection.FocusedCell is not { } address ||
            DataGrid.GetCellControl(address.RecordId, address.Field) is not ComboBox combo) return null;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        int focusedIndex = -1;
        bool focusWithin = false;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, combo)) focusWithin = true;
            if (node is ComboBoxItem item && combo.IndexFromContainer(item) is int index && index >= 0)
                focusedIndex = index;
        }
        var items = new List<object>();
        if (combo.IsDropDownOpen)
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.ContainerFromIndex(i) is ComboBoxItem { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } item)
                {
                    var origin = item.TransformToVisual(this).TransformPoint(new Point());
                    items.Add(new { index = i, text = Convert.ToString(combo.Items[i]),
                        bounds = new DataGridScene.Rendering.Skia.RectD(origin.X, origin.Y, item.ActualWidth, item.ActualHeight) });
                }
        return new { address.RecordId, address.Field, isOpen = combo.IsDropDownOpen, combo.IsLoaded,
            combo.SelectedIndex, selected = Convert.ToString(combo.SelectedItem), focusedIndex, focusWithin, items };
    }
}

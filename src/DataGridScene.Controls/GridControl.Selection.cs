using DataGridScene.Core;
using Microsoft.UI.Xaml;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register(
        nameof(SelectionMode), typeof(GridSelectionMode), typeof(GridControl),
        new PropertyMetadata(GridSelectionMode.MultipleRows, (owner, args) => ((GridControl)owner).Selection.Mode = (GridSelectionMode)args.NewValue));
    public GridSelectionMode SelectionMode
    {
        get => (GridSelectionMode)GetValue(SelectionModeProperty);
        set => SetValue(SelectionModeProperty, value);
    }
    private IReadOnlyList<string> SelectionFields => ColumnLayout!.DisplayColumns().Select(column => column.Field).ToArray();
}

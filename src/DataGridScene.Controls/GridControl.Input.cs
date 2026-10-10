using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace DataGridScene.Controls;

public sealed partial class GridControl
{
    private GridHit? _pressedHeader;
    private Point _pressPoint;
    private CellAddress? _pressedCell;
    private CellAddress? _previousCell;
    private GridTable? _previousTable;
    private bool _validDoubleTap;
    private void ResetPointerState()
    {
        _pressedCell = _previousCell = null;
        _previousTable = null; _validDoubleTap = false;
        _pressedHeader = null; _resizing = _dragging = false;
        Renderer.DragMarkerX = null;
    }
    private bool _resizing, _dragging;
    private double _originalWidth;
    private static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private static bool Shift(VirtualKeyModifiers modifiers) => modifiers.HasFlag(VirtualKeyModifiers.Shift);
    private static bool Control(VirtualKeyModifiers modifiers) => modifiers.HasFlag(VirtualKeyModifiers.Control);

    private void WirePointerInput()
    {
        _surface.PointerPressed += SurfacePressed;
        _surface.PointerMoved += SurfaceMoved;
        _surface.PointerReleased += SurfaceReleased;
        _surface.PointerCaptureLost += (_, _) => { _pressedHeader = null; _pressedCell = null; _resizing = _dragging = false; Renderer.DragMarkerX = null; Redraw(); };
        _surface.PointerWheelChanged += (_, args) =>
        {
            var point = args.GetCurrentPoint(_surface);
            double amount = point.Properties.MouseWheelDelta / 120d;
            if (point.Properties.IsHorizontalMouseWheel || Shift(args.KeyModifiers)) ScrollTo(_offsetX - amount * 96, _offsetY);
            else ScrollTo(_offsetX, _offsetY - amount * RowHeight * 3);
            args.Handled = true;
        };
        _surface.DoubleTapped += (_, args) =>
        {
            if (Frame is null) return;
            var point = args.GetPosition(_surface); var hit = Frame.HitTest(point.X, point.Y);
            if (_validDoubleTap && hit.Region == HitRegion.DataCell && hit.Field is not null) Safe(() => BeginEdit(View!.Rows[hit.RowIndex].Record!.Id, hit.Field));
            else if (hit.Region == HitRegion.Header && hit.Field is not null) Safe(() => BestFit(hit.Field));
            args.Handled = true;
        };
        _surface.RightTapped += (_, args) =>
        {
            if (Frame is null) return;
            var point = args.GetPosition(_surface); ShowContext(Frame.HitTest(point.X, point.Y), point); args.Handled = true;
        };
        _surface.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.TranslateInertia;
        _surface.ManipulationDelta += (_, args) =>
        {
            if (_pressedHeader is null && _pressedCell is null) ScrollTo(_offsetX - args.Delta.Translation.X, _offsetY - args.Delta.Translation.Y);
            args.Handled = true;
        };
    }
    private void SurfacePressed(object sender, PointerRoutedEventArgs args)
    {
        if (Frame is null || View is null || ColumnLayout is null) return;
        var pointer = args.GetCurrentPoint(_surface);
        if (!pointer.Properties.IsLeftButtonPressed && pointer.PointerDevice.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse) return;
        if (!CommitEdit()) { args.Handled = true; return; }
        Focus(FocusState.Pointer);
        var point = pointer.Position; var hit = Frame.HitTest(point.X, point.Y);
        _validDoubleTap = false;
        if (hit.Region != HitRegion.DataCell) { _previousCell = null; _previousTable = null; }
        Safe(() =>
        {
            switch (hit.Region)
            {
                case HitRegion.Header when hit.Field is null:
                    if (Selection.Count > 0 && Selection.Count == View.FilteredRecords.Count) Selection.Clear(); else Selection.SelectAll(View, SelectionFields);
                    break;
                case HitRegion.Header when hit.Field is not null:
                    _pressPoint = point; _pressedHeader = hit;
                    _resizing = Math.Abs(point.X - hit.Bounds.Right) <= 6;
                    _originalWidth = ColumnLayout.Columns.Single(column => column.Field.Equals(hit.Field, StringComparison.OrdinalIgnoreCase)).Width;
                    _surface.CapturePointer(args.Pointer);
                    break;
                case HitRegion.Filter when hit.Field is not null: BeginFilterEdit(hit.Field); break;
                case HitRegion.GroupRemove when hit.Field is not null: GroupColumn(hit.Field, false); break;
                case HitRegion.GroupChip when hit.Field is not null:
                    View.SetQuery(View.Query with { Groups = View.Query.Groups.Select(group => group.Field == hit.Field ? group with { Direction = group.Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending } : group).ToArray() });
                    break;
                case HitRegion.GroupRow: View.ToggleGroup(hit.GroupKey!); break;
                case HitRegion.Indicator:
                    var row = View.Rows[hit.RowIndex].Record!;
                    if (point.X >= 24) View.ToggleDetail(row.Id);
                    else Selection.Select(View, new(row.Id, ColumnLayout.DisplayColumns()[0].Field), Shift(args.KeyModifiers), true, SelectionFields);
                    break;
                case HitRegion.DataCell when hit.Field is not null:
                    var record = View.Rows[hit.RowIndex].Record!;
                    var address = new CellAddress(record.Id, hit.Field);
                    _validDoubleTap = ReferenceEquals(_previousTable, Table) && _previousCell == address;
                    _previousTable = Table; _previousCell = address;
                    Selection.Select(View, address, Shift(args.KeyModifiers), Control(args.KeyModifiers), SelectionFields);
                    if (!Control(args.KeyModifiers) && pointer.PointerDevice.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Mouse)
                    {
                        _pressedCell = address; _pressPoint = point; _surface.CapturePointer(args.Pointer);
                    }
                    var column = Table!.Schema[hit.Field];
                    if (!IsReadOnly && !column.IsReadOnly && column.Presentation == ColumnPresentation.CheckBox)
                        History!.Execute([new(record.Id, hit.Field, column.GetValue(record) is not true)]);
                    break;
            }
        });
        args.Handled = true;
    }
    private void SurfaceMoved(object sender, PointerRoutedEventArgs args)
    {
        if (ColumnLayout is null || Frame is null) return;
        var point = args.GetCurrentPoint(_surface).Position;
        if (_pressedCell is not null && View is not null)
        {
            if (Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 5)
            {
                if (point.Y < Frame.BodyTop) _offsetY = Math.Max(0, _offsetY - RowHeight);
                else if (point.Y > Frame.FooterTop) _offsetY += RowHeight;
                UpdateGeometry();
                var target = Frame!.HitTest(Math.Clamp(point.X, GridLayout.IndicatorWidth + 1, Math.Max(GridLayout.IndicatorWidth + 1, Frame.Width - 1)),
                    Math.Clamp(point.Y, Frame.BodyTop + 1, Math.Max(Frame.BodyTop + 1, Frame.FooterTop - 1)));
                if (target.Region == HitRegion.DataCell && target.Field is not null)
                    Selection.Select(View, new(View.Rows[target.RowIndex].Record!.Id, target.Field), extend: true, columnOrder: SelectionFields);
            }
            args.Handled = true; return;
        }
        if (_pressedHeader?.Field is not string field) return;
        if (_resizing) Safe(() => ColumnLayout.SetWidth(field, Math.Clamp(_originalWidth + point.X - _pressPoint.X, 36, 4096)));
        else if (Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 6)
        {
            _dragging = true;
            var target = Frame.HitTest(point.X, point.Y);
            Renderer.DragMarkerX = target.Region == HitRegion.Header ? target.Bounds.X : null; Redraw();
        }
        args.Handled = true;
    }
    private void SurfaceReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pressedCell is not null)
        {
            _pressedCell = null; _surface.ReleasePointerCapture(args.Pointer); args.Handled = true; return;
        }
        var pressed = _pressedHeader;
        if (pressed?.Field is not string field || Frame is null) return;
        var point = args.GetCurrentPoint(_surface).Position;
        bool resizing = _resizing, dragging = _dragging;
        _pressedHeader = null; _resizing = _dragging = false; Renderer.DragMarkerX = null;
        _surface.ReleasePointerCapture(args.Pointer);
        Safe(() =>
        {
            if (resizing) return;
            var target = Frame.HitTest(point.X, point.Y);
            if (dragging)
            {
                if (point.Y < Frame.BandTop && ShowGroupPanel) GroupColumn(field);
                else if (target.Region == HitRegion.Header && target.Field is not null) ColumnLayout!.MoveBefore(field, target.Field);
            }
            else if (point.X > pressed.Bounds.Right - 22) ShowColumnFilter(field, point);
            else CycleSort(field, Shift(args.KeyModifiers));
        });
        Redraw(); args.Handled = true;
    }
    private void GridKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_editor is not null || View is null || ColumnLayout is null) return;
        bool control = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        bool handled = true;
        Safe(() =>
        {
            if (control && args.Key == VirtualKey.A) Selection.SelectAll(View, SelectionFields);
            else if (control && args.Key == VirtualKey.C) CopySelection();
            else if (control && args.Key == VirtualKey.V) PasteClipboard();
            else if (control && args.Key == VirtualKey.Z) { if (!IsReadOnly) History!.Undo(); }
            else if (control && args.Key == VirtualKey.Y) { if (!IsReadOnly) History!.Redo(); }
            else if (control && args.Key == VirtualKey.F) SearchRequested?.Invoke(this, EventArgs.Empty);
            else if (args.Key == VirtualKey.Escape) Selection.Clear();
            else if (args.Key is VirtualKey.F2 or VirtualKey.Enter && Selection.FocusedCell is { } edit) BeginEdit(edit.RecordId, edit.Field);
            else if (args.Key == VirtualKey.F4 && Selection.FocusedCell is { } detail) View.ToggleDetail(detail.RecordId);
            else if (args.Key == VirtualKey.Space && Selection.FocusedCell is { } cell)
            {
                var column = Table!.Schema[cell.Field];
                if (!IsReadOnly && !column.IsReadOnly && column.DataType == typeof(bool)) History!.Execute([new(cell.RecordId, cell.Field, column.GetValue(Table[cell.RecordId]) is not true)]);
                else Selection.Select(View, cell, toggle: true, columnOrder: SelectionFields);
            }
            else if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Tab)
                Navigate(args.Key, shift, control);
            else handled = false;
        });
        args.Handled = handled;
    }
    public void Navigate(VirtualKey key, bool shift = false, bool control = false)
    {
        if (View is null || ColumnLayout is null || !CommitEdit()) return;
        var columns = ColumnLayout.DisplayColumns();
        if (View.Rows.Count == 0 || columns.Count == 0) return;
        int rowIndex = Selection.FocusedCell is { } focused ? View.IndexOfRecord(focused.RecordId) : -1;
        int columnIndex = Selection.FocusedCell is { } current ? columns.ToList().FindIndex(column => column.Field == current.Field) : 0;
        if (columnIndex < 0) columnIndex = 0;
        int direction = 1;
        if (rowIndex < 0) rowIndex = 0;
        else switch (key)
        {
            case VirtualKey.Up: rowIndex--; direction = -1; break;
            case VirtualKey.Down: rowIndex++; break;
            case VirtualKey.Left: columnIndex--; break;
            case VirtualKey.Right: columnIndex++; break;
            case VirtualKey.Tab:
                columnIndex += shift ? -1 : 1;
                if (columnIndex < 0) { columnIndex = columns.Count - 1; rowIndex--; direction = -1; }
                else if (columnIndex >= columns.Count) { columnIndex = 0; rowIndex++; }
                break;
            case VirtualKey.Home: columnIndex = 0; if (control) rowIndex = 0; break;
            case VirtualKey.End: columnIndex = columns.Count - 1; if (control) { rowIndex = View.Rows.Count - 1; direction = -1; } break;
            case VirtualKey.PageUp: rowIndex -= Math.Max(1, (int)((Frame?.BodyHeight ?? 300) / RowHeight)); direction = -1; break;
            case VirtualKey.PageDown: rowIndex += Math.Max(1, (int)((Frame?.BodyHeight ?? 300) / RowHeight)); break;
        }
        rowIndex = Math.Clamp(rowIndex, 0, View.Rows.Count - 1);
        int candidate = rowIndex;
        while (candidate >= 0 && candidate < View.Rows.Count && View.Rows[candidate].Kind != GridRowKind.Data) candidate += direction;
        if (candidate < 0 || candidate >= View.Rows.Count)
        {
            candidate = rowIndex;
            while (candidate >= 0 && candidate < View.Rows.Count && View.Rows[candidate].Kind != GridRowKind.Data) candidate -= direction;
        }
        if (candidate < 0 || candidate >= View.Rows.Count) return;
        var next = new CellAddress(View.Rows[candidate].Record!.Id, columns[Math.Clamp(columnIndex, 0, columns.Count - 1)].Field);
        Selection.Select(View, next, extend: shift && key != VirtualKey.Tab, columnOrder: SelectionFields); EnsureVisible(next); Focus(FocusState.Keyboard);
    }
    private MenuFlyoutItem Menu(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => Safe(action); return item;
    }
    private void ShowContext(GridHit hit, Point point)
    {
        var flyout = new MenuFlyout();
        if (hit.Field is string field && hit.Region is HitRegion.Header or HitRegion.Summary)
        {
            if (hit.Region == HitRegion.Summary)
            {
                foreach (var kind in Enum.GetValues<SummaryKind>())
                {
                    if (kind is SummaryKind.Sum or SummaryKind.Average && !GridValue.IsNumericType(Table!.Schema[field].DataType)) continue;
                    flyout.Items.Add(Menu(kind.ToString(), () => View!.SetQuery(View.Query with { Summaries = View.Query.Summaries.Where(summary => summary.Field != field).Append(new SummaryDescriptor(field, kind)).ToArray() })));
                }
                flyout.Items.Add(Menu("Clear summary", () => View!.SetQuery(View.Query with { Summaries = View.Query.Summaries.Where(summary => summary.Field != field).ToArray() })));
            }
            else
            {
                flyout.Items.Add(Menu("Sort ascending", () => SortColumn(field, SortDirection.Ascending)));
                flyout.Items.Add(Menu("Sort descending", () => SortColumn(field, SortDirection.Descending)));
                flyout.Items.Add(Menu("Clear sorting", () => SortColumn(field, null)));
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(Menu("Group by this column", () => GroupColumn(field)));
                flyout.Items.Add(Menu("Remove from grouping", () => GroupColumn(field, false)));
                flyout.Items.Add(Menu("Best fit", () => BestFit(field)));
                flyout.Items.Add(Menu("Freeze left", () => ColumnLayout!.SetPin(field, ColumnPin.Left)));
                flyout.Items.Add(Menu("Freeze right", () => ColumnLayout!.SetPin(field, ColumnPin.Right)));
                flyout.Items.Add(Menu("Unfreeze", () => ColumnLayout!.SetPin(field, ColumnPin.None)));
                flyout.Items.Add(Menu("Hide this column", () => ColumnLayout!.SetVisible(field, false)));
                flyout.Items.Add(Menu("Filter…", () => ShowColumnFilter(field, point)));
            }
        }
        else
        {
            flyout.Items.Add(Menu("Copy selected rows", CopySelection));
            flyout.Items.Add(Menu("Paste cells", PasteClipboard));
            flyout.Items.Add(Menu("Select all filtered records", () => Selection.SelectAll(View!, SelectionFields)));
            flyout.Items.Add(Menu("Clear selection", Selection.Clear));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Menu("Expand all groups", () => View!.ExpandAll()));
            flyout.Items.Add(Menu("Collapse all groups", () => View!.CollapseAll()));
            flyout.Items.Add(Menu("Undo", () => { if (!IsReadOnly) History!.Undo(); }));
            flyout.Items.Add(Menu("Redo", () => { if (!IsReadOnly) History!.Redo(); }));
        }
        flyout.ShowAt(_surface, new FlyoutShowOptions { Position = point });
    }
    private void ShowColumnFilter(string field, Point point)
    {
        if (View is null || Table is null) return;
        var content = new StackPanel { Spacing = 8, Width = 260 };
        content.Children.Add(new TextBlock { Text = Table.Schema[field].Caption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var search = new TextBox { PlaceholderText = "Contains, =value, >=value, null…", Text = _filterTexts.GetValueOrDefault(field) ?? "" };
        content.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Apply" }; var clear = new Button { Content = "Clear" }; buttons.Children.Add(apply); buttons.Children.Add(clear); content.Children.Add(buttons);
        var flyout = new Flyout { Content = content };
        apply.Click += (_, _) => Safe(() => { SetColumnFilter(field, search.Text); flyout.Hide(); });
        clear.Click += (_, _) => Safe(() => { SetColumnFilter(field, ""); flyout.Hide(); });
        search.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { Safe(() => { SetColumnFilter(field, search.Text); flyout.Hide(); }); args.Handled = true; } };
        var column = Table.Schema[field];
        var values = Table.Records.Select(record => column.GetValue(record)).GroupBy(GridValue.Key).Select(group => group.First()).Take(101).ToArray();
        content.Children.Add(new TextBlock { Text = values.Length > 100 ? "First 100 distinct values · use filter for others" : "Choose a value", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var list = new StackPanel { Spacing = 2 };
        foreach (var value in values.Take(100))
        {
            var button = new Button { Content = value is null ? "(Blanks)" : column.Display(value, Table.Schema.Culture), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
            button.Click += (_, _) => Safe(() => { SetColumnFilter(field, value is null ? "null" : "=" + Convert.ToString(value, Table.Schema.Culture)); flyout.Hide(); }); list.Children.Add(button);
        }
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        flyout.ShowAt(_surface, new FlyoutShowOptions { Position = point });
    }
}

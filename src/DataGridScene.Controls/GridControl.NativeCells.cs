using DataGridScene.Core;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.Foundation;
using Windows.System;

namespace DataGridScene.Controls;

public sealed record NativeCellInfo(CellAddress Address, string ControlType, RectD Bounds, RectD ClipBounds,
    bool IsDirty, bool CanEdit, string? Error)
{
    public bool IsSelected { get; init; }
    public bool IsFocused { get; init; }
}

public sealed partial class GridControl
{
    private sealed class NativeHost(GridCellTemplate template, FrameworkElement content)
    {
        public readonly GridCellTemplate Template = template;
        public readonly FrameworkElement Content = content;
        public readonly Border Container = new() { Child = content, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        public readonly RectangleGeometry Clip = new();
        public readonly Border Adorner = new() { IsHitTestVisible = false };
        public GridCellVisualState? Appearance;
        public GridCellEditBuffer? Buffer;
        public NativeCellPlacement? Placement;
        public PropertyChangedEventHandler? Listener;
        public int Version = -1;
        public bool ReadOnly, Binding;
    }
    private readonly Canvas _nativeCells = new();
    private readonly Dictionary<string, GridCellTemplate> _editCellTemplates = new(StringComparer.OrdinalIgnoreCase);
    private CellAddress? _nativeEditCell;
    private readonly Dictionary<string, GridCellTemplate> _cellTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CellAddress, NativeHost> _cellHosts = [];
    private readonly Dictionary<GridCellTemplate, Stack<NativeHost>> _cellPools = [];
    private readonly HashSet<string> _cellFields = new(StringComparer.OrdinalIgnoreCase);
    private GridFrame? _nativeFrame;
    private bool _syncingNativeCells, _nativeSyncPending, _committingNativeCells;
    private int _pooledCells;
    public int RealizedCellControlCount => _cellHosts.Count;
    public int PooledCellControlCount => _pooledCells;
    public long CreatedCellControlCount { get; private set; }
    public long ReusedCellControlCount { get; private set; }
    public long CellControlBindCount { get; private set; }
    public long CellControlPlacementCount { get; private set; }
    public long CellControlAppearanceCount { get; private set; }
    private readonly SolidColorBrush _cellFocusBrush = new(Microsoft.UI.Colors.DodgerBlue);
    private readonly SolidColorBrush _cellErrorBrush = new(Microsoft.UI.Colors.Red);
    public const int MaximumRealizedCellControls = 4096;
    public const int MaximumPooledCellControls = 128;
    public bool HasPendingCellEdits => _cellHosts.Values.Any(host => host.Buffer?.IsDirty == true);

    /// <summary>Register normal Uno content for a column. Null returns to Skia rendering.
    /// Templates are application configuration and are not serialized in workspace JSON.</summary>
    public void SetCellTemplate(string field, GridCellTemplate? template)
    {
        RequireView(); field = Table!.Schema[field].Field;
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        if (_cellTemplates.TryGetValue(field, out var previous) && ReferenceEquals(previous, template)) return;
        foreach (var pair in _cellHosts.Where(pair => pair.Key.Field == field).ToArray()) RecycleCell(pair.Key, pair.Value, retain: false);
        if (template is null) { _cellTemplates.Remove(field); if (!_displaySelectors.ContainsKey(field)) _cellFields.Remove(field); }
        else { _cellTemplates[field] = template; _cellFields.Add(field); }
        InvalidateCellTemplates();
    }
    /// <summary>Use a native control only during editing; other cells keep the inexpensive canvas display.</summary>
    public void SetCellEditTemplate(string field, GridCellTemplate? template)
    {
        RequireView(); field = Table!.Schema[field].Field;
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        if (template is null) _editCellTemplates.Remove(field); else _editCellTemplates[field] = template;
        InvalidateCellTemplates();
    }
    public void SetCellEditDataTemplate(string field, DataTemplate? template) =>
        SetCellEditTemplate(field, template is null ? null : GridCellTemplate.FromDataTemplate(template));
    private bool BeginNativeCellEdit(long recordId, string field)
    {
        if (!_cellFields.Contains(field) && !_editCellTemplates.ContainsKey(field) && !_editSelectors.ContainsKey(field)) return false;
        if (!CommitEdit()) return true;
        var address = new CellAddress(recordId, field);
        var edit = !IsReadOnly && !Table!.Schema[field].IsReadOnly ? ResolveCellTemplate(address, editing: true) : null;
        if (edit is null && ResolveCellTemplate(address, editing: false) is null) return false;
        if (edit is not null) _nativeEditCell = address;
        Selection.Select(View!, new(recordId, field)); EnsureVisible(new(recordId, field));
        _nativeFrame = null; SynchronizeCellControls();
        if (_cellHosts.TryGetValue(new(recordId, field), out var host))
        {
            FocusCell(host);
            var buffer = host.Buffer;
            DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(host.Buffer, buffer) && buffer?.IsActive == true) FocusCell(host); });
        }
        return true;
    }
    public void SetCellDataTemplate(string field, DataTemplate? template) =>
        SetCellTemplate(field, template is null ? null : GridCellTemplate.FromDataTemplate(template));
    public void ClearCellTemplates()
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        ResetCellControls(); _cellTemplates.Clear(); _editCellTemplates.Clear(); _displaySelectors.Clear(); _editSelectors.Clear(); _templateChoices.Clear(); _cellFields.Clear(); Renderer.IsNativeCell = null; Redraw();
    }
    public IReadOnlyList<NativeCellInfo> GetNativeCells() => _cellHosts.Values.Where(host => host.Buffer is not null && host.Placement is not null)
        .Select(host => new NativeCellInfo(host.Buffer!.Address, host.Content.GetType().Name, host.Placement!.Bounds,
            host.Placement.ClipBounds, host.Buffer.IsDirty, host.Buffer.CanEdit, host.Buffer.Error) { IsSelected = host.Appearance?.IsSelected == true, IsFocused = host.Appearance?.IsFocused == true }).ToArray();
    /// <summary>Returns the currently realized element only; never materializes an offscreen cell.</summary>
    public FrameworkElement? GetCellControl(long recordId, string field) =>
        Table is not null && Table.Schema.TryGetColumn(field, out var column) && _cellHosts.TryGetValue(new(recordId, column.Field), out var host) ? host.Content : null;

    private void InitializeCellControls()
    {
        _root.Children.Insert(1, _nativeCells);
        _nativeCells.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (Frame is not null) { var point = args.GetCurrentPoint(_surface).Position; UpdateHover(Frame.HitTest(point.X, point.Y)); }
        }), true);
        _nativeCells.PointerExited += (_, _) => UpdateHover(null);
        _nativeCells.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            if (IsNativeWheelConsumer(args.OriginalSource as DependencyObject)) return;
            var point = args.GetCurrentPoint(_nativeCells); double amount = point.Properties.MouseWheelDelta / 120d;
            if (point.Properties.IsHorizontalMouseWheel || Shift(args.KeyModifiers)) ScrollTo(_offsetX - amount * 96, _offsetY);
            else ScrollTo(_offsetX, _offsetY - amount * RowHeight * 3);
            args.Handled = true;
        }), true);
        Unloaded += (_, _) => { if (!_disposed && CommitEdit()) ResetCellControls(); };
    }
    private bool IsNativeWheelConsumer(DependencyObject? source)
    {
        bool scrollable = false;
        for (var element = source; element is not null && !ReferenceEquals(element, _nativeCells); element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox text) return text.AcceptsReturn;
            if (element is Slider or ComboBox { IsDropDownOpen: true } or CalendarDatePicker { IsCalendarOpen: true }) return true;
            if (element is ScrollViewer viewer && viewer.ScrollableHeight > 0) scrollable = true;
        }
        return scrollable;
    }
    private NativeHost AcquireCell(GridCellTemplate template)
    {
        if (_cellPools.TryGetValue(template, out var pool) && pool.TryPop(out var recycled))
        { _pooledCells--; ReusedCellControlCount++; if (pool.Count == 0) _cellPools.Remove(template); return recycled; }
        var element = template.Create() ?? throw new InvalidOperationException("A cell factory returned null.");
        if (VisualTreeHelper.GetParent(element) is not null || _cellHosts.Values.Any(host => ReferenceEquals(host.Content, element)))
            throw new InvalidOperationException("A cell factory must create a fresh, unparented element.");
        var host = new NativeHost(template, element); host.Container.Clip = host.Clip;
        // Paint the focus/error outline above opaque native content without shrinking
        // the editor's measure area or moving its text when validation state changes.
        host.Container.Child = null;
        var body = new Grid(); body.Children.Add(element); body.Children.Add(host.Adorner); host.Container.Child = body;
        host.Container.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (_syncingNativeCells || host.Binding || host.Buffer is not { IsActive: true } buffer) return;
            if (!CommitOtherCellControls(buffer)) { args.Handled = true; return; }
            Selection.Select(View!, buffer.Address, Shift(args.KeyModifiers), Control(args.KeyModifiers), SelectionFields);
        }), true);
        host.Container.GotFocus += (_, _) =>
        {
            if (_syncingNativeCells || host.Binding || host.Buffer is not { IsActive: true } buffer) return;
            if (!CommitOtherCellControls(buffer)) return;
            if (Selection.FocusedCell != buffer.Address) Selection.Select(View!, buffer.Address, columnOrder: SelectionFields);
        };
        host.Container.LostFocus += (_, _) =>
        {
            var buffer = host.Buffer;
            if (buffer is null || host.Binding || _syncingNativeCells || !host.Template.CommitOnLostFocus) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(host.Buffer, buffer) || !buffer.IsActive || IsFocusWithin(host.Container)) return;
                if (!CommitCellBuffer(buffer)) FocusCell(host);
            });
        };
        host.Container.KeyDown += (_, args) =>
        {
            if (args.Handled || host.Buffer is not { IsActive: true } buffer) return;
            if (args.Key == VirtualKey.Escape)
            { buffer.Cancel(); RefreshCellHost(host); _nativeEditCell = null; _nativeFrame = null; SynchronizeCellControls(); ClearError(); Focus(FocusState.Keyboard); args.Handled = true; }
            else if (args.Key == VirtualKey.Enter && !(args.OriginalSource is TextBox { AcceptsReturn: true }))
            { if (CommitOtherCellControls()) Focus(FocusState.Keyboard); args.Handled = true; }
            // Tab is native focus traversal, not grid navigation. Commit before leaving.
            else if (args.Key == VirtualKey.Tab && !CommitCellBuffer(buffer)) { args.Handled = true; FocusCell(host); }
        };
        if (element is NumberBox)
            host.Container.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) =>
            {
                if (args.Key == VirtualKey.Enter && host.Buffer is { IsActive: true })
                { if (CommitOtherCellControls()) Focus(FocusState.Keyboard); args.Handled = true; }
            }), true);
        CreatedCellControlCount++; return host;
    }
    private bool IsFocusWithin(DependencyObject parent)
    {
        if (XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, parent)) return true;
        return false;
    }
    private void FocusCell(NativeHost host)
    {
        static Control? First(DependencyObject element)
        {
            if (element is Control { IsTabStop: true, IsEnabled: true } control) return control;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (First(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
            return null;
        }
        First(host.Content)?.Focus(FocusState.Programmatic);
    }
    private void BindCell(NativeHost host, CellAddress address)
    {
        var buffer = new GridCellEditBuffer(Table!, History!, address.RecordId, address.Field, () => !_disposed && !IsReadOnly);
        host.Buffer = buffer;
        host.Listener = (_, _) =>
        {
            if (host.Binding || _syncingNativeCells || _committingNativeCells || !ReferenceEquals(host.Buffer, buffer)) return;
            RefreshCellHost(host);
            if (buffer.Error is not null) Report(new InvalidOperationException(buffer.Error));
            if (_nativeSyncPending && !buffer.IsCommitting) QueueNativeSynchronization();
        };
        buffer.PropertyChanged += host.Listener;
        host.Binding = true;
        try { buffer.SynchronizeBinding(() => host.Content.DataContext = buffer); }
        finally { host.Binding = false; }
        RefreshCellHost(host);
        AutomationProperties.SetAutomationId(host.Content, $"GridCell-{address.RecordId}-{address.Field}");
        AutomationProperties.SetName(host.Container, $"Row {address.RecordId}, {buffer.Column.Caption}");
    }
    private void RefreshCellHost(NativeHost host)
    {
        if (host.Binding || host.Buffer is not { IsActive: true } buffer) return;
        host.Binding = true;
        try
        {
            buffer.SynchronizeBinding(() => host.Template.Bind?.Invoke(host.Content, buffer)); CellControlBindCount++;
            host.Version = buffer.Record.Version; host.ReadOnly = !buffer.CanEdit;
            RefreshCellAppearance(host);
            ToolTipService.SetToolTip(host.Container, buffer.Error);
        }
        finally { host.Binding = false; }
    }
    private void RefreshCellAppearance(NativeHost host)
    {
        if (host.Buffer is not { } buffer) return;
        var state = new GridCellVisualState(Selection.ContainsCell(buffer.RecordId, buffer.Field),
            Selection.FocusedCell == buffer.Address, _nativeEditCell == buffer.Address,
            Renderer.HoveredRecordId == buffer.RecordId, DarkTheme);
        var accent = Renderer.Palette.Accent;
        _cellFocusBrush.Color = Windows.UI.Color.FromArgb(accent.Alpha, accent.Red, accent.Green, accent.Blue);
        host.Adorner.BorderThickness = buffer.Error is not null || state.IsFocused ? new Thickness(1) : new Thickness(0);
        host.Adorner.BorderBrush = buffer.Error is not null ? _cellErrorBrush : _cellFocusBrush;
        if (host.Appearance == state) return;
        buffer.SynchronizeBinding(() => host.Template.UpdateAppearance?.Invoke(host.Content, state));
        host.Appearance = state; CellControlAppearanceCount++;
    }
    private void SynchronizeCellControls()
    {
        if (_disposed || _dataUpdateDepth > 0) return;
        if (_syncingNativeCells || _committingNativeCells || _cellHosts.Values.Any(host => host.Buffer?.IsCommitting == true)) { _nativeSyncPending = true; return; }
        if (Frame is null || Table is null || View is null || _cellFields.Count == 0 && _nativeEditCell is null)
        { if (_cellHosts.Count > 0) ResetCellControls(); return; }
        // Selection pruning can notify before the row-height rebuild on a reset.
        // Never reconcile against a frame belonging to the previous view/row count.
        if (!ReferenceEquals(View.Table, Table) || !ReferenceEquals(Frame.View, View) || Frame.Heights.Count != View.Rows.Count)
        { _nativeFrame = null; return; }
        _syncingNativeCells = true;
        try
        {
            Renderer.IsNativeCell ??= address => _cellHosts.TryGetValue(address, out var host) && host.Placement is not null;
            var frame = Frame;
            bool geometryChanged = !ReferenceEquals(_nativeFrame, frame);
            bool choicesChanged = _displaySelectors.Count > 0 || _editSelectors.Count > 0;
            choicesChanged &= _templateTableRevision != Table.Revision || _templateReadOnly != IsReadOnly;
            if (geometryChanged || choicesChanged)
            {
                var fields = _cellFields;
                if (_nativeEditCell is { } edit) { fields = new(_cellFields, StringComparer.OrdinalIgnoreCase); fields.Add(edit.Field); }
                var placements = NativeCellGeometry.Collect(frame, fields, MaximumRealizedCellControls)
                    .Where(cell => _cellFields.Contains(cell.Address.Field) || cell.Address == _nativeEditCell).ToArray();
                // Prune before invoking application code: a selector that throws midway
                // through repeated scrolls must not accumulate offscreen cached choices.
                var visible = placements.Select(cell => cell.Address).ToHashSet();
                PruneTemplateChoices(visible);
                var selected = new Dictionary<CellAddress, GridCellTemplate>();
                // Resolve every candidate before changing hosts. Versioned cache entries
                // include null (canvas fallback); selectors never run from Draw().
                foreach (var placement in placements)
                {
                    var address = placement.Address;
                    GridCellTemplate? template;
                    if (_cellHosts.TryGetValue(address, out var active) && active.Buffer?.IsDirty == true)
                        template = active.Template; // Don't replace an editor with pending input.
                    else template = (address == _nativeEditCell ? ResolveCellTemplate(address, editing: true) : null)
                        ?? ResolveCellTemplate(address, editing: false);
                    if (template is not null) selected.Add(address, template);
                }
                placements = placements.Where(cell => selected.ContainsKey(cell.Address)).ToArray();
                GridCellTemplate Template(CellAddress address) => selected[address];
                var wanted = selected.Keys.ToHashSet();
                foreach (var pair in _cellHosts.ToArray())
                {
                    var host = pair.Value;
                    if (wanted.Contains(pair.Key) && ReferenceEquals(host.Template, Template(pair.Key)) && Table.Contains(pair.Key.RecordId) && ReferenceEquals(host.Buffer!.Record, Table[pair.Key.RecordId])) continue;
                    // External shaping may hide an invalid edit. Retain its buffer and control,
                    // but hide it rather than recycling away the pending value. Explicit cancel
                    // or successful commit releases it. Normal scroll/filter commands commit first.
                    if (host.Buffer is { IsDirty: true } buffer && !CommitCellBuffer(buffer))
                    { host.Placement = null; host.Container.Visibility = Visibility.Collapsed; continue; }
                    RecycleCell(pair.Key, host, retain: true);
                }
                foreach (var placement in placements)
                {
                    if (!_cellHosts.TryGetValue(placement.Address, out var host))
                    {
                        if (_cellHosts.Count >= MaximumRealizedCellControls) throw new InvalidOperationException("Pending native cell edits reached the control limit. Commit or cancel them.");
                        host = AcquireCell(Template(placement.Address));
                        try
                        {
                            BindCell(host, placement.Address); _cellHosts.Add(placement.Address, host); _nativeCells.Children.Add(host.Container);
                        }
                        catch { _cellHosts.Remove(placement.Address); ReleaseCellHost(host); throw; }
                    }
                    if (!ReferenceEquals(host.Buffer!.Record, Table[placement.Address.RecordId])) continue;
                    host.Container.Visibility = Visibility.Visible;
                    if (host.Placement != placement)
                    {
                        host.Placement = placement; var bounds = placement.Bounds; var clip = placement.ClipBounds;
                        Canvas.SetLeft(host.Container, bounds.X); Canvas.SetTop(host.Container, bounds.Y);
                        host.Container.Width = bounds.Width; host.Container.Height = bounds.Height;
                        host.Clip.Rect = new Rect(clip.X - bounds.X, clip.Y - bounds.Y, clip.Width, clip.Height);
                        CellControlPlacementCount++;
                    }
                }
                _nativeFrame = frame; _templateTableRevision = Table.Revision; _templateReadOnly = IsReadOnly;
                if (!ReferenceEquals(frame, Frame)) _nativeSyncPending = true;
            }
            foreach (var host in _cellHosts.Values)
            {
                if (host.Buffer is { } buffer && (host.Version != buffer.Record.Version || host.ReadOnly == buffer.CanEdit))
                { buffer.Refresh(); RefreshCellHost(host); }
                else RefreshCellAppearance(host);
            }
        }
        catch (Exception error) { Report(error); }
        finally
        {
            _syncingNativeCells = false;
            if (_nativeSyncPending) QueueNativeSynchronization();
        }
    }
    private bool _nativeSynchronizationQueued;
    private void QueueNativeSynchronization()
    {
        if (_nativeSynchronizationQueued || _disposed) return;
        _nativeSynchronizationQueued = true;
        DispatcherQueue.TryEnqueue(() => { _nativeSynchronizationQueued = false; _nativeSyncPending = false; SynchronizeCellControls(); });
    }
    private void RecycleCell(CellAddress address, NativeHost host, bool retain)
    {
        _cellHosts.Remove(address);
        if (IsFocusWithin(host.Container)) Focus(FocusState.Programmatic);
        _nativeCells.Children.Remove(host.Container);
        UnbindCell(host);
        if (retain && _pooledCells < MaximumPooledCellControls)
        {
            if (!_cellPools.TryGetValue(host.Template, out var pool)) _cellPools.Add(host.Template, pool = new());
            pool.Push(host); _pooledCells++;
        }
        else ReleaseCellHost(host);
    }
    private void UnbindCell(NativeHost host)
    {
        var buffer = host.Buffer; host.Buffer = null; host.Placement = null; host.Appearance = null; host.Binding = true;
        try
        {
            if (buffer is not null)
            {
                buffer.PropertyChanged -= host.Listener; buffer.Dispose();
                try { host.Template.Unbind?.Invoke(host.Content, buffer); } catch (Exception error) { Report(error); }
            }
            host.Content.DataContext = null;
        }
        finally { host.Binding = false; host.Listener = null; }
    }
    private void ReleaseCellHost(NativeHost host)
    {
        UnbindCell(host);
        if (host.Container.Child is Grid body) body.Children.Clear();
        host.Container.Child = null;
        try { host.Template.Release?.Invoke(host.Content); } catch (Exception error) { Report(error); }
    }
    private void TrimUnusedPools()
    {
        foreach (var pair in _cellPools.ToArray())
            if (_displaySelectors.Count == 0 && _editSelectors.Count == 0 && !_cellTemplates.ContainsValue(pair.Key) && !_editCellTemplates.ContainsValue(pair.Key))
            {
                foreach (var host in pair.Value) { ReleaseCellHost(host); _pooledCells--; }
                _cellPools.Remove(pair.Key);
            }
    }
    private void ResetCellControls()
    {
        _syncingNativeCells = true;
        try
        {
            foreach (var pair in _cellHosts.ToArray()) RecycleCell(pair.Key, pair.Value, retain: false);
            foreach (var pool in _cellPools.Values) foreach (var host in pool) ReleaseCellHost(host);
            _cellPools.Clear(); _pooledCells = 0; _nativeEditCell = null; _nativeFrame = null; Renderer.IsNativeCell = null;
            _templateChoices.Clear(); _templateTableRevision = -1;
        }
        finally { _syncingNativeCells = false; }
    }
    private bool CommitCellBuffer(GridCellEditBuffer buffer)
    {
        try
        {
            if (_cellHosts.TryGetValue(buffer.Address, out var host) && ReferenceEquals(host.Buffer, buffer))
                host.Template.Flush?.Invoke(host.Content, buffer);
        }
        catch (Exception error) { Report(error); return false; }
        if (buffer.TryCommit()) { if (buffer.IsActive) ClearError(); return true; }
        Report(new InvalidOperationException(buffer.Error ?? "Unable to commit the native cell.")); return false;
    }
    private bool CommitOtherCellControls(GridCellEditBuffer? except = null)
    {
        if (_committingNativeCells) return true;
        _committingNativeCells = true;
        try
        {
            foreach (var host in _cellHosts.Values.ToArray())
                if (host.Buffer is { IsActive: true } buffer && !ReferenceEquals(buffer, except))
                {
                    if (!CommitCellBuffer(buffer)) { FocusCell(host); return false; }
                }
            if (except is null && _nativeEditCell is not null) { _nativeEditCell = null; _nativeFrame = null; _nativeSyncPending = true; }
            return true;
        }
        finally
        {
            _committingNativeCells = false;
            if (_nativeSyncPending) { _nativeSyncPending = false; _nativeFrame = null; SynchronizeCellControls(); }
        }
    }
    public void CancelCellEdits()
    {
        foreach (var host in _cellHosts.Values.ToArray()) { host.Buffer?.Cancel(); RefreshCellHost(host); }
        _nativeEditCell = null; _nativeFrame = null; if (_cellHosts.Count > 0) SynchronizeCellControls(); ClearError();
    }
}

using DataGridScene.Core;
using DataGridScene.Layout;
using DataGridScene.Rendering.Skia;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace DataGridScene.Controls;

/// <summary>Reusable Uno grid with a viewport-only Skia body and native in-place editors.</summary>
public sealed partial class GridControl : UserControl, IDisposable
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(nameof(Table), typeof(GridTable), typeof(GridControl),
        new PropertyMetadata(null, (owner, args) => ((GridControl)owner).Attach((GridTable?)args.NewValue)));
    public GridTable? Table
    {
        get => (GridTable?)GetValue(TableProperty);
        set
        {
            if (_dataUpdateDepth > 0) throw new InvalidOperationException("Finish batched updates before replacing the table.");
            SetValue(TableProperty, value);
        }
    }
    private readonly Grid _root = new();
    private readonly GridSurface _surface;
    private readonly Canvas _overlay = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, Width = 14, IsTabStop = false };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, Height = 14, IsTabStop = false };
    private readonly Dictionary<string, double> _rowHeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _filterTexts = new(StringComparer.OrdinalIgnoreCase);
    private GridFilter? _criteria;
    private GridMetrics _metrics = new();
    private bool _updatingScroll;
    private bool _disposed;
    private bool _dark;
    private double _offsetX, _offsetY;
    public GridView? View { get; private set; }
    public GridLayout? ColumnLayout { get; private set; }
    public GridSelection Selection { get; } = new();
    public EditHistory? History { get; private set; }
    public RowHeightIndex HeightIndex { get; } = new();
    public GridCanvasRenderer Renderer { get; } = new();
    public GridFrame? Frame { get; private set; }
    private readonly GridFrameCache _frameCache = new();
    public long FrameBuildCount => _frameCache.BuildCount;
    public long FrameReuseCount => _frameCache.ReuseCount;
    public GridMetrics Metrics => _metrics;
    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (_isReadOnly == value) return; _isReadOnly = value; Redraw(); Changed(); }
    }
    public string? LastError { get; private set; }
    public double HorizontalOffset => _offsetX;
    public double VerticalOffset => _offsetY;
    public IReadOnlyDictionary<string, string> FilterTexts => _filterTexts;
    public event EventHandler? StateChanged;
    public event EventHandler? SearchRequested;
    public event EventHandler<string>? Error;
    public bool ShowGroupPanel { get => _metrics.ShowGroupPanel; set { _metrics = _metrics with { ShowGroupPanel = value }; UpdateGeometry(); Changed(); } }
    public bool ShowFilterRow { get => _metrics.ShowFilterRow; set { _metrics = _metrics with { ShowFilterRow = value }; UpdateGeometry(); Changed(); } }
    public bool ShowBands { get => _metrics.ShowBands; set { _metrics = _metrics with { ShowBands = value }; UpdateGeometry(); Changed(); } }
    public double RowHeight
    {
        get => _metrics.RowHeight;
        set { if (!double.IsFinite(value) || value < 18 || value > 256) throw new ArgumentOutOfRangeException(nameof(value)); _metrics = _metrics with { RowHeight = value }; RebuildRows(); }
    }
    public bool DarkTheme
    {
        get => _dark;
        set { _dark = value; Renderer.Palette = value ? GridPalette.Dark : GridPalette.Light; RequestedTheme = value ? ElementTheme.Dark : ElementTheme.Light; FilterPanel.ApplyTheme(value); Redraw(); Changed(); }
    }
    public Func<GridRecord, GridDetailData>? DetailProvider { get => Renderer.DetailProvider; set { Renderer.DetailProvider = value; Redraw(); } }

    public GridControl()
    {
        IsTabStop = true; MinHeight = 160; MinWidth = 160;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, "Data grid");
        AutomationProperties.SetHelpText(this, "Use arrow keys to navigate. F2 edits a cell. Shift selects a range. Control C copies. F4 expands details.");
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = new GridLength(14) });
        _root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new() { Width = new GridLength(14) });
        _surface = new GridSurface(this);
        _root.Children.Add(_surface); _root.Children.Add(_overlay);
        Grid.SetColumn(_vertical, 1); _root.Children.Add(_vertical);
        Grid.SetRow(_horizontal, 1); _root.Children.Add(_horizontal);
        InitializeFilterPanel(); InitializeCellControls();
        Content = _root;
        _surface.SizeChanged += (_, _) => UpdateGeometry();
        InitializeScrolling();
        Selection.Changed += SelectionChanged;
        KeyDown += GridKeyDown;
        WirePointerInput();
        Loaded += (_, _) => UpdateGeometry();
    }
    private sealed class GridSurface(GridControl owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            if (owner._disposed || owner.View is null || owner.ColumnLayout is null) return;
            owner.Frame = owner._frameCache.Get(owner.View, owner.ColumnLayout, owner.Selection, owner.HeightIndex, owner._metrics, area.Width, area.Height, owner._offsetX, owner._offsetY);
            owner.Renderer.Draw(canvas, owner.Frame);
        }
    }
    private void Attach(GridTable? table)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GridControl));
        ResetFilterHistory(table);
        CancelEdit();
        CloseValueFilter(); ResetCellControls();
        ResetPointerState();
        if (View is not null) { View.Changed -= ViewChanged; View.Dispose(); }
        if (ColumnLayout is not null) ColumnLayout.Changed -= LayoutChanged;
        if (History is not null) History.Changed -= HistoryChanged;
        View = null; ColumnLayout = null; History = null; Selection.Clear();
        _filterTexts.Clear(); _valueFilters.Clear(); _criteria = null; _rowHeights.Clear(); _offsetX = _offsetY = 0;
        if (table is not null)
        {
            View = new(table); ColumnLayout = new(table.Schema); History = new(table);
            View.Changed += ViewChanged; ColumnLayout.Changed += LayoutChanged; History.Changed += HistoryChanged;
        }
        RefreshFilterDisplay(); RebuildRows();
    }
    private void ViewChanged(object? sender, EventArgs args)
    {
        if (View?.LastChangeKind == GridViewChangeKind.Values)
        {
            // Records retain their identities and positions; only painting and the focused
            // accessibility description change. Do not rebuild an N-row height index.
            SelectionChanged(sender, args); return;
        }
        if (Table is not null) Selection.Prune(Table);
        RebuildRows();
    }
    private void LayoutChanged(object? sender, EventArgs args) => UpdateGeometry();
    private void HistoryChanged(object? sender, EventArgs args) => Changed();
    private void SelectionChanged(object? sender, EventArgs args)
    {
        if (Table is not null && Selection.FocusedCell is { } cell && Table.Contains(cell.RecordId))
        {
            var column = Table.Schema[cell.Field];
            AutomationProperties.SetName(this, $"Data grid. Row {cell.RecordId}, {column.Caption}: {column.Display(column.GetValue(Table[cell.RecordId]), Table.Schema.Culture)}. {Selection.Count} selected.");
        }
        Redraw(); Changed();
    }
    public long RowGeometryBuildCount { get; private set; }
    private double EffectiveRowHeight(GridRowKind kind, double? custom = null)
    {
        double minimum = kind == GridRowKind.Detail ? 128 : SummaryGeometry.RowHeight(View!.Query, kind, RowHeight);
        return kind is GridRowKind.Group or GridRowKind.GroupFooter ? Math.Max(custom ?? minimum, minimum) : custom ?? minimum;
    }
    private void RebuildRows()
    {
        if (_dataUpdateDepth > 0) { _rowGeometryPending = true; return; }
        RowGeometryBuildCount++;
        HeightIndex.Reset(View?.Rows.Count ?? 0, _metrics.RowHeight);
        if (View is not null && (View.HasNonDataRows || _rowHeights.Count > 0))
        {
            // Resolve summary line counts once per kind, not once per group row.
            double groupHeight = EffectiveRowHeight(GridRowKind.Group);
            double footerHeight = EffectiveRowHeight(GridRowKind.GroupFooter);
            if (View.HasNonDataRows)
                for (int i = 0; i < View.Rows.Count; i++)
                {
                    var row = View.Rows[i];
                    double minimum = row.Kind == GridRowKind.Detail ? 128 : row.Kind == GridRowKind.Group ? groupHeight : row.Kind == GridRowKind.GroupFooter ? footerHeight : RowHeight;
                    double height = _rowHeights.GetValueOrDefault(row.Key, minimum);
                    if (row.Group is not null) height = Math.Max(height, minimum);
                    if (height != RowHeight) HeightIndex.SetHeight(i, height);
                }
            else
                foreach (var (key, height) in _rowHeights)
                {
                    int index = View.IndexOfRow(key);
                    if (index >= 0) HeightIndex.SetHeight(index, height);
                }
        }
        UpdateGeometry(); Changed();
    }
    public void SetRowHeight(string rowKey, double height)
    {
        ArgumentException.ThrowIfNullOrEmpty(rowKey);
        if (!double.IsFinite(height) || height < 18 || height > 4096) throw new ArgumentOutOfRangeException(nameof(height));
        _rowHeights[rowKey] = height;
        UpdateRowHeight(rowKey);
    }
    public bool ClearRowHeight(string rowKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(rowKey);
        if (!_rowHeights.Remove(rowKey)) return false;
        UpdateRowHeight(rowKey); return true;
    }
    private void UpdateRowHeight(string key)
    {
        if (_dataUpdateDepth > 0) { _rowGeometryPending = true; return; }
        int index = View?.IndexOfRow(key) ?? -1;
        if (index >= 0) HeightIndex.SetHeight(index, EffectiveRowHeight(View!.Rows[index].Kind, _rowHeights.TryGetValue(key, out var custom) ? custom : null));
        UpdateGeometry(); Changed();
    }
    private void UpdateGeometry()
    {
        if (_dataUpdateDepth > 0) { _geometryPending = true; return; }
        if (_disposed) return;
        if (View is null || ColumnLayout is null) { Frame = null; _offsetX = _offsetY = 0; _frameCache.Clear(); UpdateScrollBars(); Redraw(); return; }
        Frame = _frameCache.Get(View, ColumnLayout, Selection, HeightIndex, _metrics, _surface.ActualWidth, _surface.ActualHeight, _offsetX, _offsetY);
        _offsetX = Frame.Columns.Offset; _offsetY = Frame.OffsetY;
        UpdateScrollBars();
        PositionEditor(); Redraw();
    }
    public void Redraw()
    {
        if (_disposed) return;
        if (_dataUpdateDepth > 0) { _redrawPending = true; return; }
        SynchronizeCellControls(); _surface.Invalidate();
    }
    private void Changed()
    {
        if (_dataUpdateDepth > 0) { _statePending = true; return; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ScrollTo(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!CommitEdit()) { RestoreScrollBarValues(); return; }
        _offsetX = Math.Max(0, x); _offsetY = Math.Max(0, y); UpdateGeometry(); Changed();
    }
    public void EnsureVisible(CellAddress cell)
    {
        if (View is null || Frame is null || ColumnLayout is null) return;
        int row = View.IndexOfRecord(cell.RecordId);
        if (row < 0) return;
        double top = HeightIndex.OffsetOf(row), bottom = top + HeightIndex[row];
        if (top < _offsetY) _offsetY = top;
        else if (bottom > _offsetY + Frame.BodyHeight) _offsetY = bottom - Frame.BodyHeight;
        var columns = ColumnLayout.DisplayColumns();
        var target = columns.FirstOrDefault(column => column.Field.Equals(cell.Field, StringComparison.OrdinalIgnoreCase));
        if (target is not null && target.Pin == ColumnPin.None)
        {
            double left = 0;
            foreach (var column in columns.Where(column => column.Pin == ColumnPin.None))
            {
                if (column.Field.Equals(cell.Field, StringComparison.OrdinalIgnoreCase)) break;
                left += column.Width;
            }
            double available = Math.Max(0, Frame.Columns.MiddleRight - Frame.Columns.MiddleLeft);
            if (left < _offsetX) _offsetX = left;
            else if (left + target.Width > _offsetX + available) _offsetX = left + target.Width - available;
        }
        UpdateGeometry();
    }
    public Point SurfaceOrigin()
    {
        if (XamlRoot?.Content is UIElement root) return _surface.TransformToVisual(root).TransformPoint(new Point(0, 0));
        return new(0, 0);
    }
    public void SetQuery(GridQuery query)
    {
        RequireView(); if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        View!.SetQuery(query); _criteria = View.RequestedQuery.Filter; _filterTexts.Clear(); _valueFilters.Clear(); RefreshFilterDisplay();
        _offsetY = 0; UpdateGeometry(); ClearError();
    }
    public void SetSearch(string search)
    {
        RequireView(); ArgumentNullException.ThrowIfNull(search); if (!CommitEdit()) return;
        if (View!.RequestedQuery.Search == search) return;
        View.SetQuery(View.RequestedQuery with { Search = search }); _offsetY = 0; UpdateGeometry(); ClearError();
    }
    public void SetCriteria(string expression)
    {
        RequireView(); if (!CommitEdit()) return;
        var filter = CriteriaParser.Parse(expression); ApplyFilter(filter, _filterTexts); _criteria = filter; RefreshFilterDisplay(); ClearError(); Changed();
    }
    public void SetColumnFilter(string field, string text)
    {
        RequireView(); if (!_committing && !CommitEdit()) return;
        field = Table!.Schema[field].Field;
        var values = new Dictionary<string, ColumnValueFilter>(_valueFilters, StringComparer.OrdinalIgnoreCase);
        values.Remove(field);
        var filters = new Dictionary<string, string>(_filterTexts, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) filters.Remove(field); else filters[field] = text.Trim();
        ApplyFilter(_criteria, filters, values);
        _valueFilters.Clear(); foreach (var pair in values) _valueFilters.Add(pair.Key, pair.Value);
        _filterTexts.Clear(); foreach (var pair in filters) _filterTexts.Add(pair.Key, pair.Value);
        RefreshFilterDisplay(); ClearError(); Redraw(); Changed();
    }
    private void ApplyFilter(GridFilter? criteria, IReadOnlyDictionary<string, string> texts, IReadOnlyDictionary<string, ColumnValueFilter>? values = null)
    {
        var terms = new List<GridFilter>();
        if (criteria is not null) terms.Add(criteria);
        foreach (var pair in texts) if (CriteriaParser.AutoFilter(pair.Key, pair.Value) is { } filter) terms.Add(filter);
        foreach (var (field, selection) in values ?? _valueFilters) if (selection.ToFilter(field) is { } filter) terms.Add(filter);
        var effective = terms.Count == 0 ? null : GridFilter.And(terms.ToArray());
        if (GridFilterState.FiltersEqual(effective, View!.RequestedQuery.Filter)) return;
        View.SetQuery(View.RequestedQuery with { Filter = effective });
        _offsetY = 0; UpdateGeometry();
    }
    public void ClearFilters()
    {
        RequireView(); if (!CommitEdit()) return;
        View!.SetQuery(View.RequestedQuery with { Filter = null, Search = "", IsFilterEnabled = true }); _criteria = null; _filterTexts.Clear(); _valueFilters.Clear(); RefreshFilterDisplay(); _offsetY = 0; ClearError(); UpdateGeometry();
    }
    public void SortColumn(string field, SortDirection? direction, bool append = false)
    {
        RequireView(); if (!CommitEdit()) return;
        var sorts = append ? View!.RequestedQuery.Sorts.Where(sort => !sort.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        if (direction is not null) sorts.Add(new(field, direction.Value));
        View!.SetQuery(View.RequestedQuery with { Sorts = sorts }); ClearError();
    }
    private void CycleSort(string field, bool append)
    {
        var existing = View!.RequestedQuery.Sorts.FirstOrDefault(sort => sort.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        SortColumn(field, existing is null ? SortDirection.Ascending : existing.Direction == SortDirection.Ascending ? SortDirection.Descending : null, append);
    }
    public void GroupColumn(string field, bool grouped = true)
    {
        RequireView(); if (!CommitEdit()) return;
        var groups = View!.RequestedQuery.Groups.Where(group => !group.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList();
        if (grouped) groups.Add(new(field));
        View.SetQuery(View.RequestedQuery with { Groups = groups, GroupSummarySorts = View.RequestedQuery.GroupSummarySorts.Where(sort => groups.Any(group => group.Field.Equals(sort.Field, StringComparison.OrdinalIgnoreCase))).ToArray() }); _offsetY = 0; UpdateGeometry();
    }
    public void BestFit(string? field = null)
    {
        RequireView(); if (!CommitEdit()) return;
        if (field is not null) _ = Table!.Schema[field];
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in Table!.Schema.Columns.Where(column => field is null || column.Field.Equals(field, StringComparison.OrdinalIgnoreCase)))
        {
            float width = Renderer.MeasureText(column.Caption, true) + 48;
            // An explicit best-fit command measures all filtered records, never during scrolling.
            foreach (var record in View!.FilteredRecords)
            {
                if (column.Presentation == ColumnPresentation.Sparkline) { width = Math.Max(width, 140); break; }
                width = Math.Max(width, Renderer.MeasureText(column.Display(column.GetValue(record), Table.Schema.Culture)) + 24);
            }
            widths[column.Field] = Math.Clamp(width, 50, 640);
        }
        // Publish once, after every getter/measurement succeeds. A rejected best-fit must
        // not leave half the columns resized or allocate a frame for every column.
        ColumnLayout!.RestoreColumns(ColumnLayout.Columns.Select(column => widths.TryGetValue(column.Field, out double width) ? column with { Width = width } : column));
    }
    public string SaveLayout(bool includeSelection = true)
    {
        RequireView();
        if (_dataUpdateDepth > 0) throw new InvalidOperationException("Finish data updates before saving a workspace.");
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        return LayoutSerializer.Save(new GridLayoutDocument
        {
            Columns = ColumnLayout!.Columns, Query = View!.RequestedQuery with { Filter = _criteria },
            FilterTexts = new Dictionary<string, string>(_filterTexts, StringComparer.OrdinalIgnoreCase),
            ValueFilters = new Dictionary<string, ColumnValueFilter>(_valueFilters, StringComparer.OrdinalIgnoreCase),
            RowHeight = RowHeight, ShowFilterRow = ShowFilterRow, ShowGroupPanel = ShowGroupPanel,
            ShowBands = ShowBands, Theme = DarkTheme ? "Dark" : "Light", SelectionMode = SelectionMode,
            HorizontalOffset = _offsetX, VerticalOffset = _offsetY,
            RowHeights = new Dictionary<string, double>(_rowHeights, StringComparer.Ordinal),
            ViewState = View.CaptureState(),
            Selection = includeSelection ? Selection.CaptureState() : null
        });
    }
    public void RestoreLayout(string json)
    {
        RequireView();
        if (!CommitEdit()) throw new InvalidOperationException(LastError ?? "Finish editing first.");
        if (_dataUpdateDepth > 0) throw new InvalidOperationException("Finish data updates before restoring a workspace.");
        var document = LayoutSerializer.Load(json, Table!.Schema);
        var nextLayout = new GridLayout(Table.Schema);
        nextLayout.RestoreColumns(document.Columns);
        // Stage data shaping before replacing any live control state. For example,
        // a computed column may reject a sort only when actual records are present.
        var nextView = new GridView(Table, document.EffectiveQuery());
        try
        {
            nextView.RestoreState(document.ViewState);
            if (document.Selection is not null) new GridSelection().RestoreState(nextView, document.Selection);
        }
        catch { nextView.Dispose(); throw; }
        ResetCellControls();
        var previousView = View!;
        previousView.Changed -= ViewChanged;
        ColumnLayout!.Changed -= LayoutChanged;
        View = nextView; ColumnLayout = nextLayout;
        View.Changed += ViewChanged; ColumnLayout.Changed += LayoutChanged;
        previousView.Dispose();
        ResetPointerState();
        _criteria = document.Query.Filter;
        _filterTexts.Clear();
        foreach (var pair in document.FilterTexts) _filterTexts.Add(pair.Key, pair.Value);
        _valueFilters.Clear(); foreach (var pair in document.ValueFilters) _valueFilters.Add(pair.Key, pair.Value);
        CloseValueFilter();
        RefreshFilterDisplay();
        _rowHeights.Clear();
        foreach (var pair in document.RowHeights) _rowHeights.Add(pair.Key, pair.Value);
        _metrics = _metrics with
        {
            RowHeight = document.RowHeight, ShowFilterRow = document.ShowFilterRow,
            ShowGroupPanel = document.ShowGroupPanel, ShowBands = document.ShowBands
        };
        _offsetX = document.HorizontalOffset; _offsetY = document.VerticalOffset;
        SelectionMode = document.SelectionMode;
        if (document.Selection is not null) Selection.RestoreState(View, document.Selection);
        DarkTheme = document.Theme == "Dark";
        RebuildRows(); ClearError();
    }
    private void RequireView() { ObjectDisposedException.ThrowIf(_disposed, this); if (View is null) throw new InvalidOperationException("Assign a GridTable first."); }
    private void ClearError() { if (LastError is null) return; LastError = null; Changed(); }
    private void Report(Exception exception) { LastError = exception.Message; Error?.Invoke(this, LastError); Changed(); }
    private void Safe(Action action) { try { action(); } catch (Exception exception) { Report(exception); } }
    public new void Dispose()
    {
        if (_disposed) return;
        ResetFilterHistory(null);
        CancelEdit(); CloseValueFilter(); _disposed = true; ResetCellControls();
        if (View is not null) { View.Changed -= ViewChanged; View.Dispose(); }
        if (ColumnLayout is not null) ColumnLayout.Changed -= LayoutChanged;
        if (History is not null) History.Changed -= HistoryChanged;
        Selection.Changed -= SelectionChanged; Frame = null; _frameCache.Clear(); Renderer.Dispose();
    }
}

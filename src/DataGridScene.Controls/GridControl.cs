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
    public GridTable? Table { get => (GridTable?)GetValue(TableProperty); set => SetValue(TableProperty, value); }
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
    public GridMetrics Metrics => _metrics;
    public bool IsReadOnly { get; set; }
    public string? LastError { get; private set; }
    public double HorizontalOffset => _offsetX;
    public double VerticalOffset => _offsetY;
    public IReadOnlyDictionary<string, string> FilterTexts => _filterTexts;
    public event EventHandler? StateChanged;
    public event EventHandler? SearchRequested;
    public event EventHandler<string>? Error;
    public bool ShowGroupPanel { get => _metrics.ShowGroupPanel; set { _metrics = _metrics with { ShowGroupPanel = value }; UpdateGeometry(); } }
    public bool ShowFilterRow { get => _metrics.ShowFilterRow; set { _metrics = _metrics with { ShowFilterRow = value }; UpdateGeometry(); } }
    public bool ShowBands { get => _metrics.ShowBands; set { _metrics = _metrics with { ShowBands = value }; UpdateGeometry(); } }
    public double RowHeight
    {
        get => _metrics.RowHeight;
        set { if (!double.IsFinite(value) || value < 18 || value > 256) throw new ArgumentOutOfRangeException(nameof(value)); _metrics = _metrics with { RowHeight = value }; RebuildRows(); }
    }
    public bool DarkTheme
    {
        get => _dark;
        set { _dark = value; Renderer.Palette = value ? GridPalette.Dark : GridPalette.Light; RequestedTheme = value ? ElementTheme.Dark : ElementTheme.Light; Redraw(); }
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
        Content = _root;
        _surface.SizeChanged += (_, _) => UpdateGeometry();
        _vertical.ValueChanged += (_, args) => { if (!_updatingScroll) ScrollTo(_offsetX, args.NewValue); };
        _horizontal.ValueChanged += (_, args) => { if (!_updatingScroll) ScrollTo(args.NewValue, _offsetY); };
        Selection.Changed += SelectionChanged;
        KeyDown += GridKeyDown;
        WirePointerInput();
        Loaded += (_, _) => UpdateGeometry();
    }
    private sealed class GridSurface(GridControl owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            if (owner.View is null || owner.ColumnLayout is null) return;
            owner.Frame = new(owner.View, owner.ColumnLayout, owner.Selection, owner.HeightIndex, owner._metrics, area.Width, area.Height, owner._offsetX, owner._offsetY);
            owner.Renderer.Draw(canvas, owner.Frame);
        }
    }
    private void Attach(GridTable? table)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GridControl));
        CancelEdit();
        if (View is not null) { View.Changed -= ViewChanged; View.Dispose(); }
        if (ColumnLayout is not null) ColumnLayout.Changed -= LayoutChanged;
        if (History is not null) History.Changed -= HistoryChanged;
        View = null; ColumnLayout = null; History = null; Selection.Clear();
        _filterTexts.Clear(); _criteria = null; _rowHeights.Clear(); _offsetX = _offsetY = 0;
        if (table is not null)
        {
            View = new(table); ColumnLayout = new(table.Schema); History = new(table);
            View.Changed += ViewChanged; ColumnLayout.Changed += LayoutChanged; History.Changed += HistoryChanged;
        }
        RebuildRows();
    }
    private void ViewChanged(object? sender, EventArgs args) { if (Table is not null) Selection.Prune(Table); RebuildRows(); }
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
    private void RebuildRows()
    {
        HeightIndex.Reset(View?.Rows.Count ?? 0, _metrics.RowHeight);
        if (View is not null)
            for (int i = 0; i < View.Rows.Count; i++)
            {
                var row = View.Rows[i];
                double height = _rowHeights.TryGetValue(row.Key, out var custom) ? custom : row.Kind == GridRowKind.Detail ? 128 : row.Kind == GridRowKind.Group ? Math.Max(30, RowHeight) : RowHeight;
                if (height != RowHeight) HeightIndex.SetHeight(i, height);
            }
        UpdateGeometry(); Changed();
    }
    public void SetRowHeight(string rowKey, double height)
    {
        if (!double.IsFinite(height) || height < 18 || height > 4096) throw new ArgumentOutOfRangeException(nameof(height));
        _rowHeights[rowKey] = height; RebuildRows();
    }
    private void UpdateGeometry()
    {
        if (View is null || ColumnLayout is null) { Frame = null; Redraw(); return; }
        Frame = new(View, ColumnLayout, Selection, HeightIndex, _metrics, _surface.ActualWidth, _surface.ActualHeight, _offsetX, _offsetY);
        _offsetX = Frame.Columns.Offset; _offsetY = Frame.OffsetY;
        _updatingScroll = true;
        try
        {
            _vertical.Maximum = Math.Max(0, HeightIndex.TotalHeight - Frame.BodyHeight);
            _vertical.ViewportSize = Frame.BodyHeight; _vertical.SmallChange = RowHeight; _vertical.LargeChange = Frame.BodyHeight; _vertical.Value = _offsetY;
            _horizontal.Maximum = Frame.Columns.MaximumOffset; _horizontal.ViewportSize = Math.Max(0, Frame.Columns.MiddleRight - Frame.Columns.MiddleLeft);
            _horizontal.SmallChange = 32; _horizontal.LargeChange = _horizontal.ViewportSize; _horizontal.Value = _offsetX;
        }
        finally { _updatingScroll = false; }
        PositionEditor(); Redraw();
    }
    public void Redraw() { if (!_disposed) _surface.Invalidate(); }
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void ScrollTo(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!CommitEdit()) return;
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
        View!.SetQuery(query); _criteria = query.Filter; _filterTexts.Clear(); Renderer.FilterTexts = _filterTexts;
        _offsetY = 0; UpdateGeometry(); ClearError();
    }
    public void SetSearch(string search)
    {
        RequireView(); if (!CommitEdit()) return;
        View!.SetQuery(View.Query with { Search = search }); _offsetY = 0; UpdateGeometry(); ClearError();
    }
    public void SetCriteria(string expression)
    {
        RequireView(); if (!CommitEdit()) return;
        var filter = CriteriaParser.Parse(expression); ApplyFilter(filter, _filterTexts); _criteria = filter; ClearError();
    }
    public void SetColumnFilter(string field, string text)
    {
        RequireView(); if (!_committing && !CommitEdit()) return;
        var filters = new Dictionary<string, string>(_filterTexts, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) filters.Remove(field); else filters[field] = text.Trim();
        ApplyFilter(_criteria, filters);
        _filterTexts.Clear(); foreach (var pair in filters) _filterTexts.Add(pair.Key, pair.Value);
        Renderer.FilterTexts = _filterTexts; ClearError(); Redraw();
    }
    private void ApplyFilter(GridFilter? criteria, IReadOnlyDictionary<string, string> texts)
    {
        var terms = new List<GridFilter>();
        if (criteria is not null) terms.Add(criteria);
        foreach (var pair in texts) if (CriteriaParser.AutoFilter(pair.Key, pair.Value) is { } filter) terms.Add(filter);
        View!.SetQuery(View.Query with { Filter = terms.Count == 0 ? null : GridFilter.And(terms.ToArray()) });
        _offsetY = 0; UpdateGeometry();
    }
    public void ClearFilters()
    {
        RequireView(); if (!CommitEdit()) return;
        View!.SetQuery(View.Query with { Filter = null, Search = "" }); _criteria = null; _filterTexts.Clear(); Renderer.FilterTexts = _filterTexts; _offsetY = 0; ClearError(); UpdateGeometry();
    }
    public void SortColumn(string field, SortDirection? direction, bool append = false)
    {
        RequireView(); if (!CommitEdit()) return;
        var sorts = append ? View!.Query.Sorts.Where(sort => !sort.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        if (direction is not null) sorts.Add(new(field, direction.Value));
        View!.SetQuery(View.Query with { Sorts = sorts }); ClearError();
    }
    private void CycleSort(string field, bool append)
    {
        var existing = View!.Query.Sorts.FirstOrDefault(sort => sort.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        SortColumn(field, existing is null ? SortDirection.Ascending : existing.Direction == SortDirection.Ascending ? SortDirection.Descending : null, append);
    }
    public void GroupColumn(string field, bool grouped = true)
    {
        RequireView(); if (!CommitEdit()) return;
        var groups = View!.Query.Groups.Where(group => !group.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList();
        if (grouped) groups.Add(new(field));
        View.SetQuery(View.Query with { Groups = groups }); _offsetY = 0; UpdateGeometry();
    }
    public void BestFit(string? field = null)
    {
        RequireView(); if (!CommitEdit()) return;
        foreach (var column in Table!.Schema.Columns.Where(column => field is null || column.Field.Equals(field, StringComparison.OrdinalIgnoreCase)))
        {
            float width = Renderer.MeasureText(column.Caption, true) + 48;
            // An explicit best-fit command measures all filtered records, never during scrolling.
            foreach (var record in View!.FilteredRecords)
            {
                if (column.Presentation == ColumnPresentation.Sparkline) { width = Math.Max(width, 140); break; }
                width = Math.Max(width, Renderer.MeasureText(column.Display(column.GetValue(record), Table.Schema.Culture)) + 24);
            }
            ColumnLayout!.SetWidth(column.Field, Math.Clamp(width, 50, 640));
        }
    }
    public string SaveLayout() => LayoutSerializer.Save(new GridLayoutDocument
    {
        Columns = ColumnLayout?.Columns ?? Array.Empty<ColumnState>(), Query = View?.Query ?? new(),
        RowHeight = RowHeight, ShowFilterRow = ShowFilterRow, ShowGroupPanel = ShowGroupPanel, Theme = DarkTheme ? "Dark" : "Light"
    });
    public void RestoreLayout(string json)
    {
        RequireView(); if (!CommitEdit()) return;
        var document = LayoutSerializer.Load(json, Table!.Schema);
        // Both operations were validated against this schema by LayoutSerializer before any mutation.
        View!.SetQuery(document.Query); ColumnLayout!.RestoreColumns(document.Columns);
        _criteria = document.Query.Filter; _filterTexts.Clear(); Renderer.FilterTexts = _filterTexts;
        _metrics = _metrics with { RowHeight = document.RowHeight, ShowFilterRow = document.ShowFilterRow, ShowGroupPanel = document.ShowGroupPanel };
        DarkTheme = document.Theme == "Dark"; RebuildRows();
    }
    private void RequireView() { ObjectDisposedException.ThrowIf(_disposed, this); if (View is null) throw new InvalidOperationException("Assign a GridTable first."); }
    private void ClearError() { if (LastError is null) return; LastError = null; Changed(); }
    private void Report(Exception exception) { LastError = exception.Message; Error?.Invoke(this, LastError); Changed(); }
    private void Safe(Action action) { try { action(); } catch (Exception exception) { Report(exception); } }
    public void Dispose()
    {
        if (_disposed) return;
        CancelEdit(); _disposed = true;
        if (View is not null) { View.Changed -= ViewChanged; View.Dispose(); }
        if (ColumnLayout is not null) ColumnLayout.Changed -= LayoutChanged;
        if (History is not null) History.Changed -= HistoryChanged;
        Selection.Changed -= SelectionChanged; Renderer.Dispose();
    }
}

namespace DataGridScene.Layout;

public readonly record struct VisibleRange(int Start, int Count)
{
    public int EndExclusive => Start + Count;
}
public enum RowHeightStorageMode { Uniform, Sparse, Dense }

/// <summary>Uniform geometry uses O(1) storage and lookup. Overrides use sparse Fenwick deltas,
/// switching to dense deltas at high occupancy. Variable-height lookup/updates remain O(log n).</summary>
public sealed class RowHeightIndex
{
    private int _count, _overrides;
    private double _defaultHeight;
    private Dictionary<int, double>? _sparseHeights, _sparseTree;
    private double[]? _denseHeights, _denseTree;
    public int Count => _count;
    public long Revision { get; private set; }
    public long ResetCount { get; private set; }
    public int OverrideCount => _overrides;
    public RowHeightStorageMode StorageMode => _denseTree is not null ? RowHeightStorageMode.Dense :
        _sparseTree is not null ? RowHeightStorageMode.Sparse : RowHeightStorageMode.Uniform;
    /// <summary>Diagnostic count of allocated entries, not byte usage.</summary>
    public long StorageEntryCount => _denseTree is not null ? (long)_denseTree.Length + _denseHeights!.Length :
        (_sparseHeights?.Count ?? 0) + (_sparseTree?.Count ?? 0);
    public double TotalHeight => OffsetOf(Count);
    public double this[int index]
    {
        get
        {
            CheckIndex(index);
            return _denseHeights is not null ? _denseHeights[index] : _sparseHeights?.GetValueOrDefault(index, _defaultHeight) ?? _defaultHeight;
        }
    }
    public RowHeightIndex(int count = 0, double defaultHeight = 28) => Reset(count, defaultHeight);
    public void Reset(int count, double defaultHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count); CheckHeight(defaultHeight);
        _count = count; _defaultHeight = defaultHeight; _overrides = 0;
        _sparseHeights = _sparseTree = null; _denseHeights = _denseTree = null;
        Revision++; ResetCount++;
    }
    private static void CheckHeight(double height)
    { if (!double.IsFinite(height) || height <= 0 || height > 1_000_000) throw new ArgumentOutOfRangeException(nameof(height)); }
    private void CheckIndex(int index)
    { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); }
    public void SetHeight(int index, double height)
    {
        CheckIndex(index); CheckHeight(height);
        double previous = this[index];
        double current = height;
        if (previous == current) return;
        if (previous == _defaultHeight) _overrides++;
        if (current == _defaultHeight) _overrides--;
        if (_overrides == 0)
        {
            _sparseHeights = _sparseTree = null; _denseHeights = _denseTree = null;
            Revision++; return;
        }
        if (_denseHeights is not null) _denseHeights[index] = current;
        else
        {
            _sparseHeights ??= []; _sparseTree ??= [];
            if (current == _defaultHeight) _sparseHeights.Remove(index); else _sparseHeights[index] = current;
        }
        double delta = current - previous;
        for (int i = index + 1; i <= Count;)
        {
            if (_denseTree is not null) _denseTree[i] += delta;
            else
            {
                double value = _sparseTree!.GetValueOrDefault(i) + delta;
                if (value == 0) _sparseTree.Remove(i); else _sparseTree[i] = value;
            }
            int step = i & -i;
            if (i > Count - step) break; // No signed overflow for very large uniform indexes.
            i += step;
        }
        if (_denseTree is null && _overrides >= Math.Max(32, Count / 16)) MakeDense();
        Revision++;
    }
    private void MakeDense()
    {
        var heights = new double[Count]; Array.Fill(heights, _defaultHeight); var tree = new double[checked(Count + 1)];
        foreach (var (index, value) in _sparseHeights!) heights[index] = value;
        foreach (var (index, value) in _sparseTree!) tree[index] = value;
        _denseHeights = heights; _denseTree = tree;
        _sparseHeights = _sparseTree = null;
    }
    private double DeltaAt(int index) => _denseTree is not null ? _denseTree[index] : _sparseTree?.GetValueOrDefault(index) ?? 0;
    public double OffsetOf(int index)
    {
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        double sum = _defaultHeight * index;
        if (_overrides == 0) return sum;
        double delta = 0;
        for (int i = index; i > 0; i -= i & -i) delta += DeltaAt(i);
        return sum + delta;
    }
    public int RowAt(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (Count == 0) return -1;
        if (offset <= 0) return 0;
        if (offset >= TotalHeight) return Count - 1;
        if (_overrides == 0)
        {
            int row = (int)Math.Min(Count - 1, Math.Floor(offset / _defaultHeight));
            // Division may round to an adjacent integer for fractional row heights.
            if (row + 1 < Count && (row + 1) * _defaultHeight <= offset) row++;
            if (row * _defaultHeight > offset) row--;
            return Math.Max(0, row);
        }
        int index = 0, bit = 1; double delta = 0;
        while (bit <= Count / 2) bit <<= 1;
        for (; bit != 0; bit >>= 1)
        {
            int candidate = index + bit;
            if (candidate <= Count)
            {
                double nextDelta = delta + DeltaAt(candidate);
                if (candidate * _defaultHeight + nextDelta <= offset) { index = candidate; delta = nextDelta; }
            }
        }
        return Math.Min(index, Count - 1);
    }
    public VisibleRange Visible(double offset, double viewportHeight, int overscan = 2)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(viewportHeight) || overscan < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (Count == 0 || viewportHeight <= 0 || offset >= TotalHeight) return new(0, 0);
        offset = Math.Max(0, offset);
        int first = (int)Math.Max(0L, (long)RowAt(offset) - overscan);
        int last = (int)Math.Min(Count - 1L, (long)RowAt(Math.BitDecrement(offset + viewportHeight)) + overscan);
        return new(first, last - first + 1);
    }
}

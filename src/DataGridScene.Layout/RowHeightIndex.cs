namespace DataGridScene.Layout;

public readonly record struct VisibleRange(int Start, int Count)
{
    public int EndExclusive => Start + Count;
}

/// <summary>Fenwick-tree geometry: O(log n) height changes, prefix sums and inverse offset lookup.</summary>
public sealed class RowHeightIndex
{
    private double[] _heights = [];
    private double[] _tree = [0];
    public int Count => _heights.Length;
    public double TotalHeight => OffsetOf(Count);
    public double this[int index] => _heights[index];
    public RowHeightIndex(int count = 0, double defaultHeight = 28) => Reset(count, defaultHeight);
    public void Reset(int count, double defaultHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        CheckHeight(defaultHeight);
        _heights = new double[count]; Array.Fill(_heights, defaultHeight);
        _tree = new double[count + 1];
        for (int i = 1; i <= count; i++) _tree[i] = defaultHeight * (i & -i);
    }
    private static void CheckHeight(double height)
    { if (!double.IsFinite(height) || height <= 0 || height > 1_000_000) throw new ArgumentOutOfRangeException(nameof(height)); }
    public void SetHeight(int index, double height)
    {
        CheckHeight(height);
        if ((uint)index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        double delta = height - _heights[index]; _heights[index] = height;
        for (int i = index + 1; i < _tree.Length; i += i & -i) _tree[i] += delta;
    }
    public double OffsetOf(int index)
    {
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        double sum = 0;
        for (int i = index; i > 0; i -= i & -i) sum += _tree[i];
        return sum;
    }
    public int RowAt(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (Count == 0) return -1;
        if (offset <= 0) return 0;
        if (offset >= TotalHeight) return Count - 1;
        int index = 0; double sum = 0; int bit = 1;
        while (bit <= Count / 2) bit <<= 1;
        for (; bit != 0; bit >>= 1)
        {
            int candidate = index + bit;
            if (candidate <= Count && sum + _tree[candidate] <= offset)
            { index = candidate; sum += _tree[candidate]; }
        }
        return Math.Min(index, Count - 1);
    }
    public VisibleRange Visible(double offset, double viewportHeight, int overscan = 2)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(viewportHeight) || overscan < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (Count == 0 || viewportHeight <= 0 || offset >= TotalHeight) return new(0, 0);
        offset = Math.Max(0, offset);
        int first = Math.Max(0, RowAt(offset) - overscan);
        int last = Math.Min(Count - 1, RowAt(Math.BitDecrement(offset + viewportHeight)) + overscan);
        return new(first, last - first + 1);
    }
}

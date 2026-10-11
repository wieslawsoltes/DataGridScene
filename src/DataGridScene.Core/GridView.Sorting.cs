namespace DataGridScene.Core;

public sealed partial class GridView
{
    private void SortRows(List<GridRecord> rows, GridQuery query)
    {
        if (query.Sorts.Count == 0 || rows.Count < 2) return;
        var sorts = query.Sorts.Select(sort => (Column: Table.Schema[sort.Field], sort.Direction)).ToArray();
        var values = new object?[sorts.Length][];
        // Bound cached references independently of query width. Stored fields already
        // have O(1) access; only computed keys need materialization. Getter delegates
        // must return deterministic values during a shaping pass, just as sorting
        // requires a stable comparator. Uncached columns retain direct evaluation.
        int remaining = 1_000_000;
        for (int column = 0; column < sorts.Length; column++)
        {
            if (sorts[column].Column.ValueGetter is null || rows.Count > remaining) continue;
            var keys = new object?[rows.Count];
            for (int row = 0; row < rows.Count; row++) keys[row] = sorts[column].Column.GetValue(rows[row]);
            values[column] = keys; remaining -= rows.Count;
        }
        var order = new int[rows.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            for (int i = 0; i < sorts.Length; i++)
            {
                var (column, direction) = sorts[i];
                object? left = values[i] is { } keys ? keys[a] : column.GetValue(rows[a]);
                object? right = values[i] is { } otherKeys ? otherKeys[b] : column.GetValue(rows[b]);
                int comparison = column.Comparer?.Compare(left, right) ?? GridValue.Compare(left, right);
                if (comparison != 0) return direction == SortDirection.Ascending ? Math.Sign(comparison) : -Math.Sign(comparison);
            }
            // Input ordinals provide stable ties without an ID-to-ordinal dictionary.
            return a.CompareTo(b);
        });
        var sorted = new GridRecord[rows.Count];
        for (int i = 0; i < sorted.Length; i++) sorted[i] = rows[order[i]];
        rows.Clear(); rows.AddRange(sorted);
    }
}

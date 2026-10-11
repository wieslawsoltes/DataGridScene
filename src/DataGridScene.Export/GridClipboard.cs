using DataGridScene.Core;
using System.Globalization;
using System.Text;

namespace DataGridScene.Export;

/// <summary>Clipboard semantics shared by UI hosts; respects display order, hidden columns and sparse cell selections.</summary>
public static class GridClipboard
{
    public static string Copy(GridView view, GridSelection selection, IReadOnlyList<string> fields,
        bool includeHeaders = false, bool useDisplayFormatting = false, long maximumCells = 2_000_000)
    {
        if (maximumCells < 1) throw new ArgumentOutOfRangeException(nameof(maximumCells));
        var columns = Resolve(view, fields);
        // Locate only selected identities. Copying one row must not materialize an
        // array for the entire data set just to discard all unselected records.
        int[] indexes;
        if (selection.Mode == GridSelectionMode.Cells)
        {
            var columnIndexes = columns.Select((column, index) => (column.Field, index))
                .ToDictionary(pair => pair.Field, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
            foreach (var cell in selection.EnumerateCells())
            {
                if (!columnIndexes.TryGetValue(cell.Field, out int column)) continue;
                int row = view.IndexOfRecord(cell.RecordId);
                if (row < 0) continue;
                top = Math.Min(top, row); bottom = Math.Max(bottom, row);
                left = Math.Min(left, column); right = Math.Max(right, column);
            }
            if (bottom < 0) return string.Empty;
            columns = columns[left..(right + 1)];
            var rectangle = new List<int>();
            for (int i = top; i <= bottom; i++)
                if (view.Rows[i].Kind == GridRowKind.Data)
                {
                    if (((long)rectangle.Count + 1 + (includeHeaders ? 1 : 0)) * columns.Length > maximumCells)
                        throw new InvalidOperationException($"The clipboard operation exceeds {maximumCells:N0} cells. Use an export for larger selections.");
                    rectangle.Add(i);
                }
            indexes = rectangle.ToArray();
        }
        else indexes = selection.EnumerateSelectedIds().Select(view.IndexOfRecord).Where(index => index >= 0).Order().ToArray();
        if (((long)indexes.Length + (includeHeaders ? 1 : 0)) * columns.Length > maximumCells)
            throw new InvalidOperationException($"The clipboard operation exceeds {maximumCells:N0} cells. Use an export for larger selections.");
        var output = new StringBuilder();
        void Write(IEnumerable<string> values) => output.AppendJoin('\t', values.Select(value => DelimitedText.Escape(value, '\t'))).Append("\r\n");
        if (includeHeaders) Write(columns.Select(column => column.Caption));
        foreach (int index in indexes)
        {
            var row = view.Rows[index].Record!;
            Write(columns.Select(column =>
            {
                if (!selection.ContainsCell(row.Id, column.Field)) return "";
                object? value = column.GetValue(row);
                if (useDisplayFormatting) return column.Display(value, view.Table.Schema.Culture);
                return value switch
                {
                    null => "", DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                    DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                    IFormattable format => format.ToString(null, view.Table.Schema.Culture) ?? "",
                    _ => value.ToString() ?? ""
                };
            }));
        }
        return output.ToString();
    }
    public static IReadOnlyList<CellEdit> CreatePaste(GridView view, GridSelection selection, IReadOnlyList<string> fields, string text,
        bool fillSelectedCells = true, int maximumRows = 10000, int maximumCells = 2_000_000)
    {
        if (maximumCells < 1) throw new ArgumentOutOfRangeException(nameof(maximumCells));
        if (selection.FocusedCell is not { } focus) throw new InvalidOperationException("Select a starting cell before pasting.");
        var columns = Resolve(view, fields);
        int startColumn = Array.FindIndex(columns, column => column.Field.Equals(focus.Field, StringComparison.OrdinalIgnoreCase));
        int startRow = view.IndexOfRecord(focus.RecordId);
        if (startRow < 0 || startColumn < 0) throw new InvalidOperationException("The focused cell is not visible in the current view.");
        var input = DelimitedText.Parse(text, '\t', maximumRows);
        var edits = new List<CellEdit>();
        void Add(CellEdit edit)
        {
            if (edits.Count >= maximumCells) throw new InvalidOperationException($"The paste exceeds {maximumCells:N0} cells.");
            edits.Add(edit);
        }
        if (input.Count == 1 && input[0].Count == 1 && fillSelectedCells && selection.Mode == GridSelectionMode.Cells && selection.SelectedCellCount > 1)
        {
            var fieldsByIndex = columns.Select((column, index) => (column.Field, index))
                .ToDictionary(pair => pair.Field, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var selected = selection.EnumerateCells()
                .Where(cell => fieldsByIndex.ContainsKey(cell.Field))
                .Select(cell => (Cell: cell, Row: view.IndexOfRecord(cell.RecordId)))
                .Where(item => item.Row >= 0).OrderBy(item => item.Row).ThenBy(item => fieldsByIndex[item.Cell.Field]);
            foreach (var item in selected) Add(new(item.Cell.RecordId, item.Cell.Field, input[0][0]));
        }
        else
        {
            var rows = view.Rows.Skip(startRow).Where(row => row.Kind == GridRowKind.Data).Take(input.Count).ToArray();
            if (input.Count > rows.Length) throw new InvalidOperationException("The clipboard contains more rows than the remaining visible records.");
            for (int row = 0; row < input.Count; row++)
            {
                if (startColumn + input[row].Count > columns.Length) throw new InvalidOperationException("The clipboard extends beyond the visible columns.");
                for (int column = 0; column < input[row].Count; column++)
                    Add(new(rows[row].Record!.Id, columns[startColumn + column].Field, input[row][column]));
            }
        }
        // Fail before any mutation. The table subsequently validates every value and row atomically.
        foreach (var edit in edits)
            if (view.Table.Schema[edit.Field].IsReadOnly) throw new InvalidOperationException($"{view.Table.Schema[edit.Field].Caption} is read-only.");
        return edits.AsReadOnly();
    }
    private static GridColumn[] Resolve(GridView view, IReadOnlyList<string> fields)
    {
        var columns = fields.Select(field => view.Table.Schema[field]).ToArray();
        if (columns.Length == 0 || columns.Select(column => column.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new ArgumentException("Clipboard columns must be nonempty and unique.", nameof(fields));
        return columns;
    }
}

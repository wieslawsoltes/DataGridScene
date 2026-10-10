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
        var rows = view.Rows.Where(row => row.Kind == GridRowKind.Data).Select(row => row.Record!).ToArray();
        if (selection.Mode == GridSelectionMode.Cells)
        {
            int first = Array.FindIndex(rows, row => columns.Any(column => selection.ContainsCell(row.Id, column.Field)));
            int last = Array.FindLastIndex(rows, row => columns.Any(column => selection.ContainsCell(row.Id, column.Field)));
            if (first < 0) return string.Empty;
            rows = rows[first..(last + 1)];
            int left = Array.FindIndex(columns, column => rows.Any(row => selection.ContainsCell(row.Id, column.Field)));
            int right = Array.FindLastIndex(columns, column => rows.Any(row => selection.ContainsCell(row.Id, column.Field)));
            columns = columns[left..(right + 1)];
        }
        else rows = rows.Where(row => selection.Contains(row.Id)).ToArray();
        if ((long)(rows.Length + (includeHeaders ? 1 : 0)) * columns.Length > maximumCells)
            throw new InvalidOperationException($"The clipboard operation exceeds {maximumCells:N0} cells. Use an export for larger selections.");
        var output = new StringBuilder();
        void Write(IEnumerable<string> values) => output.AppendJoin('\t', values.Select(value => DelimitedText.Escape(value, '\t'))).Append("\r\n");
        if (includeHeaders) Write(columns.Select(column => column.Caption));
        foreach (var row in rows)
        {
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
            foreach (var row in view.Rows.Where(row => row.Kind == GridRowKind.Data))
                foreach (var column in columns)
                    if (selection.ContainsCell(row.Record!.Id, column.Field)) Add(new(row.Record.Id, column.Field, input[0][0]));
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

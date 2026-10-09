using System.Text;

namespace DataGridScene.Export;

public static class DelimitedText
{
    public static string Escape(string value, char delimiter = ',')
    {
        return value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter = ',', int maximumRows = 100000, int maximumColumns = 16384)
    {
        if (text.Length > 32 * 1024 * 1024) throw new FormatException("Delimited input exceeds 32 MiB.");
        if (delimiter is '\r' or '\n' or '"') throw new ArgumentException("Invalid delimiter.", nameof(delimiter));
        if (maximumRows < 1 || maximumColumns < 1) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        var rows = new List<IReadOnlyList<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, afterQuote = false, started = false;
        void AddCell()
        {
            if (row.Count >= maximumColumns) throw new FormatException("Too many columns.");
            row.Add(cell.ToString()); cell.Clear(); afterQuote = false; started = false;
        }
        void AddRow()
        {
            if (rows.Count >= maximumRows) throw new FormatException("Too many rows.");
            rows.Add(row.AsReadOnly()); row = [];
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else cell.Append(c);
            }
            else if (c == delimiter) AddCell();
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                AddCell(); AddRow();
            }
            else if (afterQuote) throw new FormatException("Unexpected text after a closing quote.");
            else if (c == '"')
            {
                if (started) throw new FormatException("A quote must start a field.");
                quoted = true; started = true;
            }
            else { started = true; cell.Append(c); }
            if (cell.Length > 1_048_576) throw new FormatException("A field exceeds 1 MiB.");
        }
        if (quoted) throw new FormatException("Unterminated quoted field.");
        if (started || afterQuote || cell.Length > 0 || row.Count > 0 || text.Length > 0 && text[^1] == delimiter) { AddCell(); AddRow(); }
        return rows.AsReadOnly();
    }
}

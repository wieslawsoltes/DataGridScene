using DataGridScene.Core;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;

namespace DataGridScene.Export;

public sealed record ExportOptions
{
    public IReadOnlyList<string>? Fields { get; init; }
    public IReadOnlySet<long>? SelectedIds { get; init; }
    public bool IncludeHeaders { get; init; } = true;
    public bool ProtectSpreadsheetFormulas { get; init; } = true;
    public bool UseDisplayFormatting { get; init; }
    public bool PreserveNumericPrecision { get; init; } = true;
    public char Delimiter { get; init; } = ',';
}

public static class GridExporter
{
    private static GridColumn[] Columns(GridView view, ExportOptions options)
    {
        var columns = options.Fields is null ? view.Table.Schema.Columns.ToArray() : options.Fields.Select(field => view.Table.Schema[field]).ToArray();
        if (columns.Length == 0 || columns.Select(column => column.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length) throw new ArgumentException("Export columns must be nonempty and unique.");
        return columns;
    }
    private static IEnumerable<GridRecord> Rows(GridView view, ExportOptions options) => options.SelectedIds is null ? view.OrderedRecords : view.OrderedRecords.Where(record => options.SelectedIds.Contains(record.Id));
    private static string ValueText(GridColumn column, object? value, GridSchema schema, ExportOptions options)
    {
        if (value is null) return "";
        if (options.UseDisplayFormatting) return column.Display(value, schema.Culture);
        return value switch
        {
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => value.ToString() ?? ""
        };
    }
    public static string ToCsv(GridView view, ExportOptions? options = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(writer, view, options); return writer.ToString();
    }
    public static void WriteCsv(TextWriter writer, GridView view, ExportOptions? options = null)
    {
        options ??= new();
        if (options.Delimiter is '\r' or '\n' or '"') throw new ArgumentException("Invalid delimiter.");
        var columns = Columns(view, options);
        if (options.IncludeHeaders) writer.WriteLine(string.Join(options.Delimiter, columns.Select(column => DelimitedText.Escape(column.Caption, options.Delimiter))));
        foreach (var record in Rows(view, options))
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (i > 0) writer.Write(options.Delimiter);
                object? value = columns[i].GetValue(record);
                string text = ValueText(columns[i], value, view.Table.Schema, options);
                if (options.ProtectSpreadsheetFormulas && value is string && text.TrimStart() is { Length: > 0 } trimmed && (trimmed[0] is '=' or '+' or '-' or '@' || text[0] is '\t' or '\r')) text = "'" + text;
                writer.Write(DelimitedText.Escape(text, options.Delimiter));
            }
            writer.Write("\r\n");
        }
    }
    public static string ToHtml(GridView view, ExportOptions? options = null)
    {
        options ??= new(); var columns = Columns(view, options);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>DataGridScene export</title><style>body{font:14px system-ui;margin:24px;color:#242424}table{border-collapse:collapse}th,td{padding:7px 12px;border:1px solid #d8dce2;text-align:left}th{background:#f1f3f6}tr:nth-child(even){background:#f8f9fb}@media print{thead{display:table-header-group}tr{break-inside:avoid}}</style><body><table>");
        if (options.IncludeHeaders)
        {
            html.Append("<thead><tr>"); foreach (var column in columns) html.Append("<th scope=\"col\">").Append(WebUtility.HtmlEncode(column.Caption)).Append("</th>"); html.Append("</tr></thead>");
        }
        html.Append("<tbody>");
        foreach (var record in Rows(view, options))
        {
            html.Append("<tr>"); foreach (var column in columns) html.Append("<td>").Append(WebUtility.HtmlEncode(ValueText(column, column.GetValue(record), view.Table.Schema, options))).Append("</td>"); html.Append("</tr>");
        }
        return html.Append("</tbody></table></body></html>").ToString();
    }
    public static byte[] ToXlsx(GridView view, ExportOptions? options = null)
    {
        using var stream = new MemoryStream(); WriteXlsx(stream, view, options); return stream.ToArray();
    }
    public static void WriteXlsx(Stream stream, GridView view, ExportOptions? options = null)
    {
        options ??= new(); var columns = Columns(view, options); var rows = Rows(view, options).ToArray();
        if (columns.Length > 16384 || rows.Length + (options.IncludeHeaders ? 1 : 0) > 1_048_576) throw new ArgumentException("The data exceeds XLSX worksheet limits.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
        void Text(string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
        }
        Text("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Text("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Text("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Text("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Text("xl/styles.xml", "<?xml version=\"1.0\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm\"/></numFmts><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var output = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
        using var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        xml.WriteStartDocument(); xml.WriteStartElement("worksheet", ns);
        xml.WriteStartElement("sheetViews", ns); xml.WriteStartElement("sheetView", ns); xml.WriteAttributeString("workbookViewId", "0");
        if (options.IncludeHeaders) { xml.WriteStartElement("pane", ns); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen"); xml.WriteEndElement(); }
        xml.WriteEndElement(); xml.WriteEndElement();
        xml.WriteStartElement("cols", ns);
        for (int i = 0; i < columns.Length; i++) { xml.WriteStartElement("col", ns); xml.WriteAttributeString("min", (i + 1).ToString()); xml.WriteAttributeString("max", (i + 1).ToString()); xml.WriteAttributeString("width", Math.Clamp(columns[i].Width / 7, 8, 80).ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); }
        xml.WriteEndElement(); xml.WriteStartElement("sheetData", ns);
        int rowNumber = 0;
        void WriteCell(int columnIndex, object? value)
        {
            xml.WriteStartElement("c", ns); xml.WriteAttributeString("r", ColumnName(columnIndex) + rowNumber);
            string? numeric = null; bool isDate = false;
            if (value is DateTime date && date.Year >= 1900) { numeric = date.ToOADate().ToString("R", CultureInfo.InvariantCulture); isDate = true; }
            else if (value is not null && GridValue.IsNumericType(value.GetType()))
            {
                string candidate = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                int digits = candidate.Count(char.IsDigit);
                if ((!options.PreserveNumericPrecision || digits <= 15) && !(value is double d && !double.IsFinite(d)) && !(value is float f && !float.IsFinite(f))) numeric = candidate;
            }
            if (value is bool flag) { xml.WriteAttributeString("t", "b"); xml.WriteElementString("v", ns, flag ? "1" : "0"); }
            else if (numeric is not null) { if (isDate) xml.WriteAttributeString("s", "1"); xml.WriteElementString("v", ns, numeric); }
            else
            {
                xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is", ns); xml.WriteStartElement("t", ns); xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                string text = value is null ? "" : value is DateTime dt ? dt.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (text.Length > 32767) throw new ArgumentException("A cell exceeds XLSX's 32,767-character limit.");
                xml.WriteString(SafeXml(text)); xml.WriteEndElement(); xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        if (options.IncludeHeaders)
        {
            rowNumber++; xml.WriteStartElement("row", ns); xml.WriteAttributeString("r", rowNumber.ToString());
            for (int i = 0; i < columns.Length; i++) WriteCell(i, columns[i].Caption);
            xml.WriteEndElement();
        }
        foreach (var record in rows)
        {
            rowNumber++; xml.WriteStartElement("row", ns); xml.WriteAttributeString("r", rowNumber.ToString());
            for (int i = 0; i < columns.Length; i++) WriteCell(i, columns[i].GetValue(record));
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        if (options.IncludeHeaders && rowNumber > 0) { xml.WriteStartElement("autoFilter", ns); xml.WriteAttributeString("ref", $"A1:{ColumnName(columns.Length - 1)}{rowNumber}"); xml.WriteEndElement(); }
        xml.WriteEndElement(); xml.WriteEndDocument();
    }
    private static string SafeXml(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes()) if (rune.Value is 9 or 10 or 13 || rune.Value >= 32 && rune.Value <= 0x10ffff && rune.Value is not (0xfffe or 0xffff)) text.Append(rune.ToString());
        return text.ToString();
    }
    public static string ColumnName(int index)
    {
        if (index < 0 || index >= 16384) throw new ArgumentOutOfRangeException(nameof(index));
        string name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }
}

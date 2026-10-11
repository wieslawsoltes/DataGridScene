using DataGridScene.Core;

namespace DataGridScene.Data;

public static partial class DataSourceValidation
{
    /// <summary>Detach the record-list container and check boundaries before publishing a page.
    /// Records and application payloads are source-owned immutable snapshots after publication.</summary>
    public static GridPage FreezePage(GridPage page, int offset, int count, GridSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (offset < 0 || count < 1 || count > 10000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (page is null || page.Offset != offset || page.Records is null || page.Records.Count > count || page.TotalCount is < 0 ||
            page.TotalCount is int total && page.Records.Count > 0 && (long)offset + page.Records.Count > total)
            throw new InvalidDataException("The source returned inconsistent page metadata.");
        var rows = page.Records.ToArray();
        var ids = new HashSet<long>();
        foreach (var row in rows)
        {
            if (row is null || row.Count != schema.Columns.Count) throw new InvalidDataException("A page record does not match the schema.");
            if (!ids.Add(row.Id)) throw new InvalidDataException("Duplicate record identities within a page.");
        }
        return page with { Records = Array.AsReadOnly(rows) };
    }
}

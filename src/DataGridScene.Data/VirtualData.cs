using DataGridScene.Core;
using System.Net.Http.Json;
using System.Text.Json;

namespace DataGridScene.Data;

[Flags]
public enum DataCapabilities { None = 0, Sort = 1, Filter = 2, Search = 4, Summaries = 8 }
public sealed record GridPage(int Offset, IReadOnlyList<GridRecord> Records, int? TotalCount, string? Revision = null);
public interface IGridPageSource
{
    GridSchema Schema { get; }
    DataCapabilities Capabilities { get; }
    ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken);
}
public static partial class DataSourceValidation
{
    public static void Validate(GridQuery query, DataCapabilities capabilities, int offset, int count)
    {
        if (offset < 0 || count < 1 || count > 10000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (query.EffectiveGroupSummaries.Count > 0 && query.GroupSummaries is not null || query.GroupSummarySorts.Count > 0 || query.GroupFooterDisplay != GroupFooterDisplayMode.Hidden) throw new NotSupportedException("The record-page contract does not represent group summaries or footers.");
        if (query.Groups.Count > 0) throw new NotSupportedException("The record-page contract does not represent server group rows.");
        if (query.Sorts.Count > 0 && !capabilities.HasFlag(DataCapabilities.Sort)) throw new NotSupportedException("This source does not support sorting.");
        if (query.IsFilterEnabled && query.Filter is not null && !capabilities.HasFlag(DataCapabilities.Filter)) throw new NotSupportedException("This source does not support filtering.");
        if (query.Search.Length > 0 && !capabilities.HasFlag(DataCapabilities.Search)) throw new NotSupportedException("This source does not support search.");
        if (query.Summaries.Count > 0 && !capabilities.HasFlag(DataCapabilities.Summaries)) throw new NotSupportedException("This source does not support summaries.");
    }
}
public sealed class LocalPageSource(GridTable table) : IGridPageSource
{
    public GridSchema Schema => table.Schema;
    public DataCapabilities Capabilities => DataCapabilities.Sort | DataCapabilities.Filter | DataCapabilities.Search;
    public ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); query = QuerySnapshot.Capture(query, Schema); DataSourceValidation.Validate(query, Capabilities, offset, count);
        using var view = new GridView(table, query);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GridPage(offset, view.FilteredRecords.Skip(offset).Take(count).ToArray(), view.FilteredRecords.Count, table.Revision.ToString()));
    }
}

/// <summary>POST JSON adapter. Credentials and HttpClient lifetime remain the caller's responsibility.</summary>
public sealed class HttpJsonPageSource(HttpClient client, Uri endpoint, GridSchema schema, DataCapabilities capabilities) : IGridPageSource
{
    public GridSchema Schema => schema;
    public DataCapabilities Capabilities => capabilities;
    public sealed record Request(GridQuery Query, int Offset, int Count);
    public sealed record RecordDto(long Id, JsonElement[] Values, long? ParentId = null);
    public sealed record Response(int Offset, RecordDto[] Records, int? TotalCount, string? Revision);
    public async ValueTask<GridPage> FetchAsync(GridQuery query, int offset, int count, CancellationToken cancellationToken)
    {
        query = QuerySnapshot.Capture(query, Schema);
        DataSourceValidation.Validate(query, Capabilities, offset, count);
        using var response = await client.PostAsJsonAsync(endpoint, new Request(query, offset, count), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<Response>(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Empty page response.");
        if (payload.Records is null || payload.Records.Any(row => row is null || row.Values is null)) throw new InvalidDataException("Missing page records or values.");
        if (payload.Offset != offset || payload.Records.Length > count || payload.TotalCount is < 0 ||
            payload.TotalCount is int total && payload.Records.Length > 0 && (long)offset + payload.Records.Length > total)
            throw new InvalidDataException("Invalid page metadata.");
        var table = new GridTable(schema);
        table.AddRange(payload.Records.Select(row => new GridRecord(row.Id, row.Values.Cast<object?>(), parentId: row.ParentId)));
        return DataSourceValidation.FreezePage(new(offset, table.Records, payload.TotalCount, payload.Revision), offset, count, schema);
    }
}


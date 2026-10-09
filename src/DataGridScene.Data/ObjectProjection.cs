using DataGridScene.Core;

namespace DataGridScene.Data;

/// <summary>Explicit, trim-safe object projection. No reflection or runtime code generation is required.</summary>
public sealed record ObjectField<T>(GridColumn Column, Func<T, object?> Read) where T : notnull;
public sealed class ObjectProjection<T> where T : notnull
{
    private readonly IReadOnlyList<ObjectField<T>> _fields;
    private readonly Func<T, long> _identity;
    public GridSchema Schema { get; }
    public ObjectProjection(IEnumerable<ObjectField<T>> fields, Func<T, long> identity)
    {
        _fields = fields.ToArray(); _identity = identity; Schema = new GridSchema(_fields.Select(field => field.Column));
    }
    public GridTable CreateTable(IEnumerable<T> source)
    {
        var table = new GridTable(Schema);
        table.AddRange(source.Select(item => new GridRecord(_identity(item), _fields.Select(field => field.Read(item)), item)));
        return table;
    }
}

namespace Fisher.Linq.Members;

/// <summary>
///     The general document member, backed by <c>json_extract(data, '$.path')</c>.
/// </summary>
internal class QueryableMember : IQueryableMember
{
    public QueryableMember(string locator, Type memberType, bool isBoolean = false)
    {
        RawLocator = locator;
        TypedLocator = locator;
        MemberType = memberType;
        IsBoolean = isBoolean;
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator { get; }
    public bool IsBoolean { get; }

    public object? ConvertValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        // SQLite has no boolean type. json_extract turns a JSON true/false into INTEGER 1/0, which is
        // also how Fisher stores every other boolean — so the literal has to be 1/0, not the "true"/
        // "false" strings Polecat needs for JSON_VALUE's nvarchar result.
        if (IsBoolean)
        {
            return (bool)value ? 1 : 0;
        }

        // System.Text.Json writes a Guid as the lowercase canonical form, which is exactly what
        // Guid.ToString() produces. Binding the Guid itself would write a 16-byte BLOB that matches
        // nothing — the same trap SqliteGuidIdentification exists for on the id column.
        if (value is Guid guid)
        {
            return guid.ToString();
        }

        return value;
    }
}

namespace Fisher.Linq.Members;

/// <summary>
///     The document's identity, mapped to the <c>id</c> column rather than into the JSON.
/// </summary>
internal class IdMember : IQueryableMember
{
    public IdMember(Type idType)
    {
        MemberType = idType;
    }

    public Type MemberType { get; }
    public string TypedLocator => "id";
    public string RawLocator => "id";
    public bool IsBoolean => false;

    /// <summary>
    ///     A Guid id is held as lowercase canonical TEXT, written that way by
    ///     <c>SqliteGuidIdentification</c>. Binding the raw <see cref="Guid" /> would write a 16-byte
    ///     BLOB, and binding it as an uppercase string would miss under SQLite's case-sensitive default
    ///     collation — both fail by returning nothing rather than by erroring, which is why this
    ///     conversion is not optional.
    /// </summary>
    public object? ConvertValue(object? value)
        => value is Guid guid ? guid.ToString() : value;
}

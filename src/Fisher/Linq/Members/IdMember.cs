namespace Fisher.Linq.Members;

/// <summary>
///     The document's identity, mapped to the <c>id</c> column rather than into the JSON.
/// </summary>
internal class IdMember : IQueryableMember
{
    /// <param name="idType">The identity's CLR type.</param>
    /// <param name="qualifier">
    ///     A table alias and its dot, or empty. Non-empty only inside a join (fisher#25), where
    ///     <c>id</c> alone is ambiguous when both sides have one — which every document table does.
    /// </param>
    public IdMember(Type idType, string qualifier = "")
    {
        MemberType = idType;
        TypedLocator = qualifier + "id";
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator => TypedLocator;
    public bool IsBoolean => false;

    /// <summary>
    ///     A Guid id is held as lowercase canonical TEXT, written that way by
    ///     <c>SqliteGuidIdentification</c>. Binding the raw <see cref="Guid" /> would write a 16-byte
    ///     BLOB, and binding it as an uppercase string would miss under SQLite's case-sensitive default
    ///     collation — both fail by returning nothing rather than by erroring, which is why this
    ///     conversion is not optional.
    /// </summary>
    public object? ConvertValue(object? value)
    {
        // fisher#356: a strong-typed id compares as the inner value the column holds. The wrapper itself
        // cannot be bound at all — Microsoft.Data.Sqlite refuses a type it has no mapping for.
        if (value is not null && Storage.StrongTypedId.TryResolve(value.GetType(), out var wrapper))
        {
            value = wrapper.ValueProperty.GetValue(value);
        }

        return value is Guid guid ? guid.ToString() : value;
    }
}

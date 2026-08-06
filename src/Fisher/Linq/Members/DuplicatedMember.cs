namespace Fisher.Linq.Members;

/// <summary>
///     A member whose value is also available in a column of its own, so a comparison against it can
///     use an index.
/// </summary>
/// <remarks>
///     <para>
///         Everything except <see cref="TypedLocator" /> is the underlying member's, which is what makes
///         duplicating a field a pure performance decision: the same predicate means the same thing,
///         because the column's generated expression <em>is</em> the locator being replaced. A
///         duplicated timestamp compares through the same <c>strftime</c> normalisation, a duplicated
///         string-stored enum still refuses to be ordered, and a duplicated bool still binds 1/0.
///     </para>
///     <para>
///         <b><see cref="RawLocator" /> stays on the JSON.</b> It answers "is this member present",
///         which is not quite "is the column null" — a value the generated expression cannot parse
///         yields a null column while the JSON key is there — and the null test cannot use the index
///         anyway. The string methods and <c>length</c> use the raw locator for the same reason, and
///         they are the operations no index would serve.
///     </para>
/// </remarks>
internal sealed class DuplicatedMember : IQueryableMember
{
    private readonly IQueryableMember _inner;

    public DuplicatedMember(IQueryableMember inner, string columnName)
    {
        _inner = inner;
        TypedLocator = Weasel.Sqlite.SchemaUtils.QuoteName(columnName);
    }

    /// <summary>
    ///     What the column's <c>GENERATED ALWAYS AS</c> expression must be: the locator this one
    ///     replaces. Asked for by <see cref="Storage.DocumentTable" />, so the column is defined as
    ///     exactly what the query stopped computing.
    /// </summary>
    public string GeneratedExpression => _inner.TypedLocator;

    public Type MemberType => _inner.MemberType;

    public string TypedLocator { get; }

    public string RawLocator => _inner.RawLocator;

    public bool IsBoolean => _inner.IsBoolean;

    public bool AllowsRangeComparison => _inner.AllowsRangeComparison;

    public object? ConvertValue(object? value) => _inner.ConvertValue(value);
}

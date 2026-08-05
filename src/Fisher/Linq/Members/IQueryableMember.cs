namespace Fisher.Linq.Members;

/// <summary>
///     A queryable member of a document, and the SQL locator it resolves to.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <c>Polecat.Linq.Members.IQueryableMember</c> with two deliberate differences, both
///         forced by <c>json_extract</c> behaving unlike SQL Server's <c>JSON_VALUE</c>.
///     </para>
///     <para>
///         <see cref="TypedLocator" /> and <see cref="RawLocator" /> are usually the <em>same string</em>
///         in Fisher. <c>JSON_VALUE</c> always returns <c>nvarchar</c>, so Polecat must wrap every typed
///         comparison in a <c>CAST</c>. <c>json_extract</c> returns the JSON value's natural SQLite
///         type — <c>integer</c> for a JSON number, <c>real</c> for a float, <c>text</c> for a string —
///         so <c>json_extract(data,'$.Age') &gt; 30</c> compares numerically with no cast at all. The
///         distinction is kept because the shape is worth mirroring and null checks still want the bare
///         locator.
///     </para>
///     <para>
///         <see cref="AllowsRangeComparison" /> has no Polecat counterpart. It exists because some
///         members are stored in a form that is correct for equality but meaningless when ordered — see
///         <see cref="DateMember" />.
///     </para>
/// </remarks>
internal interface IQueryableMember
{
    /// <summary>
    ///     The CLR type of the member.
    /// </summary>
    Type MemberType { get; }

    /// <summary>
    ///     The locator to use for a typed comparison.
    /// </summary>
    string TypedLocator { get; }

    /// <summary>
    ///     The bare locator, with no wrapping — what an <c>is null</c> check compares.
    /// </summary>
    string RawLocator { get; }

    /// <summary>
    ///     Whether the member is a boolean. SQLite has no boolean type and <c>json_extract</c> yields
    ///     INTEGER 1/0 for a JSON <c>true</c>/<c>false</c>, matching how Fisher stores booleans
    ///     everywhere else.
    /// </summary>
    bool IsBoolean { get; }

    /// <summary>
    ///     Whether <c>&lt;</c>, <c>&gt;</c> and ordering are meaningful against this member's stored
    ///     form. False for members whose storage is not order-preserving text.
    /// </summary>
    bool AllowsRangeComparison => true;

    /// <summary>
    ///     Render a CLR value into the form the stored JSON actually holds, so a predicate literal
    ///     compares against like for like.
    /// </summary>
    object? ConvertValue(object? value);
}

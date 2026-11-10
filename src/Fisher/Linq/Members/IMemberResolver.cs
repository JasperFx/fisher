using System.Linq.Expressions;

namespace Fisher.Linq.Members;

/// <summary>
///     Resolves a member expression to the <see cref="IQueryableMember" /> that knows its SQL locator.
/// </summary>
internal interface IMemberResolver
{
    IQueryableMember ResolveMember(MemberExpression expression);

    /// <summary>
    ///     The document mapping the members belong to, or null where they belong to something that is
    ///     not a document — an event body or a stream-state row.
    /// </summary>
    /// <remarks>
    ///     Defaulted rather than required, so the two non-document resolvers stay as small as they
    ///     are. Full-text search is what wants it (fisher#215): the FTS5 index is named after the
    ///     document's table and declared on its mapping, neither of which a locator carries — and a
    ///     null here is what refuses the operator against an event body by name.
    /// </remarks>
    Storage.DocumentMapping? Mapping => null;

    /// <summary>
    ///     The table alias every locator this resolver builds is qualified with, including the
    ///     trailing dot, or empty for the unqualified form.
    /// </summary>
    /// <remarks>
    ///     A predicate that names a column directly rather than through an
    ///     <see cref="IQueryableMember" /> still has to be qualified under a join — the full-text
    ///     filter's <c>rowid</c> is the case — and this is where it gets the qualifier from rather
    ///     than rebuilding one.
    /// </remarks>
    string TableQualifier => "";

    /// <summary>
    ///     A member of one case of a union-typed member, read through <c>(Case)x.Member.Value</c>
    ///     (fisher#466). Only document and event-body JSON has union members.
    /// </summary>
    IQueryableMember ResolveUnionCase(MemberExpression unionMember, Type caseType)
        => throw new BadLinqExpressionException(
            $"Cannot translate the union case access '({caseType.Name}){unionMember}.Value' here.");

    /// <summary>
    ///     <c>x.Member.Value is Case</c>, or <c>x.Member.Value == null</c> when <paramref name="caseType" />
    ///     is null (fisher#466).
    /// </summary>
    Weasel.Core.SqlGeneration.ISqlFragment ResolveUnionTypeTest(MemberExpression unionMember, Type? caseType)
        => throw new BadLinqExpressionException($"Cannot translate a type test of '{unionMember}.Value' here.");
}

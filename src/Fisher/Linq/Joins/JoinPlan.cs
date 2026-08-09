using System.Linq.Expressions;
using Fisher.Linq.Members;
using Weasel.Storage;

namespace Fisher.Linq.Joins;

/// <summary>
///     What reading a joined row needs, once the SQL has been built.
/// </summary>
/// <param name="Outer">The outer side's select clause — its columns start at 0.</param>
/// <param name="OuterType">The outer document type.</param>
/// <param name="Inner">The inner side's select clause.</param>
/// <param name="InnerType">The inner document type.</param>
/// <param name="InnerOffset">
///     Where the inner side's columns begin, which is however many the outer side selected.
/// </param>
/// <param name="InnerDataOrdinal">
///     Which of the inner side's own columns is <c>data</c> — the one column whose being NULL means the
///     left join found no match, since every other one can legitimately be null.
/// </param>
/// <param name="Project">The caller's result selector, over the two documents.</param>
/// <param name="Member">
///     Resolves a lambda written after the join — an aggregate's selector, say — to the document member
///     behind it, or null when it names no member of either document.
/// </param>
/// <remarks>
///     <see cref="Member" /> is the same mapping the post-join <c>Where</c> and <c>OrderBy</c> go
///     through, held as a closure so a terminal operator can reach it after the statement has been
///     built. Without it an aggregate selector would have to re-derive the projection, the two member
///     factories and the intermediate shape from scratch, which is three chances to disagree with the
///     clauses already on the statement about which side a member belongs to.
/// </remarks>
internal sealed record JoinPlan(
    ISelectClause Outer,
    Type OuterType,
    ISelectClause Inner,
    Type InnerType,
    int InnerOffset,
    int InnerDataOrdinal,
    Func<object, object?, object?> Project,
    Func<LambdaExpression, IQueryableMember?> Member);

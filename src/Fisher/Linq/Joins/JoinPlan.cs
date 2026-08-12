using System.Linq.Expressions;
using Fisher.Linq.Members;
using Weasel.Storage;

namespace Fisher.Linq.Joins;

/// <summary>
///     One document table taking part in a join, and where its columns sit in the joined row.
/// </summary>
/// <param name="DocumentType">The document type this side reads.</param>
/// <param name="Alias">The alias every locator on this side is qualified with.</param>
/// <param name="Clause">This side's select clause — the same one a <c>LoadAsync</c> would read through.</param>
/// <param name="Members">This side's member factory, already carrying <paramref name="Alias" />.</param>
/// <param name="Parameter">
///     The canonical parameter standing for this side's document once every lambda in the chain has
///     been rewritten onto the documents. Attribution is by <b>reference</b> to this, never by type: a
///     self-join has the same document type on more than one side.
/// </param>
/// <param name="Offset">Where this side's columns begin, which is however many the sides before it selected.</param>
/// <param name="DataOrdinal">
///     Which of this side's own columns is <c>data</c> — the one column whose being NULL means a left
///     join found no match here, since every other one can legitimately be null.
/// </param>
internal sealed record JoinSide(
    Type DocumentType,
    string Alias,
    ISelectClause Clause,
    MemberFactory Members,
    ParameterExpression Parameter,
    int Offset,
    int DataOrdinal);

/// <summary>
///     What reading a joined row needs, once the SQL has been built.
/// </summary>
/// <param name="Sides">
///     Every table in the join, outer first, in the order their columns appear in the row.
/// </param>
/// <param name="Project">
///     The caller's result selector, over one document per side in <paramref name="Sides" /> order. A
///     side that a left join found no match for arrives null.
/// </param>
/// <param name="Member">
///     Resolves a lambda written after the join — an aggregate's selector, say — to the document member
///     behind it, or null when it names no member of any side.
/// </param>
/// <remarks>
///     <para>
///         <see cref="Member" /> is the same mapping the post-join <c>Where</c> and <c>OrderBy</c> go
///         through, held as a closure so a terminal operator can reach it after the statement has been
///         built. Without it an aggregate selector would have to re-derive the projection, every side's
///         member factory and the intermediate shapes from scratch, which is that many chances to
///         disagree with the clauses already on the statement about which side a member belongs to.
///     </para>
///     <para>
///         <b>A list of sides rather than an outer and an inner</b> since fisher#55. One join needs two
///         and reads as a pair; a chain needs N, and every place that special-cased "the inner one" —
///         the offsets, the null check, the result selector's arity — turned out to be the same code
///         written for a list of length two.
///     </para>
/// </remarks>
internal sealed record JoinPlan(
    IReadOnlyList<JoinSide> Sides,
    Func<object?[], object?> Project,
    Func<LambdaExpression, IQueryableMember?> Member);

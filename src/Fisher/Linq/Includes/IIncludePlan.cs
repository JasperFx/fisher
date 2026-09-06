using Fisher.Internal;

namespace Fisher.Linq.Includes;

/// <summary>
///     One <c>Include()</c> attached to a query — the related documents to fetch once the query's own
///     rows are in hand, and where to put them.
/// </summary>
/// <remarks>
///     Non-generic so the parser and the provider can carry a list of them without knowing either
///     document type; the generic work happens inside <see cref="IncludePlan{TParent,TInclude}" />.
/// </remarks>
internal interface IIncludePlan
{
    /// <summary>The document type being included, named in refusal messages.</summary>
    Type IncludeType { get; }

    /// <summary>
    ///     Fetch the related documents for <paramref name="parents" /> and hand each to this plan's
    ///     destination.
    /// </summary>
    /// <remarks>
    ///     Called after the parent rows have been materialized, which is what makes the identity
    ///     values available without a temporary table — see <see cref="IncludePlan{TParent,TInclude}" />
    ///     for why that is the right trade on an embedded store.
    /// </remarks>
    Task ResolveAsync(FisherSession session, IReadOnlyList<object> parents, CancellationToken token);
}

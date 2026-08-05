using JasperFx.Events.Tags;

namespace Fisher.Events.Tags;

/// <summary>
///     A set of DCB reads declared up front and run together by <see cref="Execute" />.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This exists for API parity, not for speed, and that is a deliberate choice rather
///         than an unfinished one.</strong> In Marten and Polecat a batch earns its keep by collapsing
///         several network round trips into one. SQLite is embedded, so there are no round trips to
///         collapse and Fisher gains essentially nothing in throughput. It is here so that DCB code
///         written against one Critter Stack store runs unchanged against another, and so Fisher can
///         honestly enroll in the shared batched-query compliance tests rather than passing them on a
///         test-only shim.
///     </para>
///     <para>
///         Do not reach for this expecting it to be faster than the same reads issued directly — it is
///         not, and it is not trying to be. The one property that does still hold: the reads run back
///         to back against one connection with nothing interleaved, so a set of boundaries is
///         established against a coherent view rather than drifting apart as each is fetched.
///     </para>
///     <para>
///         Each method hands back a task that does <em>not</em> complete until <see cref="Execute" />
///         runs, matching the siblings' contract.
///     </para>
/// </remarks>
public interface IBatchedQuery
{
    /// <summary>
    ///     Whether any event matches the tag query.
    /// </summary>
    Task<bool> EventsExist(EventTagQuery query);

    /// <summary>
    ///     Open a writable DCB boundary over the tag query.
    /// </summary>
    Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query) where T : class;

    /// <summary>
    ///     Run everything declared so far and complete the tasks already handed out.
    /// </summary>
    Task Execute(CancellationToken token = default);
}

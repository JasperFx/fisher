using JasperFx.Events.Tags;

namespace Fisher.Events.Tags;

/// <summary>
///     A set of DCB reads declared up front and run together by <see cref="Execute" />.
/// </summary>
/// <remarks>
///     <para>
///         Each method hands back a task that does <em>not</em> complete until <see cref="Execute" />
///         runs. That is the contract Marten and Polecat's batched queries follow, and matching it is
///         most of the point: code written against one store's batch ports to another.
///     </para>
///     <para>
///         <strong>The rationale differs on SQLite, and it is worth being honest about.</strong> In the
///         siblings a batch exists to collapse several network round trips into one. SQLite is
///         embedded, so there are no round trips to collapse and the throughput argument mostly
///         evaporates. What remains is the part that still holds here: the reads run back to back
///         against one connection with nothing interleaved, so a set of boundaries is established
///         against a coherent view rather than drifting apart as each is fetched.
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

using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Batching;

/// <summary>
///     The event store reads a batch can declare, reached as <see cref="IBatchedQuery.Events" />: the same
///     shape as Marten's <c>batch.Events</c>, so code that batches aggregate fetches ports between the stores
///     (fisher#679).
/// </summary>
/// <remarks>
///     <para>
///         Every read runs when <see cref="IBatchedQuery.Execute" /> does, in declaration order, on the session's
///         one connection. As with the rest of <see cref="IBatchedQuery" />, this is parity rather than speed:
///         there is no round trip to save on an embedded database.
///     </para>
///     <para>
///         The <c>&lt;T, TId&gt;</c> forms take a stream id, a strong-typed wrapper around one, or a natural key
///         the aggregate owns or maps (<c>[MappedNaturalKey&lt;T&gt;]</c>), exactly as the session's own
///         overloads do. A fetch for exclusive writing takes the database's write lock when the batch runs, and
///         the session holds it until it commits.
///     </para>
/// </remarks>
public interface IBatchEvents
{
    /// <summary>The state of a stream, or null when it does not exist.</summary>
    Task<StreamState?> FetchStreamState(Guid streamId);

    /// <inheritdoc cref="FetchStreamState(Guid)" />
    Task<StreamState?> FetchStreamState(string streamKey);

    /// <summary>The events of a stream, optionally bounded by version or time.</summary>
    Task<IReadOnlyList<IEvent>> FetchStream(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        long fromVersion = 0);

    /// <inheritdoc cref="FetchStream(Guid,long,DateTimeOffset?,long)" />
    Task<IReadOnlyList<IEvent>> FetchStream(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        long fromVersion = 0);

    /// <summary>Fetch an aggregate for writing, with the optimistic version guard.</summary>
    Task<IEventStream<T>> FetchForWriting<T>(Guid id) where T : class;

    /// <inheritdoc cref="FetchForWriting{T}(Guid)" />
    Task<IEventStream<T>> FetchForWriting<T>(string key) where T : class;

    /// <summary>
    ///     Fetch an aggregate for writing, failing with a <c>ConcurrencyException</c> when the stream is not at
    ///     <paramref name="expectedVersion" />.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion) where T : class;

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long)" />
    Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion) where T : class;

    /// <summary>
    ///     Fetch an aggregate for writing under the database's write lock, which the session holds until it
    ///     commits.
    /// </summary>
    Task<IEventStream<T>> FetchForExclusiveWriting<T>(Guid id) where T : class;

    /// <inheritdoc cref="FetchForExclusiveWriting{T}(Guid)" />
    Task<IEventStream<T>> FetchForExclusiveWriting<T>(string key) where T : class;

    /// <summary>The current state of an aggregate, or null.</summary>
    Task<T?> FetchLatest<T>(Guid id) where T : class;

    /// <inheritdoc cref="FetchLatest{T}(Guid)" />
    Task<T?> FetchLatest<T>(string id) where T : class;

    /// <summary>
    ///     Fetch an aggregate for writing by a stream id, a strong-typed identifier, or a natural key.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T, TId>(TId id) where T : class where TId : notnull;

    /// <summary>
    ///     Fetch an aggregate for writing by a stream id, a strong-typed identifier, or a natural key, failing
    ///     with a <c>ConcurrencyException</c> when the stream is not at <paramref name="expectedVersion" />.
    /// </summary>
    Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, long expectedVersion)
        where T : class where TId : notnull;

    /// <summary>
    ///     Fetch an aggregate for exclusive writing by a stream id, a strong-typed identifier, or a natural key.
    /// </summary>
    Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id) where T : class where TId : notnull;

    /// <summary>
    ///     The current state of an aggregate by a stream id, a strong-typed identifier, or a natural key; null
    ///     when nothing is found, and for a null key.
    /// </summary>
    Task<T?> FetchLatest<T, TId>(TId id) where T : class where TId : notnull;

    /// <inheritdoc cref="IBatchedQuery.EventsExist" />
    Task<bool> EventsExist(EventTagQuery query);

    /// <inheritdoc cref="IBatchedQuery.FetchForWritingByTags{T}" />
    Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query) where T : class;
}

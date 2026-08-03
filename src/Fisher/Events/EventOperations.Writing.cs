using Fisher.Exceptions;
using Fisher.Storage;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Events;

/// <summary>
///     The read-then-write half of the event store surface: appends that check the stream's current
///     version first, and the <c>FetchForWriting</c> / <c>WriteToAggregate</c> workflow built on top
///     of it.
/// </summary>
public partial class EventOperations
{
    // ---- AppendOptimistic ----

    /// <summary>
    ///     Append to an existing stream, guarding on the version the stream has right now.
    /// </summary>
    /// <remarks>
    ///     The version is read immediately and carried as the expected version on the write, so a
    ///     concurrent append committing in between makes <c>SaveChangesAsync</c> fail rather than
    ///     silently interleaving.
    /// </remarks>
    /// <exception cref="NonExistentStreamException">The stream does not exist.</exception>
    public async Task AppendOptimistic(Guid streamId, CancellationToken token, params object[] events)
    {
        AssertGuidIdentity();
        var version = await RequireStreamVersionAsync(streamId, token).ConfigureAwait(false);
        Append(streamId, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="AppendOptimistic(Guid,CancellationToken,object[])" />
    public Task AppendOptimistic(Guid streamId, params object[] events)
        => AppendOptimistic(streamId, CancellationToken.None, events);

    /// <inheritdoc cref="AppendOptimistic(Guid,CancellationToken,object[])" />
    public async Task AppendOptimistic(string streamKey, CancellationToken token, params object[] events)
    {
        AssertStringIdentity();
        var version = await RequireStreamVersionAsync(streamKey, token).ConfigureAwait(false);
        Append(streamKey, events).ExpectedVersionOnServer = version;
    }

    /// <inheritdoc cref="AppendOptimistic(Guid,CancellationToken,object[])" />
    public Task AppendOptimistic(string streamKey, params object[] events)
        => AppendOptimistic(streamKey, CancellationToken.None, events);

    // ---- AppendExclusive ----

    /// <summary>
    ///     Append to an existing stream with exclusive access to it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This behaves as <see cref="AppendOptimistic(Guid,CancellationToken,object[])" /> on
    ///         SQLite.</b> Marten and Polecat take a row lock here (<c>UPDLOCK, HOLDLOCK</c> /
    ///         advisory lock) so a competing session <em>waits</em>. SQLite has no row locks and only
    ///         one writer per database file; the equivalent would be holding a <c>BEGIN IMMEDIATE</c>
    ///         write transaction open from this call until <c>SaveChangesAsync</c>, which would block
    ///         every other writer in the process for as long as the caller holds the session.
    ///     </para>
    ///     <para>
    ///         The safety property is the same either way — no lost update, because the version guard
    ///         still runs inside the write transaction. What differs is the behaviour under contention:
    ///         a loser here fails with a concurrency exception instead of waiting its turn.
    ///     </para>
    /// </remarks>
    public Task AppendExclusive(Guid streamId, CancellationToken token, params object[] events)
        => AppendOptimistic(streamId, token, events);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task AppendExclusive(Guid streamId, params object[] events)
        => AppendOptimistic(streamId, CancellationToken.None, events);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task AppendExclusive(string streamKey, CancellationToken token, params object[] events)
        => AppendOptimistic(streamKey, token, events);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task AppendExclusive(string streamKey, params object[] events)
        => AppendOptimistic(streamKey, CancellationToken.None, events);

    // ---- FetchForWriting ----

    /// <summary>
    ///     Fetch a stream's aggregate together with an append surface for it, guarded on the version
    ///     the stream has right now.
    /// </summary>
    /// <remarks>
    ///     The aggregate is rebuilt by live aggregation on every call — Fisher has no snapshot storage
    ///     to read instead. A stream that does not exist yet comes back with a null
    ///     <see cref="IEventStream{T}.Aggregate" /> and an append surface that will start it.
    /// </remarks>
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class
    {
        AssertGuidIdentity();
        return FetchForWritingAsync<T>(id, null, cancellation);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,CancellationToken)" />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, CancellationToken cancellation = default)
        where T : class
    {
        AssertStringIdentity();
        return FetchForWritingAsync<T>(key, null, cancellation);
    }

    /// <summary>
    ///     Fetch a stream for writing, asserting up front that it is at
    ///     <paramref name="expectedVersion" />.
    /// </summary>
    /// <exception cref="EventStreamUnexpectedMaxEventIdException">The stream is at a different version.</exception>
    public Task<IEventStream<T>> FetchForWriting<T>(Guid id, long expectedVersion,
        CancellationToken cancellation = default) where T : class
    {
        AssertGuidIdentity();
        return FetchForWritingAsync<T>(id, expectedVersion, cancellation);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long,CancellationToken)" />
    public Task<IEventStream<T>> FetchForWriting<T>(string key, long expectedVersion,
        CancellationToken cancellation = default) where T : class
    {
        AssertStringIdentity();
        return FetchForWritingAsync<T>(key, expectedVersion, cancellation);
    }

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T>(Guid id, CancellationToken cancellation = default)
        where T : class
        => FetchForWriting<T>(id, cancellation);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T>(string key, CancellationToken cancellation = default)
        where T : class
        => FetchForWriting<T>(key, cancellation);

    /// <summary>
    ///     Fetch for writing by an identity whose type is not fixed by the signature.
    /// </summary>
    /// <remarks>
    ///     In Marten and Polecat this overload is the natural-key and strong-typed-id entry point.
    ///     Fisher has neither, so it accepts only an id that is already the stream identity type.
    /// </remarks>
    public Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => id switch
        {
            Guid guid => FetchForWriting<T>(guid, cancellation),
            string key => FetchForWriting<T>(key, cancellation),
            _ => throw new NotImplementedException(UnsupportedIdentityMessage(typeof(TId)))
        };

    /// <inheritdoc cref="FetchForWriting{T,TId}(TId,CancellationToken)" />
    public Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => FetchForWriting<T, TId>(id, cancellation);

    // ---- WriteToAggregate ----

    /// <summary>
    ///     Fetch a stream for writing, hand it to <paramref name="writing" />, and commit.
    /// </summary>
    public async Task WriteToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="WriteToAggregate{T}(Guid,Action{IEventStream{T}},CancellationToken)" />
    public async Task WriteToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="WriteToAggregate{T}(Guid,Action{IEventStream{T}},CancellationToken)" />
    public async Task WriteToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="WriteToAggregate{T}(Guid,Action{IEventStream{T}},CancellationToken)" />
    public async Task WriteToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long,CancellationToken)" />
    public async Task WriteToAggregate<T>(Guid id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long,CancellationToken)" />
    public async Task WriteToAggregate<T>(Guid id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long,CancellationToken)" />
    public async Task WriteToAggregate<T>(string id, int expectedVersion, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
    {
        writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false));
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchForWriting{T}(Guid,long,CancellationToken)" />
    public async Task WriteToAggregate<T>(string id, int expectedVersion, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
    {
        await writing(await FetchForWriting<T>(id, expectedVersion, cancellation).ConfigureAwait(false))
            .ConfigureAwait(false);
        await _session.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task WriteExclusivelyToAggregate<T>(string id, Action<IEventStream<T>> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task WriteExclusivelyToAggregate<T>(Guid id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    /// <inheritdoc cref="AppendExclusive(Guid,CancellationToken,object[])" />
    public Task WriteExclusivelyToAggregate<T>(string id, Func<IEventStream<T>, Task> writing,
        CancellationToken cancellation = default) where T : class
        => WriteToAggregate(id, writing, cancellation);

    // ---- FetchLatest / ProjectLatest ----

    /// <summary>
    ///     The current state of an aggregate.
    /// </summary>
    /// <remarks>
    ///     Equivalent to <c>AggregateStreamAsync</c> today. In Marten and Polecat this reads a
    ///     persisted snapshot when one is registered and only falls back to folding the stream; Fisher
    ///     has no snapshot storage yet, so it always folds. Events pending in this session are not
    ///     included — see <see cref="ProjectLatest{T}(Guid,CancellationToken)" />.
    /// </remarks>
    public async ValueTask<T?> FetchLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
        => await AggregateStreamAsync<T>(id, token: cancellation).ConfigureAwait(false);

    /// <inheritdoc cref="FetchLatest{T}(Guid,CancellationToken)" />
    public async ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class
        => await AggregateStreamAsync<T>(id, token: cancellation).ConfigureAwait(false);

    /// <inheritdoc cref="FetchForWriting{T,TId}(TId,CancellationToken)" />
    public ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => id switch
        {
            Guid guid => FetchLatest<T>(guid, cancellation),
            string key => FetchLatest<T>(key, cancellation),
            _ => throw new NotImplementedException(UnsupportedIdentityMessage(typeof(TId)))
        };

    /// <summary>
    ///     The state of an aggregate including events appended in this session but not yet committed.
    /// </summary>
    public ValueTask<T?> ProjectLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        AssertGuidIdentity();
        return ProjectLatestAsync<T>(id, cancellation);
    }

    /// <inheritdoc cref="ProjectLatest{T}(Guid,CancellationToken)" />
    public ValueTask<T?> ProjectLatest<T>(string id, CancellationToken cancellation = default) where T : class
    {
        AssertStringIdentity();
        return ProjectLatestAsync<T>(id, cancellation);
    }

    private async ValueTask<T?> ProjectLatestAsync<T>(object streamId, CancellationToken cancellation)
        where T : class
    {
        var committed = await AggregateStreamAsync<T>(streamId, 0, null, null, 0, cancellation)
            .ConfigureAwait(false);

        if (!_streams.TryGetValue(streamId, out var pending) || pending.Events.Count == 0)
        {
            return committed;
        }

        var projected = await Graph.AggregatorFor<T>()
            .BuildAsync(pending.Events, _session, committed, cancellation).ConfigureAwait(false);

        if (projected is not null)
        {
            AggregateIdentity.TrySetIdentity(projected, streamId);
        }

        return projected;
    }

    // ---- internals ----

    private async Task<IEventStream<T>> FetchForWritingAsync<T>(object streamId, long? expectedVersion,
        CancellationToken token) where T : class
    {
        var version = await ReadStreamVersionAsync(streamId, token).ConfigureAwait(false);

        if (expectedVersion.HasValue && (version ?? 0) != expectedVersion.Value)
        {
            throw new EventStreamUnexpectedMaxEventIdException(streamId, typeof(T), expectedVersion.Value,
                version ?? 0);
        }

        T? aggregate = null;
        if (version > 0)
        {
            aggregate = await AggregateStreamAsync<T>(streamId, 0, null, null, 0, token).ConfigureAwait(false);
        }

        var action = TrackForWriting(streamId, version);

        return streamId is Guid guid
            ? EventStream<T>.ForGuid(this, action, guid, aggregate, token)
            : EventStream<T>.ForString(this, action, (string)streamId, aggregate, token);
    }

    /// <summary>
    ///     The <see cref="StreamAction" /> this fetch should append into, tracked in the unit of work.
    /// </summary>
    /// <remarks>
    ///     A stream already touched in this session keeps its existing action rather than getting a
    ///     fresh one. Fisher tracks streams in a dictionary keyed by identity — replacing the entry
    ///     would drop events an earlier <c>Append</c> or <c>FetchForWriting</c> had already queued
    ///     against the same stream.
    /// </remarks>
    private StreamAction TrackForWriting(object streamId, long? version)
    {
        if (_streams.TryGetValue(streamId, out var existing))
        {
            existing.ExpectedVersionOnServer ??= version ?? 0;
            return existing;
        }

        var action = streamId is Guid guid
            ? new StreamAction(guid, version.HasValue ? StreamActionType.Append : StreamActionType.Start)
            : new StreamAction((string)streamId, version.HasValue ? StreamActionType.Append : StreamActionType.Start);

        action.ExpectedVersionOnServer = version ?? 0;
        action.TenantId = _session.TenantId;

        return Track(streamId, action);
    }

    /// <summary>
    ///     The current version of a stream, or null when it does not exist.
    /// </summary>
    private async Task<long?> ReadStreamVersionAsync(object streamId, CancellationToken token)
    {
        var sql = $"select version from {Graph.StreamsTableName} where id = @stream_id";

        if (IsConjoined)
        {
            sql += " and tenant_id = @tenant_id";
        }

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _session.Options.CommandTimeout;

        BindStreamId(command, streamId);

        if (IsConjoined)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", _session.TenantId)
            {
                SqliteType = SqliteType.Text
            });
        }

        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private async Task<long> RequireStreamVersionAsync(object streamId, CancellationToken token)
        => await ReadStreamVersionAsync(streamId, token).ConfigureAwait(false)
           ?? throw new NonExistentStreamException(streamId);

    private static string UnsupportedIdentityMessage(Type idType)
        => $"Fisher cannot fetch a stream by an identity of type '{idType.Name}'. Only the configured " +
           "stream identity type is supported — natural keys and strongly typed ids are not implemented yet.";
}

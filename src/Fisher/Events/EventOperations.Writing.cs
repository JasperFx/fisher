using Fisher.Exceptions;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Projections;
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
    ///     <para>
    ///         The aggregate is rebuilt by live aggregation on every call. A stream that does not exist
    ///         yet comes back with a null <see cref="IEventStream{T}.Aggregate" /> and an append surface
    ///         that will start it.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately still a fold, where <c>FetchLatest</c> reads an Inline aggregate's
    ///         document</b> (fisher#88). Polecat scoped its equivalent fix the same way. The two are
    ///         asking different questions: <c>FetchLatest</c> reports current state, while this is the
    ///         read half of a read-modify-write whose guard is the stream's version — so folding the
    ///         stream is what the version it returns has to agree with.
    ///     </para>
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
    ///     Fetch for writing by an identity whose type is not fixed by the signature — a stream
    ///     identity, or a natural key.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In Marten and Polecat this overload is the natural-key and strong-typed-id entry point,
    ///         and it is both here too now: fisher#14 closed the strong-typed half and fisher#40 the
    ///         natural-key one, so nothing on <c>IEventStoreOperations</c> is partial any more.
    ///     </para>
    ///     <para>
    ///         <b>The stream identity type wins when the two coincide.</b> A string natural key on a
    ///         store with string stream identity is ambiguous by construction, and reading it as the
    ///         stream key is the interpretation that does not depend on which aggregate types happen to
    ///         declare a key. <c>FetchForWritingByNaturalKey</c> is the unambiguous spelling.
    ///     </para>
    /// </remarks>
    public Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => id switch
        {
            Guid guid when Graph.StreamIdentity == StreamIdentity.AsGuid
                => FetchForWriting<T>(guid, cancellation),
            string key when Graph.StreamIdentity == StreamIdentity.AsString
                => FetchForWriting<T>(key, cancellation),
            _ when NaturalKeyFor<T>() is not null => FetchForWritingByNaturalKey<T, TId>(id, cancellation),
            Guid guid => FetchForWriting<T>(guid, cancellation),
            string key => FetchForWriting<T>(key, cancellation),
            _ => throw new NotImplementedException(UnsupportedIdentityMessage(typeof(TId)))
        };

    /// <summary>
    ///     Fetch a stream for writing by the aggregate's natural key — the business identifier it was
    ///     created with, rather than its stream id (fisher#40).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The key is resolved through <c>fi_natural_key_&lt;alias&gt;</c> and the fetch then goes
    ///         through the ordinary <c>FetchForWriting</c> flow, so the optimistic version guard, the
    ///         aggregation and the tracked <c>StreamAction</c> are all the same ones. Two statements
    ///         rather than Polecat's one, because its single round trip exists to take a row lock in
    ///         the same breath and Fisher has no row lock to take.
    ///     </para>
    ///     <para>
    ///         A key naming no live stream throws <see cref="Exceptions.UnknownNaturalKeyException" />
    ///         rather than handing back an empty stream to append to. The mapping row is written by the
    ///         <c>StartStream</c> that created the stream, so "no such key" means the stream was never
    ///         started — appending to a stream id invented here would create one the key does not name.
    ///     </para>
    /// </remarks>
    public async Task<IEventStream<T>> FetchForWritingByNaturalKey<T, TId>(TId key,
        CancellationToken cancellation = default) where T : class where TId : notnull
    {
        var streamId = await ResolveNaturalKeyAsync<T, TId>(key, cancellation).ConfigureAwait(false);

        return streamId switch
        {
            Guid guid => await FetchForWriting<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchForWriting<T>((string)streamId, cancellation).ConfigureAwait(false)
        };
    }

    /// <inheritdoc cref="FetchForWritingByNaturalKey{T,TId}" />
    public async ValueTask<T?> FetchLatestByNaturalKey<T, TId>(TId key,
        CancellationToken cancellation = default) where T : class where TId : notnull
    {
        var streamId = await ResolveNaturalKeyAsync<T, TId>(key, cancellation).ConfigureAwait(false);

        return streamId switch
        {
            Guid guid => await FetchLatest<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchLatest<T>((string)streamId, cancellation).ConfigureAwait(false)
        };
    }

    private NaturalKeyDefinition? NaturalKeyFor<T>()
        => Graph.Options.Projections.NaturalKeyFor(typeof(T));

    private async Task<object> ResolveNaturalKeyAsync<T, TId>(TId key, CancellationToken cancellation)
        where T : class where TId : notnull
    {
        var definition = NaturalKeyFor<T>()
                         ?? throw new InvalidOperationException(
                             $"'{typeof(T).Name}' declares no natural key. Mark the aggregate's "
                             + "identifying member with [NaturalKey] and register the projection, or "
                             + "fetch by stream id.");

        var unwrapped = definition.Unwrap(key)
                        ?? throw new ArgumentNullException(nameof(key), "A natural key cannot be null.");

        var connection = await _session.ConnectionAsync(cancellation).ConfigureAwait(false);

        return await new Storage.NaturalKeyLookup(Graph)
                   .ResolveAsync(definition, unwrapped, TenantId, connection, cancellation)
                   .ConfigureAwait(false)
               ?? throw new Exceptions.UnknownNaturalKeyException(typeof(T), unwrapped);
    }

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
    ///     An <c>Inline</c>-projected aggregate is read from its projected document; anything else
    ///     folds the stream through <c>AggregateStreamAsync</c>. See
    ///     <see cref="CanReadInlineDocument{T}" />. Events pending in this session are not included —
    ///     see <see cref="ProjectLatest{T}(Guid,CancellationToken)" />.
    /// </remarks>
    public async ValueTask<T?> FetchLatest<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        if (CanReadInlineDocument<T>(typeof(Guid)))
        {
            return await _session.LoadAsync<T>(id, cancellation).ConfigureAwait(false);
        }

        return await AggregateStreamAsync<T>(id, token: cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchLatest{T}(Guid,CancellationToken)" />
    public async ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class
    {
        if (CanReadInlineDocument<T>(typeof(string)))
        {
            return await _session.LoadAsync<T>(id, cancellation).ConfigureAwait(false);
        }

        return await AggregateStreamAsync<T>(id, token: cancellation).ConfigureAwait(false);
    }

    /// <summary>
    ///     fisher#88: is <typeparamref name="T" /> the subject of an <c>Inline</c> projection whose
    ///     projected document can be addressed by a <paramref name="keyType" />-typed identity?
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fisher used to live-aggregate the stream for <em>every</em> <c>FetchLatest&lt;T&gt;</c>,
    ///         whatever <c>T</c>'s lifecycle — the doc comment here said so, and said Fisher had no
    ///         snapshot storage to read instead, which stopped being true when
    ///         <c>Projections.Snapshot&lt;T&gt;</c> landed. Marten's fetch planner routes an Inline
    ///         aggregate to <c>FetchInlinedPlan</c>, which simply loads the projected document. The
    ///         visible difference is on a stream that exists but holds nothing <c>T</c> owns: Marten
    ///         and Polecat find no document and return <c>null</c>, where Fisher folded the foreign
    ///         events and handed back whatever the aggregator constructed. This is polecat#463, fixed
    ///         there in 5.15.0 by polecat#467, and Fisher is the same shape.
    ///     </para>
    ///     <para>
    ///         <b>A default-constructed aggregate is not neutral.</b> For a conventional
    ///         <c>Create</c>/<c>Apply</c> aggregate the old path came out null anyway, because nothing
    ///         built an instance. The shape that surfaces it is a catch-all <c>Evolve(IEvent)</c>,
    ///         which accepts every event type by construction: the aggregator built an instance, the
    ///         switch inside matched nothing, and the defaults came back as though they were state. In
    ///         the reported case a <c>bool IsActive</c> defaulting to <c>true</c> made the phantom read
    ///         as an <em>active alert</em> for a service that had none. Since
    ///         <c>FetchLatest&lt;T&gt;(id) is null</c> is the idiomatic "does this aggregate exist?"
    ///         probe, that probe was satisfied by any stream id holding events at all.
    ///     </para>
    ///     <para>
    ///         Reading the document is also what the write side already believed: the inline projection
    ///         screens out streams it does not own, which is exactly why no row was ever written for
    ///         them. The two halves now agree.
    ///     </para>
    ///     <para>
    ///         <b>Inline only</b>, mirroring JasperFx's <c>InlineFetchPlanner</c>. A Live aggregate has
    ///         no document to read, and Marten routes an Async one to the document only when the
    ///         mapping is revisioned.
    ///     </para>
    ///     <para>
    ///         <b>The mapping gate is what keeps an externally-stored projection on the old path.</b>
    ///         A type registered with <c>Projections.StorageProviders</c> — an EF Core-backed
    ///         projection — is deliberately never mapped, so its rows are in no <c>fi_doc_*</c> table
    ///         and <c>LoadAsync</c> would answer about a table nothing writes to.
    ///         <c>HasMappingFor</c> is the same question the storage registry itself is constructed
    ///         with, and it does not create a mapping the way <c>MappingFor</c> would.
    ///     </para>
    ///     <para>
    ///         <b>And the key type has to match, because a stream identity and a document identity are
    ///         not always the same type.</b> A natural key resolves to a stream <em>key</em> (string)
    ///         for an aggregate whose document id is a Guid, and that key cannot address the document
    ///         at all; those fall back to live aggregation exactly as before.
    ///     </para>
    ///     <para>
    ///         <b><c>IdType</c> rather than <c>StoredIdType</c>, which is where Polecat's equivalent
    ///         does not port.</b> Polecat compares the <em>inner</em> type so a strong-typed id matches
    ///         on the value it wraps, because its load path re-wraps on the way through. Fisher's does
    ///         not: <c>LoadAsync&lt;T&gt;(Guid)</c> resolves storage by hard-casting to
    ///         <c>IDocumentStorage&lt;T, Guid&gt;</c>, and a strong-typed aggregate's storage is keyed
    ///         on the wrapper — so unwrapping here passes the gate and then throws
    ///         <c>InvalidCastException</c> from inside the load. Comparing <c>IdType</c> leaves a
    ///         strong-typed aggregate folding the stream, exactly as it did before this change, so the
    ///         phantom survives only for that shape. <c>StrongTypedIdentityCompliance</c> is what
    ///         caught it, and widening this is what fisher#89's <c>LoadAsync&lt;T&gt;(object)</c> —
    ///         an entry point that resolves a canonical and a wrapped identity alike — exists to make
    ///         possible.
    ///     </para>
    /// </remarks>
    private bool CanReadInlineDocument<T>(Type keyType) where T : class
        => Graph.Options.Projections.TryFindAggregate(typeof(T), out var projection)
           && projection.Lifecycle == ProjectionLifecycle.Inline
           && Graph.Options.Schema.HasMappingFor(typeof(T))
           && Graph.Options.Schema.MappingFor(typeof(T)).IdType == keyType;

    /// <inheritdoc cref="FetchForWriting{T,TId}(TId,CancellationToken)" />
    /// <inheritdoc cref="FetchForWriting{T,TId}(TId,CancellationToken)" />
    public ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => id switch
        {
            Guid guid when Graph.StreamIdentity == StreamIdentity.AsGuid => FetchLatest<T>(guid, cancellation),
            string key when Graph.StreamIdentity == StreamIdentity.AsString => FetchLatest<T>(key, cancellation),
            _ when NaturalKeyFor<T>() is not null => FetchLatestByNaturalKey<T, TId>(id, cancellation),
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
        action.TenantId = TenantId;

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
            command.Parameters.Add(new SqliteParameter("tenant_id", TenantId)
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

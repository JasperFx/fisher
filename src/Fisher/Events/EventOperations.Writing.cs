using Fisher.Exceptions;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Fetching;
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
    /// <summary>
    ///     Aggregates fetched for writing in this unit of work, waiting to be written back to the
    ///     <see cref="IAggregateWriteCache" /> once it commits. Null until a type that opted in is
    ///     fetched, which is the overwhelmingly common case.
    /// </summary>
    private List<(IAggregateWriteCache Cache, AggregateCacheKey Key, object Aggregate, long Version)>?
        _pendingCacheWrites;

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

    /// <summary>
    ///     Read the current state of the aggregate a natural key names, or null if the key names no
    ///     live stream.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A miss is null here, where <see cref="FetchForWritingByNaturalKey{T,TId}" /> throws,
    ///         and the asymmetry is the contract rather than an oversight</b> (jasperfx#764). The two
    ///         methods ask different questions: <c>FetchForWriting</c> is the read half of a
    ///         read-modify-write and has to say what it would be writing to, where <c>FetchLatest</c>
    ///         reports current state and <c>FetchLatest(...) is null</c> is the idiomatic "does this
    ///         aggregate exist?" probe — the same probe fisher#88 made honest for the by-id overload.
    ///         Throwing made the key-shaped spelling the one member of the family that could not answer
    ///         its own question.
    ///     </para>
    ///     <para>
    ///         The <c>FetchForWriting</c> miss is deliberately <em>not</em> pinned by the shared suite,
    ///         because that is the one place the three stores genuinely disagree — Marten hands back a
    ///         null aggregate, Polecat throws <c>InvalidOperationException</c>, Fisher throws
    ///         <see cref="Exceptions.UnknownNaturalKeyException" />. This one they agree on, and Fisher
    ///         was the odd one out until the suite ran.
    ///     </para>
    /// </remarks>
    public async ValueTask<T?> FetchLatestByNaturalKey<T, TId>(TId key,
        CancellationToken cancellation = default) where T : class where TId : notnull
    {
        var streamId = await TryResolveNaturalKeyAsync<T, TId>(key, cancellation).ConfigureAwait(false);

        return streamId switch
        {
            null => null,
            Guid guid => await FetchLatest<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchLatest<T>((string)streamId, cancellation).ConfigureAwait(false)
        };
    }

    private NaturalKeyDefinition? NaturalKeyFor<T>()
        => Graph.Options.Projections.NaturalKeyFor(typeof(T));

    /// <inheritdoc cref="TryResolveNaturalKeyAsync{T,TId}" />
    private async Task<object> ResolveNaturalKeyAsync<T, TId>(TId key, CancellationToken cancellation)
        where T : class where TId : notnull
        => await TryResolveNaturalKeyAsync<T, TId>(key, cancellation).ConfigureAwait(false)
           ?? throw new Exceptions.UnknownNaturalKeyException(typeof(T),
               NaturalKeyFor<T>()!.Unwrap(key)!);

    /// <summary>
    ///     Resolve a natural key to the stream identity it names, or null if it names no live stream.
    /// </summary>
    /// <remarks>
    ///     The "or null" half is what lets <see cref="FetchLatestByNaturalKey{T,TId}" /> answer a miss
    ///     with null while <see cref="FetchForWritingByNaturalKey{T,TId}" /> keeps throwing — see that
    ///     method's remarks for why the two differ. An aggregate declaring no key at all, and a null
    ///     key, are configuration errors on both paths and still throw here.
    /// </remarks>
    private async Task<object?> TryResolveNaturalKeyAsync<T, TId>(TId key, CancellationToken cancellation)
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
            .ConfigureAwait(false);
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
            return await _session.LoadAsync<T>((object)id, cancellation).ConfigureAwait(false);
        }

        return await AggregateStreamAsync<T>(id, token: cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FetchLatest{T}(Guid,CancellationToken)" />
    public async ValueTask<T?> FetchLatest<T>(string id, CancellationToken cancellation = default) where T : class
    {
        if (CanReadInlineDocument<T>(typeof(string)))
        {
            return await _session.LoadAsync<T>((object)id, cancellation).ConfigureAwait(false);
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
    ///         <b><c>StoredIdType</c>, so a strong-typed aggregate is covered too</b> — it compares the
    ///         wrapper's <em>inner</em> type against the stream key, and the load then re-wraps.
    ///         <b>That only became safe with fisher#89.</b> Before it, the raw value went to
    ///         <c>LoadAsync&lt;T&gt;(Guid)</c>, which resolves storage by hard-casting to
    ///         <c>IDocumentStorage&lt;T, Guid&gt;</c> while a strong-typed aggregate's storage is keyed
    ///         on the wrapper — so unwrapping passed the gate and then threw
    ///         <c>InvalidCastException</c> from inside the load, which is what
    ///         <c>StrongTypedIdentityCompliance</c> caught. The identity is now handed to
    ///         <c>LoadAsync&lt;T&gt;(object)</c>, which resolves a canonical and a wrapped identity
    ///         alike, so the phantom is closed for every aggregate shape rather than all but one.
    ///     </para>
    /// </remarks>
    private bool CanReadInlineDocument<T>(Type keyType) where T : class
        => Graph.Options.Projections.TryFindAggregate(typeof(T), out var projection)
           && projection.Lifecycle == ProjectionLifecycle.Inline
           && Graph.Options.Schema.HasMappingFor(typeof(T))
           && Graph.Options.Schema.MappingFor(typeof(T)).StoredIdType == keyType;

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
            aggregate = await AggregateForWritingAsync<T>(streamId, version.Value, token).ConfigureAwait(false);
        }

        var action = TrackForWriting(streamId, version);

        return streamId is Guid guid
            ? EventStream<T>.ForGuid(this, action, guid, aggregate, token)
            : EventStream<T>.ForString(this, action, (string)streamId, aggregate, token);
    }

    /// <summary>
    ///     The aggregate a <c>FetchForWriting</c> hands back, folded onto a cached baseline where one is
    ///     available (fisher#97 / jasperfx#674).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The cached snapshot is a baseline and nothing more.</b> The stream's version has
    ///         already been read by the caller above, and it is read on every call whether the cache hit
    ///         or missed; the optimistic guard still runs inside the write transaction. So a stale entry
    ///         costs a larger delta fold — never a wrong aggregate, and never a suppressed concurrency
    ///         failure. The "trusted" variant that also skips the version read is retired upstream and
    ///         must not be reintroduced here.
    ///     </para>
    ///     <para>
    ///         <b>What it removes is bigger on Fisher than on the siblings, and for a structural
    ///         reason.</b> Marten and Polecat load a stored snapshot and fold the events after it, so
    ///         the cache removes a document read. Fisher's <c>FetchForWriting</c> deliberately folds the
    ///         whole stream on every call — see the remarks on <c>FetchForWriting</c> for why it does
    ///         not read the Inline document the way <c>FetchLatest</c> does — so what a hit removes here
    ///         is the fold of the stream's entire history, leaving only the events after the baseline.
    ///         The measurements behind jasperfx#674 are PostgreSQL round trips and do not transfer;
    ///         this is a different saving on a different axis.
    ///     </para>
    ///     <para>
    ///         <b>No enabled/disabled branch.</b> <c>ResolveCache(Type)</c> hands back
    ///         <c>NulloAggregateWriteCache</c> for a type nobody enrolled, so an unenrolled aggregate
    ///         takes exactly the path it took before: every take misses and every store is dropped.
    ///         Resolved per call rather than once, because Fisher has no fetch-plan object to hang it on
    ///         — it is a hash-set probe under a lock, next to two SQL statements.
    ///     </para>
    ///     <para>
    ///         <b>A baseline ahead of the stream heals on this call.</b> That is what a restore or a
    ///         rollback leaves behind, and a negative delta cannot be folded — so the entry is dropped
    ///         (<c>TryTake</c> has already removed it) and the fetch redone from nothing, which then
    ///         writes the correct baseline back.
    ///     </para>
    /// </remarks>
    private async Task<T?> AggregateForWritingAsync<T>(object streamId, long version, CancellationToken token)
        where T : class
    {
        var cache = Graph.AggregateWriteCaching.ResolveCache(typeof(T));
        var key = AggregateCacheKeyFor<T>(streamId);

        // Take-on-read is a contract requirement rather than an implementation detail: the fold below
        // mutates the instance it is handed, so exactly one caller may ever win an entry. A loser
        // simply misses and takes the uncached path, which is always correct.
        var aggregate = cache.TryTake(key, out var claimed, out var baseline) && claimed is T typed
                        && baseline > 0 && baseline <= version
            ? await AggregateStreamAsync<T>(streamId, 0, null, typed, baseline + 1, token).ConfigureAwait(false)
            : await AggregateStreamAsync<T>(streamId, 0, null, null, 0, token).ConfigureAwait(false);

        if (aggregate is not null)
        {
            RecordAggregateCacheWriteBack(cache, key, aggregate, version);
        }

        return aggregate;
    }

    /// <summary>
    ///     The cache key for one aggregate of one stream.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The database identifier carries the logical store as well as the file</b>, which is a
    ///         Fisher-shaped widening of what the shared key means by "database". Under
    ///         database-per-tenant the identifier already distinguishes two files holding the same
    ///         stream id; within one file, two logical stores are separated by the table prefix rather
    ///         than by a schema, so folding <c>DatabaseSchemaName</c> in is what stops them colliding
    ///         when they are handed the same cache instance. <see cref="AggregateWriteCacheOptions" />
    ///         names that collision as the one its own key cannot close.
    ///     </para>
    ///     <para>
    ///         <b>The tenant is always the session's, never <c>GlobalTenant</c>.</b> Fisher has no
    ///         aggregate registered as global, so claiming one would be a wider key than the store can
    ///         justify: under conjoined tenancy two tenants' streams share an id space and must not
    ///         share an entry. Where a store is single-tenanted every session resolves the same tenant
    ///         id anyway, so nothing is lost.
    ///     </para>
    /// </remarks>
    private AggregateCacheKey AggregateCacheKeyFor<T>(object streamId) where T : class
        => new(typeof(T), $"{_session.FisherDatabase.Identifier}/{_session.Options.DatabaseSchemaName}",
            TenantId, streamId);

    /// <summary>
    ///     Hold a fetched aggregate to be written back to the cache once the unit of work has been
    ///     written.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nothing is stored at fetch time, and the reason is take-on-read rather than
    ///         poisoning.</b> An entry written while the caller still holds the instance can be claimed
    ///         by a second session, which folds <em>its</em> delta onto the very object the first
    ///         caller is still reading — the aggregate would silently gain state nobody in that session
    ///         appended. Since the whole subject of this feature is that caching is unobservable except
    ///         in latency, that is disqualifying. Deferring to the end of the unit of work is also what
    ///         Marten does.
    ///     </para>
    ///     <para>
    ///         <b>The version stored is the one read <em>before</em> this unit of work appended
    ///         anything</b>, which is what makes a failed commit a non-event here: the baseline
    ///         describes committed state that existed either way, so there is no poisoned entry to
    ///         compensate for and no eviction to arrange. It also means the entry is behind the stream
    ///         by whatever this session appended, which is exactly the case
    ///         <c>AggregateForWritingAsync</c> exists to fold — and it is the honest label, since
    ///         nothing applies this session's events to the instance handed out (see
    ///         <c>aggregate_write_cache.the_inline_projection_leaves_the_fetched_aggregate_alone</c>,
    ///         which pins the premise).
    ///     </para>
    ///     <para>
    ///         A fetch that never commits therefore leaves no entry, having consumed the one it claimed.
    ///         That is the contract's own expectation — an implementation is free to evict whenever it
    ///         likes, and dropping an entry is always sound.
    ///     </para>
    /// </remarks>
    private void RecordAggregateCacheWriteBack(IAggregateWriteCache cache, AggregateCacheKey key,
        object aggregate, long version)
    {
        // The unenrolled path allocates nothing: ResolveCache hands back the nullo cache for a type
        // nobody opted in, and storing into it would be a list of work to discard later.
        if (cache is NulloAggregateWriteCache)
        {
            return;
        }

        (_pendingCacheWrites ??= []).Add((cache, key, aggregate, version));
    }

    /// <summary>
    ///     Write every aggregate fetched for writing in this unit of work back to its cache.
    /// </summary>
    /// <remarks>
    ///     Called by <c>FisherSession.SaveChangesAsync</c> once the write has succeeded, and outside
    ///     the resilience pipeline — a retried <c>SQLITE_BUSY</c> re-executes the whole write delegate,
    ///     and this is not work that should happen once per attempt. Nothing here can fail in a way the
    ///     caller should hear about: a cache is free to drop anything it is given.
    /// </remarks>
    internal void FlushAggregateCacheWriteBacks()
    {
        if (_pendingCacheWrites is null)
        {
            return;
        }

        foreach (var (cache, key, aggregate, version) in _pendingCacheWrites)
        {
            cache.Store(key, aggregate, version);
        }

        _pendingCacheWrites.Clear();
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
           ?? throw new Fisher.Exceptions.NonExistentStreamException(streamId);

    private static string UnsupportedIdentityMessage(Type idType)
        => $"Fisher cannot fetch a stream by an identity of type '{idType.Name}'. Only the configured " +
           "stream identity type is supported — natural keys and strongly typed ids are not implemented yet.";
}

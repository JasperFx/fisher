using System.Diagnostics.CodeAnalysis;
using Fisher.Events.Storage;
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

    /// <summary>
    ///     Fetch for writing by an identity whose type is not fixed by the signature — a stream
    ///     identity, a strong-typed wrapper around one, or a natural key the aggregate owns or maps.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In Marten and Polecat this overload is the natural-key and strong-typed-id entry point,
    ///         and it is both here too: fisher#40 closed the natural-key half, fisher#397 the strong-typed
    ///         one, and fisher#679 added mapped keys (<c>[MappedNaturalKey&lt;T&gt;]</c>), which resolve
    ///         through the lookup their source projection owns.
    ///     </para>
    ///     <para>
    ///         <b>The two never coincide.</b> A natural key of the stream identity type (a bare <c>string</c> on
    ///         a string-identity store, a <c>Guid</c> on a Guid one) is refused when the store is built
    ///         (fisher#692, as Marten 10 and Polecat 6), because a fetch by it could not be told apart from a
    ///         fetch by stream id. Fisher used to read it as the stream id.
    ///     </para>
    ///     <para>
    ///         <typeparamref name="TId" /> is never <c>Nullable&lt;T&gt;</c>: fetch by the key type, and deal
    ///         with a null value before fetching. A null natural key is refused with
    ///         <see cref="ArgumentOutOfRangeException" />, the way an empty <c>Guid</c> stream id is.
    ///     </para>
    /// </remarks>
    public Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => FetchForWritingByIdentityAsync<T, TId>(id, null, false, cancellation);

    /// <summary>
    ///     <see cref="FetchForWriting{T,TId}(TId,CancellationToken)" />, failing at once with a
    ///     <see cref="ConcurrencyException" /> (an <see cref="EventStreamUnexpectedMaxEventIdException" />) when
    ///     the stream is not at <paramref name="expectedVersion" /> (fisher#688).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The version is checked before anything is enlisted in the session, as the by-id overloads do, so
    ///         a stale version leaves no stream action and no write-cache claim behind. The contract's default
    ///         checks after the fetch. The handle it returns then guards that same version when the unit of
    ///         work commits, as every fetch for writing does.
    ///     </para>
    ///     <para>
    ///         For a natural key, this is what makes a command carrying a key and a version safe to replay: a
    ///         stale command fails with the same exception, which is 409 over HTTP in Wolverine.
    ///     </para>
    /// </remarks>
    public Task<IEventStream<T>> FetchForWriting<T, TId>(TId id, long expectedVersion,
        CancellationToken cancellation = default) where T : class where TId : notnull
        => FetchForWritingByIdentityAsync<T, TId>(id, expectedVersion, false, cancellation);

    /// <summary>
    ///     <see cref="FetchForWriting{T,TId}(TId,CancellationToken)" />, taking the database's write lock first
    ///     and holding it until the unit of work commits or the session is disposed (fisher#689).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A competing exclusive writer waits its turn rather than failing on the version guard. For a
    ///         natural key, the key is resolved inside the lock, so it cannot move to another stream between
    ///         the lookup and the commit.
    ///     </para>
    ///     <para>
    ///         SQLite locks the file, not the stream: while this session holds the lock, every other writer on
    ///         the database waits. Readers carry on under WAL.
    ///     </para>
    /// </remarks>
    public Task<IEventStream<T>> FetchForExclusiveWriting<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
        => FetchForWritingByIdentityAsync<T, TId>(id, null, true, cancellation);

    private async Task<IEventStream<T>> FetchForWritingByIdentityAsync<T, TId>(TId id, long? expectedVersion,
        bool exclusive, CancellationToken cancellation) where T : class where TId : notnull
    {
        AssertNotANullableIdentityType<T, TId>();

        var (streamId, key) = ClassifyIdentity<T, TId>(id);

        if (key is not null)
        {
            var raw = RequireNaturalKeyValue(key, id);

            // Inside the lock, so the key cannot be claimed by another stream between the lookup and the
            // commit.
            if (exclusive)
            {
                await _session.AcquireExclusiveWriteLockAsync(raw, typeof(T), cancellation).ConfigureAwait(false);
            }

            streamId = await ResolveNaturalKeyAsync(key, raw, cancellation).ConfigureAwait(false)
                       ?? NewStreamForUnknownKey<T>(raw, expectedVersion);
        }

        return (streamId, expectedVersion, exclusive) switch
        {
            (Guid guid, { } version, _) => await FetchForWriting<T>(guid, version, cancellation).ConfigureAwait(false),
            (string text, { } version, _) => await FetchForWriting<T>(text, version, cancellation).ConfigureAwait(false),
            (Guid guid, null, true) => await FetchForExclusiveWriting<T>(guid, cancellation).ConfigureAwait(false),
            (string text, null, true) => await FetchForExclusiveWriting<T>(text, cancellation).ConfigureAwait(false),
            (Guid guid, null, false) => await FetchForWriting<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchForWriting<T>((string)streamId!, cancellation).ConfigureAwait(false)
        };
    }

    /// <summary>
    ///     The stream id a fetch for writing by an unknown natural key starts (fisher#694).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         As Marten 10 and Polecat 6: a key naming no live stream is a new stream at version 0, the same as
    ///         a fetch by a stream id that does not exist, with a new sequential Guid (its text on a
    ///         string-identity store). The returned handle does not carry the key. The key is registered when an
    ///         appended event sets it and the session commits, through the guarded claim, so two sessions
    ///         starting one unknown key at once write two streams and the second commit fails with
    ///         <c>DuplicateNaturalKeyException</c>. Each call starts its own stream.
    ///     </para>
    ///     <para>
    ///         An expected version on an unknown key is checked against 0 before anything is enlisted: 0 starts
    ///         the stream, anything else throws <see cref="EventStreamUnexpectedMaxEventIdException" /> (a
    ///         <c>ConcurrencyException</c>) naming the key, as Marten does. Fisher 2.0.0-rc.2 threw
    ///         <see cref="Exceptions.UnknownNaturalKeyException" /> for every unknown key.
    ///     </para>
    /// </remarks>
    private object NewStreamForUnknownKey<T>(object key, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != 0)
        {
            throw new EventStreamUnexpectedMaxEventIdException(key, typeof(T), expected, 0);
        }

        var id = JasperFx.Core.SequentialGuid.NextPostgresOrder();
        return Graph.StreamIdentity == StreamIdentity.AsGuid ? id : id.ToString();
    }

    /// <summary>
    ///     Fetch a stream for writing by the aggregate's natural key — the business identifier it was
    ///     created with, rather than its stream id (fisher#40).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The key is resolved through <c>fi_natural_key_&lt;alias&gt;</c> (the source projection's, for a
    ///         mapped key) and the fetch then goes through the ordinary <c>FetchForWriting</c> flow, so the
    ///         optimistic version guard, the aggregation, the write cache and the tracked <c>StreamAction</c>
    ///         are all the same ones.
    ///     </para>
    ///     <para>
    ///         A key naming no live stream starts a new stream at version 0 (fisher#694), as Marten 10 and
    ///         Polecat 6 do and as a fetch by a stream id that does not exist does. Appending an event that
    ///         sets the key registers it on commit. See <see cref="NewStreamForUnknownKey{T}" />.
    ///     </para>
    /// </remarks>
    public async Task<IEventStream<T>> FetchForWritingByNaturalKey<T, TId>(TId key,
        CancellationToken cancellation = default) where T : class where TId : notnull
    {
        AssertNotANullableIdentityType<T, TId>();

        var definition = RequireNaturalKeyFor<T, TId>();
        var raw = RequireNaturalKeyValue(definition, key);
        var streamId = await ResolveNaturalKeyAsync(definition, raw, cancellation).ConfigureAwait(false)
                       ?? NewStreamForUnknownKey<T>(raw, null);

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
    ///         aggregate exist?" probe.
    ///     </para>
    ///     <para>
    ///         A null key is answered with null without a query, as on Marten (fisher#679).
    ///     </para>
    /// </remarks>
    public async ValueTask<T?> FetchLatestByNaturalKey<T, TId>(TId key,
        CancellationToken cancellation = default) where T : class where TId : notnull
    {
        AssertNotANullableIdentityType<T, TId>();

        var definition = RequireNaturalKeyFor<T, TId>();
        return await FetchLatestByNaturalKeyAsync<T>(definition, key, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<T?> FetchLatestByNaturalKeyAsync<T>(NaturalKeyDefinition definition, object? key,
        CancellationToken cancellation) where T : class
    {
        if (NaturalKeyValue(definition, key) is not { } raw)
        {
            return null;
        }

        var streamId = await ResolveNaturalKeyAsync(definition, raw, cancellation).ConfigureAwait(false);

        return streamId switch
        {
            null => null,
            Guid guid => await FetchLatest<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchLatest<T>((string)streamId, cancellation).ConfigureAwait(false)
        };
    }

    /// <summary>
    ///     The natural key <typeparamref name="T" /> can be fetched by with a <typeparamref name="TId" />, or
    ///     null.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A key whose declared type is <typeparamref name="TId" /> first, owned or mapped (fisher#679);
    ///         that is the unambiguous signal, and an aggregate holds at most one key of each type, which
    ///         JasperFx enforces when the store is built.
    ///     </para>
    ///     <para>
    ///         Then a key whose inner value is a bare <typeparamref name="TId" />: an <c>OrderNumber(string)</c>
    ///         key fetched with the string inside it. The owned key is preferred when several match.
    ///     </para>
    /// </remarks>
    private NaturalKeyDefinition? NaturalKeyFor<T, TId>()
    {
        var keys = Graph.Options.Projections.AllNaturalKeysFor(typeof(T));

        return keys.FirstOrDefault(x => x.OuterType == typeof(TId))
               ?? keys.FirstOrDefault(x => x.InnerType == typeof(TId));
    }

    private NaturalKeyDefinition RequireNaturalKeyFor<T, TId>()
        => NaturalKeyFor<T, TId>()
           ?? throw new InvalidOperationException(
               $"'{typeof(T).Name}' declares no natural key of type '{typeof(TId).Name}'. Mark the aggregate's "
               + "identifying member with [NaturalKey], or map one with [MappedNaturalKey<TSource>], and register "
               + "the projection; or fetch by stream id.");

    /// <summary>
    ///     The value a natural key is stored as in the lookup, or null for a null key.
    /// </summary>
    private static object? NaturalKeyValue(NaturalKeyDefinition definition, object? key)
        => key is null ? null
            : key.GetType() == definition.OuterType ? definition.Unwrap(key)
            : key;

    private static object RequireNaturalKeyValue(NaturalKeyDefinition definition, object? key)
        => NaturalKeyValue(definition, key)
           ?? throw new ArgumentOutOfRangeException("id",
               $"A natural key of type {definition.OuterType.Name} for {definition.AggregateType.Name} cannot be "
               + "null. Fetching for writing needs a key that names a stream.");

    /// <summary>
    ///     Refuse a <c>Nullable&lt;T&gt;</c> identity type argument, as Marten does (fisher#679).
    /// </summary>
    /// <remarks>
    ///     A key declared <c>TKey?</c> on the aggregate is still a <c>TKey</c> key. Fetching by <c>TKey?</c>
    ///     would leave what a null means to each overload, so a fetch always names the underlying type.
    /// </remarks>
    private static void AssertNotANullableIdentityType<T, TId>()
    {
        if (Nullable.GetUnderlyingType(typeof(TId)) is { } underlying)
        {
            throw new ArgumentException(
                $"Fisher fetches {typeof(T).Name} by {underlying.Name}, not {underlying.Name}?. Use "
                + $"{underlying.Name} as the type argument, and handle a null value before fetching.",
                "id");
        }
    }

    /// <summary>
    ///     Decide what an identity of an open type is: a stream identity (unwrapped where it is a strong-typed
    ///     wrapper) or a natural key.
    /// </summary>
    private (object? StreamId, NaturalKeyDefinition? Key) ClassifyIdentity<T, TId>(TId id) where TId : notnull
    {
        switch (id)
        {
            case Guid guid when Graph.StreamIdentity == StreamIdentity.AsGuid:
                return (guid, null);
            case string text when Graph.StreamIdentity == StreamIdentity.AsString:
                return (text, null);
        }

        if (NaturalKeyFor<T, TId>() is { } exact && exact.OuterType == typeof(TId))
        {
            return (null, exact);
        }

        if (TryUnwrapStreamIdentity(id, out var inner))
        {
            return (inner, null);
        }

        if (NaturalKeyFor<T, TId>() is { } raw)
        {
            return (null, raw);
        }

        return id switch
        {
            Guid guid => (guid, null),
            string text => (text, null),
            _ => throw new NotSupportedException(UnsupportedIdentityMessage<T>(typeof(TId)))
        };
    }

    /// <summary>
    ///     Resolve a natural key to the stream identity it names, or null if it names no live stream.
    /// </summary>
    private async Task<object?> ResolveNaturalKeyAsync(NaturalKeyDefinition definition, object raw,
        CancellationToken cancellation)
    {
        var connection = await _session.EventConnectionAsync(cancellation).ConfigureAwait(false);

        return await new NaturalKeyLookup(Graph)
            .ResolveAsync(definition, raw, TenantId, connection, _session.ActiveTransaction, cancellation)
            .ConfigureAwait(false);
    }

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

    /// <summary>
    ///     The current state of an aggregate by an identity whose type is not fixed by the signature — a
    ///     stream identity, a strong-typed wrapper around one, or a natural key the aggregate owns or maps.
    /// </summary>
    /// <remarks>
    ///     A null natural key, and one naming no live stream, are answered with null. A mapped key reads its
    ///     source projection's lookup, which the source keeps current inline, so an async read model is
    ///     reachable by key the moment the events commit (fisher#679).
    /// </remarks>
    public async ValueTask<T?> FetchLatest<T, TId>(TId id, CancellationToken cancellation = default)
        where T : class where TId : notnull
    {
        AssertNotANullableIdentityType<T, TId>();

        var (streamId, key) = ClassifyIdentity<T, TId>(id);

        if (key is not null)
        {
            return await FetchLatestByNaturalKeyAsync<T>(key, id, cancellation).ConfigureAwait(false);
        }

        return streamId switch
        {
            Guid guid => await FetchLatest<T>(guid, cancellation).ConfigureAwait(false),
            _ => await FetchLatest<T>((string)streamId!, cancellation).ConfigureAwait(false)
        };
    }

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
        var (version, isArchived) = await ReadStreamRowAsync(streamId, token).ConfigureAwait(false);

        if (expectedVersion.HasValue && (version ?? 0) != expectedVersion.Value)
        {
            throw new EventStreamUnexpectedMaxEventIdException(streamId, typeof(T), expectedVersion.Value,
                version ?? 0);
        }

        // Decided off the stream's row rather than by letting the fold find no events. A cached baseline
        // behind an archived stream would read no delta and come back labelled with the stream's version,
        // and the write-back would store it there, so the events in between would never be folded after
        // an un-archive. Leaving the cache alone also keeps a good baseline for that un-archive.
        T? aggregate = null;
        if (version > 0 && !isArchived)
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
        //
        // Bounded by the version the caller read (fisher#486). An append committing between that read and
        // this fold would otherwise land in an aggregate labelled with the older version, and the
        // write-back would store it, so the next fetch folds that event a second time.
        //
        // Archived events stay in this read. The caller has already seen a live stream, so a stream
        // archived since then must not leave a gap under the version this fold is labelled with.
        var aggregate = cache.TryTake(key, out var claimed, out var baseline) && claimed is T typed
                        && baseline > 0 && baseline <= version
            ? await AggregateStreamAsync<T>(streamId, version, null, typed, baseline + 1, token, includeArchived: true)
                .ConfigureAwait(false)
            : await AggregateStreamAsync<T>(streamId, version, null, null, 0, token, includeArchived: true)
                .ConfigureAwait(false);

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
    private async Task<(long? Version, bool IsArchived)> ReadStreamRowAsync(object streamId, CancellationToken token)
    {
        var sql = $"select version, is_archived from {Graph.StreamsTableName} where id = @stream_id";

        if (IsConjoined)
        {
            sql += " and tenant_id = @tenant_id";
        }

        var connection = await _session.EventConnectionAsync(token).ConfigureAwait(false);
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

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetInt64(1) != 0)
            : (null, false);
    }

    private async Task<long> RequireStreamVersionAsync(object streamId, CancellationToken token)
        => (await ReadStreamRowAsync(streamId, token).ConfigureAwait(false)).Version
           ?? throw new JasperFx.Events.NonExistentStreamException(streamId);

    /// <summary>
    ///     A strong-typed wrapper whose inner value is the configured stream identity type, unwrapped
    ///     (fisher#397).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An aggregate keyed on <c>PodId(Guid)</c> is the ordinary strong-typed shape, and it was
    ///         refused here as "not implemented yet" long after fisher#14 shipped strong-typed ids
    ///         everywhere else. The wrapper exists only in .NET; the stream id is the inner value.
    ///     </para>
    ///     <para>
    ///         Discovered with <see cref="StrongTypedId.TryResolve" />, the same rule identity
    ///         discovery uses, so a type is a wrapper here exactly when it is one on the document side.
    ///         A wrapper around the <em>other</em> identity type is not unwrapped, and falls through to
    ///         the refusal: converting a Guid to its string form, or the reverse, would address a stream
    ///         the caller did not name.
    ///     </para>
    /// </remarks>
    private bool TryUnwrapStreamIdentity<TId>(TId id, [NotNullWhen(true)] out object? inner) where TId : notnull
    {
        inner = null;

        if (!StrongTypedId.TryResolve(typeof(TId), out var info))
        {
            return false;
        }

        var streamIdType = Graph.StreamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);
        if (info.SimpleType != streamIdType)
        {
            return false;
        }

        inner = info.ValueProperty.GetValue(id);
        return inner is not null;
    }

    private string UnsupportedIdentityMessage<T>(Type idType)
    {
        var streamIdType = Graph.StreamIdentity == StreamIdentity.AsGuid ? "Guid" : "string";
        return $"Fisher cannot fetch a stream of {typeof(T).Name} by an identity of type '{idType.Name}'. " +
               $"This store's stream identity is {streamIdType}, so pass a {streamIdType}, a strong-typed " +
               $"wrapper around a {streamIdType}, or the natural key {typeof(T).Name} declares.";
    }
}

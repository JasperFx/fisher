using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using Fisher.Linq;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Vectors;

namespace Fisher.Projections.Vectors;

/// <summary>
///     A projection that turns events into embeddings (fisher#261) — the half of vector search that
///     <em>produces</em> a vector, where <c>VectorSearchAsync</c> (fisher#241) searches one the
///     application already put on the document.
/// </summary>
/// <remarks>
///     <para>
///         <b>The body of this is <see cref="VectorProjectionMap{TId}" /> and
///         <see cref="VectorEmbeddingPlan{TId}" />, shared with every store since jasperfx#841.</b>
///         Folding a page down to the last content per id, hashing it, comparing against the stored
///         hash and making one batched provider call are identical everywhere and were written three
///         times. What is left here is the two things only a store can do: read the current hashes,
///         and write the rows.
///     </para>
///     <para>
///         The shared map was modelled on this one, so nothing about the declaration moved:
///         <c>Map&lt;TEvent&gt;</c>'s content selector still sees the <see cref="IEvent{T}" /> wrapper
///         rather than the bare body, <typeparamref name="TId" /> is still open rather than Guid-only,
///         <c>Delete&lt;TEvent&gt;</c> still has no id-less overload, and a selector that throws is
///         still not swallowed. All four were Marten.PgVector defects Fisher declined to port, and all
///         four are now the shared behaviour.
///     </para>
///     <para>
///         <b>⚠️ It writes an ordinary Fisher document, not a table of its own.</b> That is what makes
///         the result searchable with nothing added: the document declares
///         <c>VectorIndex(x =&gt; x.Embedding, dimensions)</c> and <c>VectorSearchAsync</c> and
///         <c>HybridSearchAsync</c> read it like any other. It also means the migration, soft delete,
///         tenancy and the identity map all apply without this class knowing about any of them.
///     </para>
///     <para>
///         <b>Asynchronous only, and refused otherwise since fisher#287.</b> Embedding is a metered
///         network call per batch, and an inline projection runs inside the caller's
///         <c>SaveChangesAsync</c> — holding SQLite's single write lock open across an HTTP round trip
///         to an embedding API, which would block every other writer in the process for the duration.
///         See <see cref="ValidateConfiguration" />.
///     </para>
/// </remarks>
/// <typeparam name="TDoc">The document written. Must carry the four members of <see cref="IVectorized{TId}" />.</typeparam>
/// <typeparam name="TId">The document's identity — any type Fisher can store, including a strong-typed wrapper.</typeparam>
public abstract class VectorProjection<TDoc, TId> : IProjection, IValidatedProjection<StoreOptions>
    where TDoc : class, IVectorized<TId>, new()
    where TId : notnull
{
    private readonly IEmbeddingProvider _provider;
    private readonly VectorProjectionMap<TId> _map = new();

    protected VectorProjection(IEmbeddingProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        Configure(_map);

        if (_map.IsEmpty)
        {
            throw new InvalidOperationException(
                $"'{GetType().Name}' configured no event mappings, so it would read every page and "
                + "write nothing. Call map.Map<TEvent>(content, id) in Configure for at least one "
                + "event type.");
        }
    }

    /// <summary>Declare which events produce content, and which delete.</summary>
    protected abstract void Configure(VectorProjectionMap<TId> map);

    /// <summary>The provider's dimension count, for asserting the declared index agrees with it.</summary>
    internal int Dimensions => _provider.Dimensions;

    /// <summary>
    ///     Refuse any lifecycle but <see cref="ProjectionLifecycle.Async" />, at store construction
    ///     (fisher#287).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The class has documented itself async-only since fisher#261 and nothing enforced it, so
    ///         <c>Projections.Add(new ArticleVectors(provider), ProjectionLifecycle.Inline)</c>
    ///         succeeded and every subsequent <c>SaveChangesAsync</c> called the embedding provider
    ///         while holding SQLite's one write lock. It works on a laptop, where the model call is
    ///         quick; in production a slow provider second queues every writer in the process behind
    ///         it, and an outage fails the user's commit where it should have faulted a daemon shard
    ///         that retries.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>An ordinary <see cref="IValidatedProjection{T}" /> only because jasperfx#845
    ///         shipped.</b> A bare <see cref="IProjection" /> is registered through a
    ///         <c>ProjectionWrapper</c>, and <c>ProjectionGraph.AssertValidity</c> used to test the
    ///         sources with <c>OfType&lt;IValidatedProjection&lt;T&gt;&gt;()</c> — a wrapper is not its
    ///         inner projection's type, so this interface would never have been asked. Polecat's port
    ///         (polecat#632) had to run a pass of its own for exactly that reason. <b>The same fix
    ///         means a hand-written <c>IProjection</c> in a Fisher application that already implemented
    ///         <c>IValidatedProjection&lt;StoreOptions&gt;</c> starts being asked on this bump</b>,
    ///         which can surface configuration errors that were silently passing.
    ///     </para>
    ///     <para>
    ///         The lifecycle is read off the registration rather than held on the projection, because a
    ///         bare <see cref="IProjection" /> has nowhere to carry one — the wrapper the graph built
    ///         is what knows. <c>Live</c> is already refused by <c>ProjectionGraph.Add</c> before this
    ///         runs; what reaches here is <c>Inline</c>.
    ///     </para>
    /// </remarks>
    public IEnumerable<string> ValidateConfiguration(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var source in options.Projections.All)
        {
            if (source.Lifecycle == ProjectionLifecycle.Async) continue;

            if (!ReferenceEquals((source as IProjectionWrapper)?.InnerProjection, this)) continue;

            yield return
                $"'{source.Name}' is a VectorProjection registered {source.Lifecycle}, but a vector "
                + "projection is asynchronous only. Embedding is a metered network call, and an Inline "
                + "projection runs inside every caller's SaveChangesAsync — so a slow or unavailable "
                + "provider would hold SQLite's single write lock for a network round trip and block "
                + "every other writer in the process. Register it with ProjectionLifecycle.Async.";
        }
    }

    public async Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        AssertTheIndexAgrees(operations);

        // Last write per id wins within the page, and a delete wins over everything before it. All of
        // that is the shared plan's, including the rule that a delete does NOT suppress content
        // arriving after it in the same page.
        var plan = VectorEmbeddingPlan<TId>.Build(_map, events);

        if (plan.AggregateIds.Count > 0)
        {
            await ApplyAggregatesAsync(operations, events, plan, cancellation).ConfigureAwait(false);
        }

        foreach (var id in plan.Deletions)
        {
            // Addressed by the id the MAP produced, never by the stream id -- see VectorProjectionMap.
            operations.Delete(new TDoc { Id = id });
        }

        var writes = await plan
            .ResolveAsync(_provider, (ids, token) => ExistingHashesAsync(operations, ids, token), cancellation)
            .ConfigureAwait(false);

        foreach (var write in writes)
        {
            operations.Store(new TDoc
            {
                Id = write.Id,
                Content = write.Content,
                ContentHash = write.ContentHash,
                Embedding = write.Embedding.ToArray()
            });
        }
    }

    /// <summary>
    ///     The content hash currently stored for each of these ids.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read through the session rather than on a connection of its own</b>, which is the
    ///         first divergence from Marten's template and the one with teeth. Marten's projection
    ///         opens <c>database.CreateConnection()</c> and does its reads and writes outside the
    ///         session's transaction, so an embedding commits even when the events that produced it
    ///         roll back. Here everything is queued onto the session the projection batch hands over,
    ///         so the embedding row and the progression row land in one transaction — and on SQLite a
    ///         second connection writing while the batch holds the file's write lock would block
    ///         against itself anyway.
    ///     </para>
    ///     <para>
    ///         One query for the page rather than one per id, through the ordinary LINQ path, so the
    ///         tenant and soft-delete filters apply without being restated.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>The read uses <c>IsOneOf</c>, not <c>ids.Contains(x.Id)</c>.</b> An array receiver
    ///         binds to <c>MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T)</c>, whose span operand
    ///         cannot be unwrapped when <c>T</c> is an open type parameter — a ref struct cannot be
    ///         returned as object. <c>IsOneOf</c> is the marker operator built for this (fisher#26) and
    ///         reads the same either way.
    ///     </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<TId, string>> ExistingHashesAsync(
        IDocumentSession operations, IReadOnlyList<TId> ids, CancellationToken cancellation)
    {
        var list = ids.ToList();

        var existing = await operations.Query<TDoc>()
            .Where(x => x.Id.IsOneOf(list))
            .ToListAsync(cancellation)
            .ConfigureAwait(false);

        var hashes = new Dictionary<TId, string>();

        foreach (var document in existing)
        {
            if (document.ContentHash is { } hash)
            {
                hashes[document.Id] = hash;
            }
        }

        return hashes;
    }

    /// <summary>
    ///     Build the content of every document whose text comes from aggregate state, by live
    ///     aggregation of its stream up to the last event this page carries for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Live aggregation rather than reading a snapshot, and the shared map's remarks say
    ///         why.</b> Reading an ASYNC snapshot is the wrong answer: the daemon does not order shards
    ///         against each other, so a vector projection on one shard can see a snapshot another shard
    ///         has not caught up to, and the embedding is then silently built from stale state. An
    ///         INLINE snapshot would be sound and would mean refusing to register the projection unless
    ///         the aggregate really is projected inline — a second configuration rule, for a read that
    ///         costs one folded stream per affected stream per page either way on an embedded store.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>Folded to the page's last version for that document, not to the head of the
    ///         stream.</b> A projection replaying history must embed the state the page describes;
    ///         folding to the head would make a rebuild produce a different embedding than the original
    ///         run did, for every document whose stream has moved on since.
    ///     </para>
    ///     <para>
    ///         A stream that folds to nothing is treated as "nothing to index", the same as a content
    ///         selector returning null. Content hashing does the rest — an event that turns out not to
    ///         change the built text costs no model call at all, which is the common case precisely
    ///         when the events are partial updates.
    ///     </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification =
            "Closes AggregateStreamAsync over the aggregate type the projection's own MapFromAggregate named. Aggregate types are preserved by projection registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private async Task ApplyAggregatesAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
        VectorEmbeddingPlan<TId> plan, CancellationToken cancellation)
    {
        var aggregateType = _map.AggregateType!;
        var byGuid = ((FisherSession)operations).Options.Events.StreamIdentity == StreamIdentity.AsGuid;

        // The plan reports WHICH documents need aggregate state; which stream to fold, and how far,
        // is the store's business, so it is re-derived here from the same events. Last trigger per id
        // wins, which is the rule the plan itself applies to content.
        var streams = new Dictionary<TId, (object Identity, long Version)>();

        foreach (var @event in events)
        {
            if (_map.TryAggregateTrigger(@event, out var id))
            {
                streams[id] = (byGuid ? @event.StreamId : @event.StreamKey!, @event.Version);
            }
        }

        // Closed over a private generic method of this class rather than over AggregateStreamAsync
        // directly, so the reflection stops at "which aggregate type" — the awaiting, the Guid/string
        // branch and handing the result back to the plan are all ordinary typed code.
        var method = typeof(VectorProjection<TDoc, TId>)
            .GetMethod(nameof(FoldAndApplyAsync),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(aggregateType);

        foreach (var id in plan.AggregateIds)
        {
            var (identity, version) = streams[id];

            await ((Task)method.Invoke(this, [operations, plan, id, identity, version, cancellation])!)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ApplyAggregatesAsync" />
    private async Task FoldAndApplyAsync<TAggregate>(IDocumentSession operations,
        VectorEmbeddingPlan<TId> plan, TId id, object identity, long version, CancellationToken cancellation)
        where TAggregate : class
    {
        var aggregate = identity is Guid streamId
            ? await operations.Events
                .AggregateStreamAsync<TAggregate>(streamId, version, token: cancellation)
                .ConfigureAwait(false)
            : await operations.Events
                .AggregateStreamAsync<TAggregate>((string)identity, version, token: cancellation)
                .ConfigureAwait(false);

        plan.ApplyAggregate(id, _map, aggregate);
    }

    /// <summary>
    ///     The declared vector index has to be on <see cref="IVectorized{TId}.Embedding" />, and its
    ///     dimensions have to match the provider's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves fail silently otherwise, in the way this codebase keeps meeting. An index
    ///         declared on some other member leaves the projection writing one column and every search
    ///         reading another — valid SQL, no error, and an index that is always empty. A dimension
    ///         mismatch is worse: the rows are written, and every search refuses at the
    ///         <em>caller's</em> end with a message about the query vector, which points away from the
    ///         projection that produced them.
    ///     </para>
    ///     <para>
    ///         Checked on the first page rather than at construction because the projection is built
    ///         before the store is, so there is no schema to ask yet.
    ///     </para>
    /// </remarks>
    private void AssertTheIndexAgrees(IDocumentSession operations)
    {
        var options = ((FisherSession)operations).Options;
        var mapping = options.Schema.MappingFor(typeof(TDoc));
        var indexes = mapping.VectorIndexes;

        var index = indexes.FirstOrDefault(x => x.MemberName == nameof(IVectorized<TId>.Embedding));

        if (index is null)
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}' declares no vector index on '{nameof(IVectorized<TId>.Embedding)}', so "
                + $"'{GetType().Name}' would write a column nothing searches. Declare "
                + $"Schema.For<{typeof(TDoc).Name}>().VectorIndex(x => x.{nameof(IVectorized<TId>.Embedding)}, "
                + $"{_provider.Dimensions})"
                + (indexes.Count == 0
                    ? "."
                    : $" — it declares one on '{string.Join(", ", indexes.Select(x => x.MemberName))}' instead."));
        }

        if (index.Dimensions != _provider.Dimensions)
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}.{nameof(IVectorized<TId>.Embedding)}' was declared with "
                + $"{index.Dimensions} dimensions and {_provider.GetType().Name} produces "
                + $"{_provider.Dimensions}. The rows would be written and then refused by every "
                + "search, with a message about the caller's query vector rather than about this "
                + "projection.");
        }
    }
}

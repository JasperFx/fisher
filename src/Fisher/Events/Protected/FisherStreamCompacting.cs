using Fisher.Events.Storage;
using Fisher.Internal;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Documents;
using JasperFx.Events.Protected;

namespace Fisher.Events.Protected;

/// <summary>
///     Fisher's half of the shared <see cref="StreamCompactingExecution{TSession}" /> (jasperfx#1048): the fold,
///     the deletes, the watermark and the archiver closures. The order of the steps, the refusals and the
///     "folds to nothing leaves the stream alone" rule are the base's.
/// </summary>
/// <remarks>
///     <para>
///         <b>The fetch is outside the write transaction, and that is safe here.</b> Compacting only ever touches
///         events at or below a version it observed, and an append only ever adds above one, so the two do not
///         overlap. Two concurrent compactions of the same stream either rewrite the same row with the same
///         snapshot or find their target already deleted and update nothing. There is no lost update to
///         prevent, so there is no version guard.
///     </para>
///     <para>
///         <b>An archived stream is still compacted</b>, which the base's default read would not do: it reads
///         through the public <c>FetchStreamAsync</c>, which leaves archived events out.
///         <see cref="CompactAsync{T}" /> widens the session's stream reads for the duration, so the snapshot
///         row stays archived (the replace rewrites the body and leaves the flag alone).
///     </para>
///     <para>
///         Compacting is one-way: a projection rebuilt afterwards rebuilds from the snapshot rather than from
///         the history that produced it.
///     </para>
/// </remarks>
internal sealed class FisherStreamCompacting : StreamCompactingExecution<FisherSession>
{
    internal static readonly FisherStreamCompacting Instance = new();

    /// <summary>
    ///     Queue the compaction onto <paramref name="session" /> and commit it when there was anything to queue.
    /// </summary>
    internal static async Task CompactAsync<T>(StreamCompactingRequest<T> request, FisherSession session)
        where T : class
    {
        var queued = await session.Events
            .WithArchivedStreamReadsAsync(() => Instance.ExecuteAsync(request, session))
            .ConfigureAwait(false);

        if (queued)
        {
            await session.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        }
    }

    protected override IEventStoreOperations EventsFor(FisherSession session) => session.Events;

    protected override async ValueTask<T?> BuildAggregateAsync<T>(FisherSession session,
        IReadOnlyList<IEvent> events, CancellationToken token) where T : class
    {
        var aggregate = await session.Options.EventGraph.AggregatorFor<T>()
            .BuildAsync(events, session, null, token)
            .ConfigureAwait(false);

        // Called ahead of anything being queued, and only with the events about to be folded: the last one
        // becomes the Compacted<T>, and the rest are deleted. A stream that folds to nothing is left alone anyway.
        if (aggregate != null && events.Count > 0)
        {
            await EnsureNoAsyncShardIsBehindAsync<T>(session, events[^1], token).ConfigureAwait(false);
        }

        return aggregate;
    }

    /// <summary>
    ///     fisher#634 (port of marten#6050): refuse to compact while an async projection or subscription has not
    ///     processed the events being compacted. Compaction deletes them and rewrites the last one into a
    ///     <see cref="Compacted{T}" />, so a multi-stream projection, a composite, another view of the stream or
    ///     a subscription that had not reached them yet would never see them. A single-stream projection of
    ///     <typeparamref name="T" /> itself is exempt: it fast-forwards over the snapshot.
    /// </summary>
    /// <remarks>
    ///     A shard is behind unless its <c>fi_event_progression</c> row has reached the last event's sequence,
    ///     and a shard with no row (one that never ran) is behind. A shard's row is the one for this session's
    ///     tenant when the daemon keeps per-tenant progress, else the store-global one. Read before anything is
    ///     queued: a shard only moves forward, so one that has caught up here is still caught up at commit.
    /// </remarks>
    private static async Task EnsureNoAsyncShardIsBehindAsync<T>(FisherSession session, IEvent last,
        CancellationToken token) where T : class
    {
        var projections = session.Options.Projections;

        var exempt = projections.All
            .Where(x => x is IAggregateProjection { Scope: AggregationScope.SingleStream } aggregate &&
                        aggregate.AggregateType == typeof(T))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        var tenantId = session.TenantId;
        var shards = projections.AllShards()
            .Select(x => x.Name)
            .Where(x => !exempt.Contains(x.Name))
            // A shard partitioned to another tenant never reads this tenant's events
            .Where(x => x.TenantId is null || x.TenantId == tenantId)
            .Select(x => (Global: x.Identity,
                Tenant: x.TenantId is null
                    ? ShardName.Compose(x.Name, x.ShardKey, tenantId, x.Version).Identity
                    : x.Identity))
            .Distinct()
            .ToArray();

        if (shards.Length == 0)
        {
            return;
        }

        var progress = (await session.FisherDatabase.AllProjectionProgress(token).ConfigureAwait(false))
            .ToDictionary(x => x.ShardName, x => x.Sequence, StringComparer.Ordinal);

        var behind = shards
            .Select(x => progress.TryGetValue(x.Tenant, out var atTenant)
                ? (Name: x.Tenant, Seq: atTenant)
                : (Name: x.Global, Seq: progress.GetValueOrDefault(x.Global)))
            .Where(x => x.Seq < last.Sequence)
            .Select(x => $"{x.Name} (at {x.Seq})")
            .ToArray();

        if (behind.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot compact stream '{(object?)last.StreamKey ?? last.StreamId}' through version {last.Version} " +
                $"(sequence {last.Sequence}) yet: these async projections or subscriptions have not processed its " +
                $"events: {string.Join(", ", behind)}. Compacting deletes them, so those would never see them. " +
                "Nothing was compacted; try again once they caught up.");
        }
    }

    // IEventsArchiver<T> is invariant, so every operations type a caller could reasonably close it over is
    // accepted. One Fisher does not offer fails the compaction rather than being skipped.
    protected override async Task<bool> TryArchiveAsync<T>(FisherSession session, StreamCompactingRequest<T> request,
        IReadOnlyList<IEvent> events) where T : class
        => await TryArchiveThroughAsync<IDocumentSession, T>(session, request, events).ConfigureAwait(false)
           || await TryArchiveThroughAsync<IDocumentOperations, T>(session, request, events).ConfigureAwait(false)
           || await TryArchiveThroughAsync<IQuerySession, T>(session, request, events).ConfigureAwait(false)
           || await TryArchiveThroughAsync<IDocumentSessionOperations, T>(session, request, events).ConfigureAwait(false)
           || await TryArchiveThroughAsync<IDocumentWriteOperations, T>(session, request, events).ConfigureAwait(false)
           || await TryArchiveThroughAsync<IDocumentReadOperations, T>(session, request, events).ConfigureAwait(false);

    // Tag rows first, then the events: DeleteEventsOperation keeps the fisher#6 order.
    protected override void DeleteEvents(FisherSession session, long[] sequences)
        => session.Events.DeleteEvents(sequences);

    // The jasperfx#740 watermark, queued onto the same unit of work as the replace and the deletes, so it can
    // never disagree with the compaction it describes.
    protected override void RecordCompactionWatermark(FisherSession session, Guid? streamId, string? streamKey,
        long version)
        => session.QueueOperation(new RecordCompactionWatermarkOperation(
            session.Options.EventGraph,
            streamId?.ToString() ?? streamKey!,
            session.Events.TenantId,
            version));
}

using Fisher.Events.Storage;
using Fisher.Internal;
using JasperFx.Events;
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
        => await session.Options.EventGraph.AggregatorFor<T>()
            .BuildAsync(events, session, null, token)
            .ConfigureAwait(false);

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

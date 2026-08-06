using Fisher.Internal;
using JasperFx.Events;
using JasperFx.Events.Protected;

namespace Fisher.Events.Protected;

/// <summary>
///     Fisher's execution of <see cref="StreamCompactingRequest{T}" /> — replacing a stream's earlier
///     events with a single <see cref="Compacted{T}" /> event carrying the aggregate state at that
///     point.
/// </summary>
/// <remarks>
///     <para>
///         The request shape lives in <c>JasperFx.Events.Protected</c>; only the execution is
///         store-specific. Ported from Polecat's <c>StreamCompactingExecution</c>, which is the closest
///         template.
///     </para>
///     <para>
///         Reading back needs nothing new: JasperFx's aggregator calls
///         <see cref="Compacted{T}.MaybeFastForward" /> before folding, so a stream whose first event is
///         a <c>Compacted&lt;T&gt;</c> starts from that snapshot and applies only what follows. Live
///         aggregation, <c>FetchForWriting</c> and the projection daemon all inherit that for free.
///     </para>
/// </remarks>
internal static class StreamCompacting
{
    /// <summary>
    ///     Fetch, aggregate, replace the last event with the snapshot and delete the rest — all queued
    ///     onto <paramref name="session" />, so the replace and the deletes commit together.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fetch is outside the write transaction, and that is safe here.</b> The obvious
    ///         worry is a concurrent append between reading the stream and deleting from it, but
    ///         compacting only ever touches events at or below a version it observed, and an append only
    ///         ever adds above one — so the two do not overlap. Two concurrent compactions of the same
    ///         stream do not either: whichever commits second either rewrites the same row with the same
    ///         snapshot, or finds its target already deleted and updates nothing. That is why there is
    ///         no version guard here, unlike the append path — there is no lost update to prevent, and a
    ///         guard would only be theatre.
    ///     </para>
    ///     <para>
    ///         What is <em>not</em> safe is what the events left behind mean to anything already derived
    ///         from them. See the caveat on <see cref="OverwriteEventOperation" />: a projection past
    ///         this stream keeps what it folded, and here the events it folded are gone, so a rebuild
    ///         after compacting rebuilds from the snapshot rather than from history. That is the point
    ///         of compacting, but it is one-way.
    ///     </para>
    /// </remarks>
    internal static async Task ExecuteAsync<T>(StreamCompactingRequest<T> request, FisherSession session)
        where T : class
    {
        var events = await FetchAsync(request, session).ConfigureAwait(false);

        if (events.Count == 0)
        {
            return;
        }

        // Already compacted to exactly this point, so there is nothing to collapse. Re-writing would
        // burn a new event id and rewrite a row for no change.
        if (events is [{ Data: Compacted<T> }])
        {
            return;
        }

        var last = events[^1];

        request.Version = last.Version;
        request.Sequence = last.Sequence;

        var aggregate = await session.Options.EventGraph.AggregatorFor<T>()
            .BuildAsync(events, session, null, request.CancellationToken)
            .ConfigureAwait(false);

        if (aggregate is null)
        {
            // Every event folded to nothing — a stream deleted by its own aggregate's ShouldDelete.
            // There is no state to snapshot, so leaving the events alone is the honest answer.
            return;
        }

        // The archiver hook runs before anything destructive is queued, and is given the events that
        // are about to disappear. The lifted marker interface is non-generic so the request does not
        // have to flow a TOperations parameter; the downcast to Fisher's session type is what closes it.
        if (request.Archiver is IEventsArchiver<IDocumentSession> archiver)
        {
            await archiver.MaybeArchiveAsync(session, request, events, request.CancellationToken)
                .ConfigureAwait(false);
        }

        var compacted = new Compacted<T>(aggregate,
            request.StreamId ?? Guid.Empty, request.StreamKey ?? string.Empty);

        session.Events.CompletelyReplaceEvent(last.Sequence, compacted);

        // Everything below the snapshot. Empty when the stream was one event long, which
        // DeleteEvents already treats as a no-op.
        session.Events.DeleteEvents(events.Take(events.Count - 1).Select(x => x.Sequence).ToArray());

        await session.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The events being compacted away, bounded by whichever of version and timestamp the request
    ///     set.
    /// </summary>
    /// <remarks>
    ///     <c>Version</c> defaulting to 0 means "the latest", which is exactly what
    ///     <c>FetchStreamAsync</c>'s own default means — so the request's default passes through
    ///     untranslated.
    /// </remarks>
    private static Task<IReadOnlyList<IEvent>> FetchAsync<T>(StreamCompactingRequest<T> request,
        FisherSession session) where T : class
        => session.Options.EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? session.Events.FetchStreamAsync(request.StreamId!.Value, request.Version, request.Timestamp,
                token: request.CancellationToken)
            : session.Events.FetchStreamAsync(request.StreamKey!, request.Version, request.Timestamp,
                token: request.CancellationToken);
}

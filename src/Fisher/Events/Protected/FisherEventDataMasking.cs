using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Protected;

namespace Fisher.Events.Protected;

/// <summary>
///     Fisher's half of the shared <see cref="EventDataMaskingBase{TSession}" /> (jasperfx#1048), the GDPR-style
///     erasure surface reached through <c>DocumentStore.Advanced.ApplyEventDataMaskingAsync(...)</c>. The
///     source list, the one-session-one-commit rule, the per-(tenant, sequence) dedupe and the refusals are the
///     base's; Fisher supplies the session and the reads that include archived events.
/// </summary>
/// <remarks>
///     <b>What this does not do is reach a projection.</b> Masking rewrites <c>fi_events.data</c> below the
///     daemon's high-water mark, and the mark does not move, so a shard that has already folded the unmasked
///     body keeps whatever it derived from it until it is rebuilt. See <see cref="OverwriteEventOperation" />.
/// </remarks>
internal sealed class FisherEventDataMasking : EventDataMaskingBase<IDocumentSession>
{
    private readonly DocumentStore _store;

    internal FisherEventDataMasking(DocumentStore store)
    {
        _store = store;
    }

    protected override IDocumentSession OpenSession(string? tenantId)
        => string.IsNullOrEmpty(tenantId)
            ? _store.LightweightSession()
            : _store.LightweightSession(tenantId);

    protected override IEventStoreOperations EventsFor(IDocumentSession session) => session.Events;

    protected override Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(IDocumentSession session,
        Guid streamId, CancellationToken token)
        => session.Events.FetchStreamIncludingArchivedAsync(streamId, token: token);

    protected override Task<IReadOnlyList<IEvent>> FetchStreamIncludingArchivedAsync(IDocumentSession session,
        string streamKey, CancellationToken token)
        => session.Events.FetchStreamIncludingArchivedAsync(streamKey, token: token);

    protected override Task<IReadOnlyList<IEvent>> QueryEventsIncludingArchivedAsync(IDocumentSession session,
        Expression<Func<IEvent, bool>> filter, CancellationToken token)
        => session.Events.QueryEventsIncludingArchivedAsync(filter, token);

    // Fisher's own rule list while EventGraph still hides EventRegistry's (jasperfx#1043, a separate node);
    // this binds to whichever the graph has, so the two lifts can land in either order.
    protected override bool TryMask(IEvent @event) => _store.EventGraph.TryMask(@event);

    protected override Task SaveChangesAsync(IDocumentSession session, CancellationToken token)
        => session.SaveChangesAsync(token);
}

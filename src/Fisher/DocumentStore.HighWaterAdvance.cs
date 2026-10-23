using Fisher.Events.Daemon;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Daemon.HighWater;

namespace Fisher;

/// <summary>
///     fisher#470 / jasperfx#1031: the store-agnostic preview and advance of a high-water mark, which is
///     what lets a console such as CritterWatch jump-start a daemon on a Fisher store through
///     <see cref="IEventStore" /> rather than through <c>Advanced</c>, a store type it cannot name.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every Fisher database keeps exactly one mark</b>, so a non-null tenant is refused with
///         <see cref="ArgumentException" /> as the contract asks. That holds under all three tenancies:
///         conjoined tenants share the one global sequence of their file, and under database-per-tenant
///         the tenant is already named by <em>which</em> database was passed, so a second name would only
///         be a second chance to disagree. Fisher has no per-tenant partitioned events, the one layout
///         where a mark per tenant means something.
///     </para>
///     <para>
///         The event tables are ensured first, as the daemon ensures them before it starts (fisher#333),
///         so a console pointed at a fresh file gets an empty preview rather than <c>no such table</c>.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    async Task<IReadOnlyList<HighWaterMarkPreview>> IEventStore.PreviewHighWaterAdvanceAsync(
        IEventDatabase database, string? tenantId, CancellationToken token)
    {
        var target = HighWaterDatabaseFor(database, tenantId);

        await target.EnsureEventStorageAsync(token).ConfigureAwait(false);

        return [await HighWaterDetectorFor(target).PreviewAdvanceAsync(token).ConfigureAwait(false)];
    }

    async Task<IReadOnlyList<HighWaterMarkAdvance>> IEventStore.AdvanceHighWaterMarkToLatestAsync(
        IEventDatabase database, string? tenantId, long? expectedCurrentMark, CancellationToken token)
    {
        var target = HighWaterDatabaseFor(database, tenantId);

        return [await AdvanceHighWaterMarkAsync(target, expectedCurrentMark, token).ConfigureAwait(false)];
    }

    /// <summary>
    ///     Shared by the <see cref="IEventStore" /> member and <c>Advanced.AdvanceHighWaterMarkToLatestAsync</c>,
    ///     so the two cannot disagree about what an advance is.
    /// </summary>
    internal async Task<HighWaterMarkAdvance> AdvanceHighWaterMarkAsync(FisherDatabase database,
        long? expectedCurrentMark, CancellationToken token)
    {
        await database.EnsureEventStorageAsync(token).ConfigureAwait(false);

        return await HighWaterDetectorFor(database).AdvanceToCommittedCeilingAsync(expectedCurrentMark, token)
            .ConfigureAwait(false);
    }

    private FisherHighWaterDetector HighWaterDetectorFor(FisherDatabase database)
        => new(database, Options.EventGraph);

    private FisherDatabase HighWaterDatabaseFor(IEventDatabase database, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database is not FisherDatabase target)
        {
            throw new ArgumentException(
                $"'{database.Identifier}' is not one of this Fisher store's databases. Pass one from "
                + "IEventStore.AllDatabases().", nameof(database));
        }

        if (tenantId is not null)
        {
            throw new ArgumentException(
                $"Database '{target.Identifier}' keeps one high water mark for every tenant, because Fisher "
                + $"has no per-tenant partitioned events, so tenant '{tenantId}' has no mark of its own. Pass "
                + "a null tenant to preview or advance the database's mark.", nameof(tenantId));
        }

        return target;
    }
}

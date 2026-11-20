using Fisher.Events.Backfills;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher;

// jasperfx#1222 / fisher#643: the projection backfill seam a Wolverine job drives
public partial class DocumentStore
{
    /// <summary>
    ///     Always true: a backfill keeps its ceiling and cursor in <c>fi_event_range_marks</c>, which every Fisher
    ///     event store has. A projection declares one with <c>Backfill(name)</c> on its single-stream projection.
    /// </summary>
    bool IEventStore.SupportsBackfills => true;

    /// <summary>
    ///     Fill a new member on the documents a projection already wrote, in one database, from each tenant's cursor
    ///     up to its ceiling. See <see cref="ProjectionBackfiller" />.
    /// </summary>
    Task<IReadOnlyList<BackfillProgress>> IEventStore.RunBackfillAsync(BackfillRun run, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (source, backfill) = ProjectionBackfiller.Find(Options, run.ProjectionName, run.BackfillName);

        if (run.Database is not FisherDatabase database)
        {
            throw new ArgumentOutOfRangeException(nameof(run),
                $"{run.Database.GetType().FullName} is not a Fisher database, so this store cannot backfill its documents.");
        }

        return ProjectionBackfiller.For(Options, source, backfill).ExecuteRunAsync(database, run, token);
    }
}

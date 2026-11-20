using Fisher.Events.Rewriting;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher;

// jasperfx#1215 / fisher#643: the stored-event rewrite seam a Wolverine job drives
public partial class DocumentStore
{
    /// <summary>
    ///     Always true: the rewrite keeps its ceiling and cursor in <c>fi_event_range_marks</c>, which every Fisher
    ///     event store has.
    /// </summary>
    bool IEventStore.SupportsEventRewrites => true;

    /// <summary>
    ///     Rewrite the stored events <see cref="EventRewriteRun.Definition" /> selects, in one database, from each
    ///     tenant's cursor up to its ceiling. See <see cref="StoredEventRewriter" />.
    /// </summary>
    Task<IReadOnlyList<EventRewriteProgress>> IEventStore.RewriteStoredEventsAsync(EventRewriteRun run,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Refuses an unknown transform before anything is read
        var transforms = Options.EventGraph.FindRewriteTransforms(run.Definition);

        if (run.Database is not FisherDatabase database)
        {
            throw new ArgumentOutOfRangeException(nameof(run),
                $"{run.Database.GetType().FullName} is not a Fisher database, so this store cannot rewrite its events.");
        }

        return new StoredEventRewriter(Options, run.Definition, transforms).ExecuteRunAsync(database, run, token);
    }
}

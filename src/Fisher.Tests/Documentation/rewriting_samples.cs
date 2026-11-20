using JasperFx.Events;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind the "Rewriting stored events in bulk" section of docs/events/rewriting.md.
 *
 * See "Documentation samples" in CLAUDE.md. The behaviour is pinned by EventRewriteCompliance
 * (fisher_event_store_compliance.cs) and Events/rewriting_stored_events.cs.
 */

public record CustomerNoted(string Note);

public static class rewriting_samples
{
    public static void register_a_rewrite_transform(StoreOptions options)
    {
        #region sample_register_a_rewrite_transform
        options.Events.AddRewriteTransform<CustomerNoted>("trim-notes",
            x => x with { Note = x.Note.Trim() });
        #endregion
    }

    public static async Task rewrite_stored_events(IEventStore store, CancellationToken token)
    {
        #region sample_rewrite_stored_events
        var definition = new EventRewriteDefinition
        {
            Name = "trim-customer-notes",
            Transforms = ["trim-notes"],
            BatchSize = 500
        };

        foreach (var database in await store.AllDatabases())
        {
            // The first request stores each tenant's ceiling. A later one resumes after the stored cursor.
            var run = await EventRewriteRun.FromMarksAsync(database, definition, token);
            var totals = await store.RewriteStoredEventsAsync(run, token);
        }
        #endregion
    }
}

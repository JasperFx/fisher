using JasperFx.Events.Documents;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/archiving.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds. The behaviour these samples
 * describe is pinned by StreamTombstoningCompliance (fisher_event_store_compliance.cs) and by
 * Events/fetching_events.cs.
 */

public static class archiving_samples
{
    public static async Task tombstone_a_stream(IDocumentStore store, Guid streamId, CancellationToken token)
    {
        #region sample_tombstone_stream
        await using var session = store.LightweightSession();

        // Queued like any other operation: nothing is deleted until SaveChangesAsync.
        session.Events.TombstoneStream(streamId);

        // Deletes the stream's tag rows, then its events, then its fi_streams row, in this
        // session's tenant and in one transaction. A stream that does not exist is a no-op.
        await session.SaveChangesAsync(token);
        #endregion
    }

    #region sample_tombstone_stream_store_neutral
    // Written against JasperFx.Events alone: IDocumentSessionOperations.Events is the shared
    // IEventStoreOperations, so the same purge compiles against a Fisher, Marten or Polecat session.
    public static async Task PurgeAsync(IDocumentSessionOperations session, Guid streamId,
        CancellationToken token)
    {
        session.Events.TombstoneStream(streamId);
        await session.SaveChangesAsync(token);
    }
    #endregion

    public static Task purge_through_the_shared_contract(IDocumentSession session, Guid streamId,
        CancellationToken token)
        => PurgeAsync(session, streamId, token);
}

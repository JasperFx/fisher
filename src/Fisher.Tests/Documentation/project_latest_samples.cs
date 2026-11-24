using Fisher.Tests.Events;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/projections/project-latest.md's ProjectLatestForCommit section
 * (fisher#685). The behaviour is pinned by Events/project_latest_for_commit.cs.
 */

public static class project_latest_samples
{
    public static async Task<CommitTrace?> project_latest_for_commit(IDocumentSession session, Guid traceId,
        CancellationToken token)
    {
        #region sample_project_latest_for_commit
        var stream = await session.Events.FetchForWriting<CommitTrace>(traceId, token);
        stream.AppendOne(new TraceScanned("dock"));

        // The pending event gets its version, timestamp and metadata now, and the commit keeps them
        var trace = await session.Events.ProjectLatestForCommit<CommitTrace>(traceId, token);

        // Store the response beside the work, in the same transaction
        session.Store(new TraceReceipt { Id = traceId, Version = trace!.LastVersion, At = trace.LastTimestamp });

        await session.SaveChangesAsync(token);
        #endregion

        return trace;
    }
}

public class TraceReceipt
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public DateTimeOffset At { get; set; }
}

using Fisher.Events;
using Fisher.Events.Storage;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Internal;

/// <summary>
///     What a DCB boundary read, kept until its session saves.
/// </summary>
/// <param name="Events">
///     The operations that read it, which under <c>ForTenant</c> is the tenant scope's and so decides the tenant
///     the check reads.
/// </param>
/// <param name="Query">The boundary's query.</param>
/// <param name="LastSeenSequence">The highest sequence it read.</param>
/// <param name="TagVersions">The <c>fi_dcb_tag_version</c> rows it captured before reading (fisher#635).</param>
/// <param name="RowsRead">How many events it read.</param>
internal sealed record TrackedBoundary(
    EventOperations Events,
    EventTagQuery Query,
    long LastSeenSequence,
    IReadOnlyList<DcbTagVersionCapture> TagVersions,
    int RowsRead)
{
    /// <summary>
    ///     Whether what the boundary read has changed, checked inside the save's write transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three checks. An event matching the query appended past <see cref="LastSeenSequence" />, exactly as
    ///         before fisher#635, honouring the query's event types. A captured tag version that moved, which is how
    ///         a tombstone, archive, un-archive, <c>AssignTagWhere</c> or compaction shows up. And, only when the
    ///         boundary read events but captured a tag with no version row, the count of matching events at or
    ///         below <see cref="LastSeenSequence" />, because a tombstone deletes the row of a tag no event carries
    ///         any more and a missing row then reads as the 0 that was captured.
    ///     </para>
    /// </remarks>
    public async Task<bool> HasMovedAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        if (await Events.AnyMatchingEventBeyondAsync(Query, LastSeenSequence, connection, transaction, token)
                .ConfigureAwait(false))
        {
            return true;
        }

        if (await Events.TagVersionsMovedAsync(Query, TagVersions, connection, transaction, token)
                .ConfigureAwait(false))
        {
            return true;
        }

        if (RowsRead > 0 && TagVersions.Any(x => x.Version == 0))
        {
            var count = await Events.CountMatchingEventsUpToAsync(Query, LastSeenSequence, connection,
                transaction, token).ConfigureAwait(false);

            return count != RowsRead;
        }

        return false;
    }
}

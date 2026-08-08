namespace Fisher.Events;

/// <summary>
///     How much is in the event store (fisher#42).
/// </summary>
/// <remarks>
///     <b><see cref="EventSequenceNumber" /> is why this has three fields rather than two.</b> It can
///     exceed <see cref="EventCount" /> — archived, compacted or deleted events leave the sequence
///     where it was, because <c>fi_events.seq_id</c> is <c>AUTOINCREMENT</c> and SQLite never reuses a
///     value it has handed out. That is load-bearing rather than incidental: a reused sequence below
///     the daemon's high-water mark would be an event no async projection ever sees. So the gap
///     between the two numbers is the count of events that once existed and no longer do.
/// </remarks>
public sealed record EventStoreStatistics
{
    /// <summary>Rows in <c>fi_events</c>.</summary>
    public long EventCount { get; init; }

    /// <summary>Rows in <c>fi_streams</c>.</summary>
    public long StreamCount { get; init; }

    /// <summary>
    ///     The highest sequence handed out, read from <c>sqlite_sequence</c>.
    /// </summary>
    /// <remarks>
    ///     Zero for a store nothing has ever been appended to: SQLite creates the
    ///     <c>sqlite_sequence</c> row on the first insert, not with the table, so the read has to
    ///     tolerate its absence rather than treating it as an error.
    /// </remarks>
    public long EventSequenceNumber { get; init; }
}

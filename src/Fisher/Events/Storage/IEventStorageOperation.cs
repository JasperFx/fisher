namespace Fisher.Events.Storage;

/// <summary>
///     Marks a queued storage operation that reads or writes the event store's own tables.
/// </summary>
/// <remarks>
///     What <c>FisherSession.SaveChangesAsync</c> reads to decide whether a unit of work needs the
///     event tables created on first use (fisher#333). An operation's <c>DocumentType</c> cannot say
///     it: the event-side operations report <c>typeof(object)</c> or <c>typeof(IEvent)</c> about as
///     often as each other, and so do raw SQL and flat-table writes that never touch <c>fi_events</c>.
/// </remarks>
internal interface IEventStorageOperation;

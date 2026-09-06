namespace Fisher.Exceptions;

/// <summary>
///     Thrown when appending to a stream that has been archived (fisher#184).
/// </summary>
/// <remarks>
///     <para>
///         <b>Archiving is not a soft delete you can keep writing through.</b> Fisher accepted the
///         append until <c>StreamArchivingCompliance</c>'s
///         <c>appending_to_an_archived_stream_is_rejected</c> ran, which is the class of gap the shared
///         suites exist for: Fisher's own archiving tests all check that the flag is set and that reads
///         behave, and none of them tried to write afterwards. Marten and Polecat have both refused it
///         from the start.
///     </para>
///     <para>
///         <b>Not lifted into JasperFx.Events</b>, unlike the three types jasperfx#751 took. Marten
///         throws its own <c>InvalidStreamOperationException</c> and Polecat a type of its own whose
///         message merely contains "archived", neither is on the shared surface, and the shared suite
///         deliberately asserts only that the commit fails and the stream is unchanged. So a store type
///         is the honest shape here until the three agree on one.
///     </para>
///     <para>
///         Raised from <c>AppendPlanner</c> before the version guard, because "this stream is closed" is
///         the more specific answer and the version is beside the point once it holds. Unarchive the
///         stream first if the intent really is to keep writing —
///         <c>session.Events.UnArchiveStream(...)</c> is the reversal, and it is what makes archiving
///         bookkeeping rather than deletion.
///     </para>
/// </remarks>
public class ArchivedStreamException : Exception
{
    public ArchivedStreamException(object id)
        : base($"Event stream '{id}' is archived and cannot be appended to. Call UnArchiveStream to "
               + "reopen it.")
    {
        Id = id;
    }

    /// <summary>
    ///     The stream's identity, a <see cref="Guid" /> or a <see cref="string" /> according to the
    ///     store's stream identity style.
    /// </summary>
    public object Id { get; }
}

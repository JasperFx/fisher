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
///         <b>Subclasses the shared <see cref="JasperFx.Events.ArchivedStreamException" /></b>
///         (jasperfx#871 / #878, in JasperFx.Events 2.74.0) — and the lift took <em>this</em> type as
///         its canonical shape, over Marten's generic <c>InvalidStreamOperationException</c> and
///         Polecat's <c>InvalidStreamException</c> whose message merely contains "archived". The note
///         that used to stand here, that it was deliberately not lifted "until the three agree on one",
///         has been answered by them agreeing on Fisher's.
///     </para>
///     <para>
///         Subclassing rather than deleting is the compatible choice, the same one
///         <see cref="ExistingStreamIdCollisionException" /> documents: an existing
///         <c>catch (Fisher.Exceptions.ArchivedStreamException)</c> keeps working and a <c>catch</c> on
///         the shared type starts working, which is what the compliance suite now requires. A literal
///         <c>TypeForwardedTo</c> is not available — forwarding needs the same fully qualified name and
///         the namespaces differ.
///     </para>
///     <para>
///         <b>Through the protected message-overriding constructor, so the canonical wording cannot
///         silently move Fisher's.</b> The two differ today: the canonical one ends "…reopen it, or
///         start a new stream", where Fisher's stops at the reversal. That is a real difference rather
///         than an oversight — starting a new stream is not a way to append to <em>this</em> one, and
///         on Fisher the id of an archived stream is still an id in use, so the suggestion would point
///         at <c>ExistingStreamIdCollisionException</c>. Same reasoning
///         <c>ExistingStreamIdCollisionException</c> records for taking that constructor even where the
///         messages currently agree.
///     </para>
///     <para>
///         Raised from <c>AppendPlanner</c> before the version guard, because "this stream is closed" is
///         the more specific answer and the version is beside the point once it holds. Unarchive the
///         stream first if the intent really is to keep writing —
///         <c>session.Events.UnArchiveStream(...)</c> is the reversal, and it is what makes archiving
///         bookkeeping rather than deletion.
///     </para>
/// </remarks>
public class ArchivedStreamException : JasperFx.Events.ArchivedStreamException
{
    public ArchivedStreamException(object id)
        : base($"Event stream '{id}' is archived and cannot be appended to. Call UnArchiveStream to "
               + "reopen it.", id)
    {
    }
}

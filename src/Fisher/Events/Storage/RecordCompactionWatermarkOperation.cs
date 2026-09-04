using System.Data.Common;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     Records the jasperfx#740 compaction watermark on <c>fi_streams</c>: the stream version through
///     which events have just been folded into a <c>Compacted&lt;T&gt;</c> snapshot.
/// </summary>
/// <remarks>
///     <para>
///         Queued by <see cref="Protected.StreamCompacting" /> alongside the snapshot replace and the
///         deletes, so the watermark commits atomically with the compaction it describes — a watermark
///         that could commit without its compaction (or the reverse) would make
///         <c>Version - CompactedVersion</c> lie in exactly the window a compaction policy reads it.
///     </para>
///     <para>
///         The value is the version of the last event fetched for compacting: the caller's cutoff for
///         a partial compaction, the stream's version for a full one. It only ever grows in practice —
///         a re-compaction below the current watermark finds a single <c>Compacted&lt;T&gt;</c> event
///         (or nothing) inside its bound and never queues this operation — so a plain assignment is
///         enough; a <c>max()</c> guard would defend against a sequence that cannot occur.
///     </para>
/// </remarks>
internal sealed class RecordCompactionWatermarkOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _events;
    private readonly string _streamId;
    private readonly string _tenantId;
    private readonly long _watermark;

    internal RecordCompactionWatermarkOperation(EventGraph events, string streamId, string tenantId,
        long watermark)
    {
        _events = events;
        _streamId = streamId;
        _tenantId = tenantId;
        _watermark = watermark;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Other;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append("update ");
        builder.Append(_events.StreamsTableName);
        builder.Append(" set compacted_version = ");
        builder.AppendParameter(_watermark);
        builder.Append(" where id = ");
        builder.AppendParameter(_streamId);

        // Under conjoined tenancy (tenant_id, id) is the stream identity — the same rule every other
        // fi_streams write applies.
        if (_events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            builder.Append(" and tenant_id = ");
            builder.AppendParameter(_tenantId);
        }

        builder.Append(';');
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

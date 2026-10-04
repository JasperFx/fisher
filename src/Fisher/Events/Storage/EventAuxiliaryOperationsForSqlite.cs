using System.Data.Common;
using Fisher.Storage;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     Flips <c>is_archived</c> on a stream and all of its events. Archive and un-archive differ only
///     in the flag value, so both ride this one operation.
/// </summary>
internal sealed class SetStreamArchivedOperation : Weasel.Storage.IStorageOperation, Fisher.Events.Storage.IEventStorageOperation
{
    private readonly bool _archived;
    private readonly EventGraph _events;
    private readonly object _streamId;
    private readonly string _tenantId;

    public SetStreamArchivedOperation(EventGraph events, object streamId, string tenantId, bool archived)
    {
        _events = events;
        _streamId = streamId;
        _tenantId = tenantId;
        _archived = archived;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Update;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var flag = _archived ? 1 : 0;

        builder.Append($"""
                        update {_events.StreamsTableName} set is_archived = {flag}
                        where id = @id and tenant_id = @tenant_id;
                        update {_events.EventsTableName} set is_archived = {flag}
                        where stream_id = @id and tenant_id = @tenant_id;
                        """);

        builder.AddParameters(new Dictionary<string, object?>
        {
            ["id"] = SqliteStorageDialect<Guid>.ToDatabaseValue(_streamId),
            ["tenant_id"] = _tenantId
        });
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

/// <summary>
///     Hard-deletes a stream and its events.
/// </summary>
/// <remarks>
///     <b>Tag rows go first, and that is not tidiness.</b> Every <c>fi_event_tag_*</c> table has a real
///     foreign key to <c>fi_events(seq_id)</c> and Weasel's default profile turns enforcement on, so
///     deleting the events first fails the whole unit of work with
///     <c>FOREIGN KEY constraint failed</c> — meaning a stream with a single tagged event could not be
///     tombstoned at all. This is the third operation in this family to learn the ordering, after
///     <c>DeleteAllEventDataAsync</c> (fisher#6) and
///     <see cref="Protected.DeleteEventsOperation" />; unlike those two it is reached through a public
///     API, so it is the one where the lesson was most visible.
/// </remarks>
internal sealed class TombstoneStreamOperation : Weasel.Storage.IStorageOperation, Fisher.Events.Storage.IEventStorageOperation
{
    private readonly EventGraph _events;
    private readonly object _streamId;
    private readonly string _tenantId;

    public TombstoneStreamOperation(EventGraph events, object streamId, string tenantId)
    {
        _events = events;
        _streamId = streamId;
        _tenantId = tenantId;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Deletion;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        // The sub-select re-reads the same tenant-and-stream predicate the event delete below uses, so
        // a tag row can only be reached through an event this operation is already removing.
        foreach (var registration in _events.TagTypes)
        {
            builder.Append($"""
                            delete from {_events.TagTableName(registration)}
                            where seq_id in (
                                select seq_id from {_events.EventsTableName}
                                where stream_id = @id and tenant_id = @tenant_id);
                            """);
        }

        builder.Append($"""
                        delete from {_events.EventsTableName}
                        where stream_id = @id and tenant_id = @tenant_id;
                        delete from {_events.StreamsTableName}
                        where id = @id and tenant_id = @tenant_id;
                        """);

        builder.AddParameters(new Dictionary<string, object?>
        {
            ["id"] = SqliteStorageDialect<Guid>.ToDatabaseValue(_streamId),
            ["tenant_id"] = _tenantId
        });
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

/// <summary>
///     Writes a shard's progression high-water mark.
/// </summary>
/// <remarks>
///     Where Polecat needs a <c>MERGE</c> and Marten an <c>ON CONFLICT</c>, SQLite has had upsert
///     syntax since 3.24 and this uses it directly. The <paramref name="upsert" /> flag still
///     distinguishes the two call sites — the row may not exist yet when the shard's floor is 0 —
///     but the upsert branch needs no separate matched/not-matched SQL.
/// </remarks>
internal sealed class RecordProgressionOperation : Weasel.Storage.IStorageOperation, Fisher.Events.Storage.IEventStorageOperation
{
    private readonly long _ceiling;
    private readonly bool _extendedTracking;
    private readonly string _name;
    private readonly string _progressionTableName;
    private readonly bool _upsert;

    public RecordProgressionOperation(string progressionTableName, string name, long ceiling,
        bool extendedTracking, bool upsert)
    {
        _progressionTableName = progressionTableName;
        _name = name;
        _ceiling = ceiling;
        _extendedTracking = extendedTracking;
        _upsert = upsert;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Update;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var now = SqliteTimestamp.NowExpression;

        var set = _extendedTracking
            ? $"last_seq_id = @seq, last_updated = {now}, heartbeat = {now}"
            : $"last_seq_id = @seq, last_updated = {now}";

        if (_upsert)
        {
            var columns = _extendedTracking
                ? "name, last_seq_id, last_updated, heartbeat"
                : "name, last_seq_id, last_updated";

            var values = _extendedTracking
                ? $"@name, @seq, {now}, {now}"
                : $"@name, @seq, {now}";

            builder.Append($"""
                            insert into {_progressionTableName} ({columns})
                            values ({values})
                            on conflict(name) do update set {set};
                            """);
        }
        else
        {
            builder.Append($"""
                            update {_progressionTableName}
                            set {set}
                            where name = @name;
                            """);
        }

        builder.AddParameters(new Dictionary<string, object?>
        {
            ["name"] = _name,
            ["seq"] = _ceiling
        });
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

/// <summary>
///     The async daemon's progression write, guarded on the floor its range was built from (fisher#402).
/// </summary>
/// <remarks>
///     <para>
///         <b>Two daemons applying the same range cannot both commit.</b> The write is a single upsert
///         whose update branch requires <c>last_seq_id</c> to still be the batch's floor, returning the
///         row it wrote. No row comes back if another agent has already moved the shard, and
///         <see cref="PostprocessAsync" /> raises <c>ProgressionProgressOutOfOrderException</c>, which
///         rolls the whole batch back, projection writes and all. JasperFx's agent stops the shard on
///         that exception, as it does on Marten.
///     </para>
///     <para>
///         <b>The layout this catches is two processes each hosting a <c>Solo</c> daemon over one
///         file.</b> Within one process the hosted service's daemon registry and the refusal to register
///         two stores over one file already prevent it, and <c>HotCold</c> is refused because a file is
///         not safe to share for leadership. Two processes over one file reached the same layout through
///         <c>Solo</c>, and SQLite serializing the two writers did not stop the second from re-applying
///         the range. A non-idempotent projection write, such as a flat-table <c>Increment</c>, was then
///         applied twice with nothing to say so. The user's ruling: unsupported, and detected.
///     </para>
///     <para>
///         <b>One statement for every range, where Marten uses an insert at floor zero and a guarded
///         update otherwise.</b> A missing row is inserted whatever the floor, which a shard starting
///         from the present needs: its first floor is the head, not zero, and it may have no row yet.
///         A row that exists must be at the floor. That is strictly the property Marten's pair gives,
///         without its one failure mode (a floor-zero insert colliding with a row a reset left at
///         zero). The pre-update row is what the <c>where</c> reads, the unqualified-column rule the
///         flat-table upsert relies on too.
///     </para>
///     <para>
///         The high-water row is not written through this. It has its own writer and only ever moves
///         forward (<c>FisherHighWaterDetector</c>).
///     </para>
/// </remarks>
internal sealed class ShardProgressionOperation : Weasel.Storage.IStorageOperation, Fisher.Events.Storage.IEventStorageOperation
{
    private readonly long _ceiling;
    private readonly bool _extendedTracking;
    private readonly long _floor;
    private readonly string _name;
    private readonly string _progressionTableName;

    public ShardProgressionOperation(string progressionTableName, string name, long floor, long ceiling,
        bool extendedTracking)
    {
        _progressionTableName = progressionTableName;
        _name = name;
        _floor = floor;
        _ceiling = ceiling;
        _extendedTracking = extendedTracking;
    }

    public Type DocumentType => typeof(object);

    public OperationRole Role() => OperationRole.Update;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var now = SqliteTimestamp.NowExpression;

        var columns = _extendedTracking ? "name, last_seq_id, last_updated, heartbeat" : "name, last_seq_id, last_updated";
        var values = _extendedTracking ? $"@name, @seq, {now}, {now}" : $"@name, @seq, {now}";
        var set = _extendedTracking
            ? $"last_seq_id = @seq, last_updated = {now}, heartbeat = {now}"
            : $"last_seq_id = @seq, last_updated = {now}";

        builder.Append($"""
                        insert into {_progressionTableName} ({columns})
                        values ({values})
                        on conflict(name) do update set {set}
                        where last_seq_id = @floor
                        returning name;
                        """);

        builder.AddParameters(new Dictionary<string, object?>
        {
            ["name"] = _name,
            ["seq"] = _ceiling,
            ["floor"] = _floor
        });
    }

    public async Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            exceptions.Add(new JasperFx.Events.Daemon.ProgressionProgressOutOfOrderException(_name, _floor, _ceiling));
        }
    }
}

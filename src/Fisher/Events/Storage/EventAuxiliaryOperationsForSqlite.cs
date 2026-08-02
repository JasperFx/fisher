using System.Data.Common;
using Fisher.Storage;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     Flips <c>is_archived</c> on a stream and all of its events. Archive and un-archive differ only
///     in the flag value, so both ride this one operation.
/// </summary>
internal sealed class SetStreamArchivedOperation : Weasel.Storage.IStorageOperation
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
internal sealed class TombstoneStreamOperation : Weasel.Storage.IStorageOperation
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
internal sealed class RecordProgressionOperation : Weasel.Storage.IStorageOperation
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

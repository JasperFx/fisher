using System.Data.Common;
using System.Globalization;
using Fisher.Exceptions;
using Fisher.Events.Schema;
using Fisher.Storage;
using JasperFx.Core.Exceptions;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     Whether the closed-shape append writes the stream row with an INSERT (new stream) or an UPDATE
///     (existing stream). Decided by the append planner after its version read.
/// </summary>
internal enum StreamWriteMode
{
    Insert,
    Update
}

/// <summary>
///     SQLite batched append for the closed-shape Quick event storage — the dialect-supplied
///     counterpart of Marten's Postgres <c>QuickAppendEventsOperation</c> and Polecat's SQL Server
///     one. Emits a single self-contained command per stream: the stream-row write (through the
///     descriptor's insert/update closure), one <c>INSERT INTO fi_events</c> per event, and a
///     trailing <c>SELECT</c> that reads the assigned <c>seq_id</c>s back.
/// </summary>
/// <remarks>
///     <para>
///         The read-back is where this diverges from both siblings, and the reason is worth stating.
///         Postgres returns the sequences from its bulk function; SQL Server accumulates them with
///         <c>OUTPUT inserted.seq_id INTO @table</c>. SQLite has neither — its <c>RETURNING</c> clause
///         would produce one result set per INSERT, and the shared batch executor's contract is
///         exactly one result set per operation.
///     </para>
///     <para>
///         So the trailing SELECT re-reads the rows this operation just wrote, keyed by the stream and
///         the contiguous version range the planner assigned, ordered by version. That is
///         deterministic regardless of how SQLite allocated the ids, and it does not assume the
///         allocated <c>seq_id</c>s are contiguous — an assumption that holds today (a SQLite write
///         transaction is exclusive, so no other writer can interleave) but would be a silent
///         correctness trap if that ever stopped being true.
///     </para>
/// </remarks>
internal sealed class FisherQuickAppendEventsOperation
    : Weasel.Storage.IStorageOperation, IExceptionTransform
{
    private readonly QuickEventStorageDescriptor _descriptor;
    private readonly EventGraph _graph;

    public FisherQuickAppendEventsOperation(EventGraph graph, QuickEventStorageDescriptor descriptor,
        StreamAction stream)
    {
        _graph = graph;
        _descriptor = descriptor;
        Stream = stream;
    }

    public StreamAction Stream { get; }

    /// <summary>Set by the planner: INSERT a new stream row, or UPDATE the existing one's version.</summary>
    public StreamWriteMode Mode { get; set; } = StreamWriteMode.Insert;

    public Type DocumentType => typeof(IEvent);

    public OperationRole Role() => OperationRole.Events;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        // Stream row write reuses the dialect's descriptor closures so that SQL stays in one place.
        if (Mode == StreamWriteMode.Insert)
        {
            _descriptor.ConfigureInsertStreamCommand(builder, Stream);
        }
        else
        {
            _descriptor.ConfigureUpdateStreamVersionCommand(builder, Stream);
        }

        builder.Append(";");

        var options = _graph.EventOptions;

        foreach (var @event in Stream.Events)
        {
            builder.Append("insert into ");
            builder.Append(_graph.EventsTableName);
            // A binary body goes into data_binary and leaves data holding the JSON placeholder; a JSON
            // body does the reverse. Which one this event is comes off the event type's registered
            // serializer, so a stream can mix the two freely.
            var binary = _graph.BinaryEncoderFor(@event);

            builder.Append(" (id, stream_id, version, data, type, timestamp, tenant_id, dotnet_type");

            if (options.EnableCorrelationId)
            {
                builder.Append(", correlation_id");
            }

            if (options.EnableCausationId)
            {
                builder.Append(", causation_id");
            }

            if (options.EnableHeaders)
            {
                builder.Append(", headers");
            }

            if (options.EnableUserName)
            {
                builder.Append(", user_name");
            }

            // Unconditional, because the column is (fisher#93): an explicit null for a JSON event is
            // what lets the read path decide encoding per row rather than per type.
            //
            // ⚠️ LAST, and it has to be — the value below is bound last. fisher#43 named it ninth here
            // while binding it last, so a store with any of the four metadata columns enabled wrote a
            // binary event's BLOB into correlation_id and its correlation id into data_binary. Nothing
            // caught it because the binary tests enabled no metadata and the metadata tests appended no
            // binary event. The column list and the bind order are one contract.
            builder.Append(", data_binary");

            builder.Append(") values (");

            Bind(builder, @event.Id, StorageColumnType.Guid);

            builder.Append(", ");
            if (_descriptor.IsGuidStreamIdentity)
            {
                Bind(builder, Stream.Id, StorageColumnType.Guid);
            }
            else
            {
                Bind(builder, Stream.Key!, StorageColumnType.String);
            }

            builder.Append(", ");
            Bind(builder, @event.Version, StorageColumnType.Long);

            builder.Append(", ");

            Bind(builder,
                binary is null ? session.Serializer.ToJson(@event.Data) : EventsTable.JsonPlaceholder,
                StorageColumnType.Json);

            builder.Append(", ");
            Bind(builder, @event.EventTypeName, StorageColumnType.String);

            builder.Append(", ");
            builder.Append(SqliteTimestamp.NowExpression);

            builder.Append(", ");
            Bind(builder, @event.TenantId ?? Stream.TenantId, StorageColumnType.String);

            builder.Append(", ");
            Bind(builder, @event.DotNetTypeName, StorageColumnType.String);

            if (options.EnableCorrelationId)
            {
                builder.Append(", ");
                Bind(builder, (object?)@event.CorrelationId ?? DBNull.Value, StorageColumnType.String);
            }

            if (options.EnableCausationId)
            {
                builder.Append(", ");
                Bind(builder, (object?)@event.CausationId ?? DBNull.Value, StorageColumnType.String);
            }

            if (options.EnableHeaders)
            {
                builder.Append(", ");
                object headers = @event.Headers is { Count: > 0 }
                    ? session.Serializer.ToJson(@event.Headers)
                    : DBNull.Value;
                Bind(builder, headers, StorageColumnType.Json);
            }

            if (options.EnableUserName)
            {
                builder.Append(", ");
                Bind(builder, (object?)@event.UserName ?? DBNull.Value, StorageColumnType.String);
            }

            // Last, because it is the last column in the list above — the two orders are one contract,
            // as everywhere else Fisher builds a positional insert.
            //
            // Bound as a byte[] parameter rather than composed into the SQL: Microsoft.Data.Sqlite maps
            // it to a real BLOB, so a payload of arbitrary bytes (gzip output, MessagePack) survives.
            // Any route through a text encoding here corrupts exactly those and nothing else.
            builder.Append(", ");
            if (binary is null)
            {
                builder.Append("null");
            }
            else
            {
                builder.AppendParameter(binary);
            }

            builder.Append(");");
        }

        WriteSequenceReadBack(builder);
    }

    /// <summary>
    ///     The trailing single-result-set SELECT that reads the assigned sequences back. See the class
    ///     remarks for why this is a re-read rather than a RETURNING clause.
    /// </summary>
    private void WriteSequenceReadBack(ICommandBuilder builder)
    {
        var events = Stream.Events;

        builder.Append("select seq_id from ");
        builder.Append(_graph.EventsTableName);
        builder.Append(" where stream_id = ");

        if (_descriptor.IsGuidStreamIdentity)
        {
            Bind(builder, Stream.Id, StorageColumnType.Guid);
        }
        else
        {
            Bind(builder, Stream.Key!, StorageColumnType.String);
        }

        if (_descriptor.IsTenancyConjoined)
        {
            builder.Append(" and tenant_id = ");
            Bind(builder, Stream.TenantId, StorageColumnType.String);
        }

        builder.Append(" and version >= ");
        Bind(builder, events[0].Version, StorageColumnType.Long);
        builder.Append(" and version <= ");
        Bind(builder, events[^1].Version, StorageColumnType.Long);
        builder.Append(" order by version;");
    }

    private void Bind(ICommandBuilder builder, object value, StorageColumnType type)
    {
        // Guids are TEXT in this schema; convert before binding so the value matches what the
        // column holds rather than the 16-byte BLOB Microsoft.Data.Sqlite would otherwise write.
        var parameter = builder.AppendParameter(SqliteStorageDialect<Guid>.ToDatabaseValue(value));
        _descriptor.Dialect.SetParameterType(parameter, type);
    }

    public async Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        // Single result set, one row per event in version order.
        var events = Stream.Events;
        var i = 0;

        while (i < events.Count && await reader.ReadAsync(token).ConfigureAwait(false))
        {
            events[i].Sequence = await reader.GetFieldValueAsync<long>(0, token).ConfigureAwait(false);
            i++;
        }
    }

    /// <summary>
    ///     Map a SQLite primary-key violation on the stream INSERT to
    ///     <see cref="ExistingStreamIdCollisionException" />.
    /// </summary>
    /// <remarks>
    ///     Matching needs the EXTENDED result code. The primary code for every constraint failure is
    ///     the same <c>SQLITE_CONSTRAINT</c> (19) — a NOT NULL violation and a duplicate stream id are
    ///     indistinguishable at that level — so only <c>SQLITE_CONSTRAINT_PRIMARYKEY</c> (1555)
    ///     identifies this case.
    /// </remarks>
    public bool TryTransform(Exception original, out Exception? transformed)
    {
        if (Mode == StreamWriteMode.Insert)
        {
            var sqlite = original as SqliteException ?? original.InnerException as SqliteException;

            if (sqlite is { SqliteExtendedErrorCode: SqliteConstraintPrimaryKey })
            {
                transformed = new ExistingStreamIdCollisionException(Stream.Key is not null
                    ? Stream.Key
                    : Stream.Id);
                return true;
            }
        }

        transformed = null;
        return false;
    }

    /// <summary>SQLITE_CONSTRAINT_PRIMARYKEY — extended result code 1555.</summary>
    internal const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQLITE_CONSTRAINT_UNIQUE — extended result code 2067.</summary>
    internal const int SqliteConstraintUnique = 2067;
}

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
///     <para>
///         <b>The command shape is the shared <see cref="PerEventQuickAppendOperationBase" />'s</b> (weasel#743):
///         stream row, one insert per event, metadata columns through <see cref="IEventMetadataBinder" />s,
///         the read-back, and the insert-only collision transform. What is Fisher's is the column set, the
///         SQLite bindings, the binary body, and the read-back's SQL.
///     </para>
/// </remarks>
internal sealed class FisherQuickAppendEventsOperation : PerEventQuickAppendOperationBase
{
    private const string CoreColumns = "id, stream_id, version, data, type, timestamp, tenant_id, dotnet_type";

    private readonly QuickEventStorageDescriptor _descriptor;
    private readonly EventGraph _graph;

    // Each event's binary body, computed once in WriteCoreValues and bound last in the suffix.
    private readonly byte[]?[] _binary;

    public FisherQuickAppendEventsOperation(EventGraph graph, QuickEventStorageDescriptor descriptor,
        StreamAction stream) : base(stream, FisherEventMetadataBinder.For(graph.EventOptions, descriptor.Dialect))
    {
        _graph = graph;
        _descriptor = descriptor;
        _binary = new byte[stream.Events.Count][];
    }

    // Stream row write reuses the dialect's descriptor closures so that SQL stays in one place.
    protected override void WriteInsertStream(ICommandBuilder builder, IStorageSession session)
    {
        _descriptor.ConfigureInsertStreamCommand(builder, Stream);
        builder.Append(";");
    }

    protected override void WriteUpdateStreamVersion(ICommandBuilder builder, IStorageSession session)
    {
        _descriptor.ConfigureUpdateStreamVersionCommand(builder, Stream);
        builder.Append(";");
    }

    protected override void WriteEventInsertPrefix(ICommandBuilder builder, IEvent @event, int ordinal)
    {
        builder.Append("insert into ");
        builder.Append(_graph.EventsTableName);
        builder.Append(" (");
        builder.Append(ComposeColumnList(CoreColumns, MetadataBinders));

        // Unconditional, because the column is (fisher#93): an explicit null for a JSON event is what lets
        // the read path decide encoding per row rather than per type.
        //
        // ⚠️ LAST, and it has to be — the value is bound last, in the suffix. fisher#43 named it ninth
        // while binding it last, so a store with any metadata column enabled wrote a binary event's BLOB
        // into correlation_id. The column list and the bind order are one contract.
        builder.Append(", data_binary) values (");
    }

    protected override void WriteCoreValues(ICommandBuilder builder, IEvent @event, IStorageSession session)
    {
        // A binary body goes into data_binary and leaves data holding the JSON placeholder; a JSON body does
        // the reverse. Which one this event is comes off the event type's registered serializer, so a
        // stream can mix the two freely.
        var ordinal = indexOf(@event);
        var binary = _binary[ordinal] = _graph.BinaryEncoderFor(@event);

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
        if (@event.Timestamp != default)
        {
            // The value the planner stamped before inline projections ran. Persisting it (rather than the
            // server-side now) is what keeps the inline and replay views of e.Timestamp identical.
            Bind(builder, SqliteTimestamp.ToDatabaseValue(@event.Timestamp), StorageColumnType.String);
        }
        else
        {
            builder.Append(SqliteTimestamp.NowExpression);
        }

        builder.Append(", ");
        Bind(builder, @event.TenantId ?? Stream.TenantId, StorageColumnType.String);

        builder.Append(", ");
        Bind(builder, @event.DotNetTypeName, StorageColumnType.String);
    }

    protected override void WriteEventInsertSuffix(ICommandBuilder builder, IEvent @event, int ordinal)
    {
        // Last, because data_binary is the last column in the list — the two orders are one contract.
        //
        // Bound as a byte[] parameter rather than composed into the SQL: Microsoft.Data.Sqlite maps it to a
        // real BLOB, so a payload of arbitrary bytes (gzip output, MessagePack) survives. Any route through
        // a text encoding here corrupts exactly those and nothing else.
        builder.Append(", ");
        if (_binary[ordinal] is { } binary)
        {
            builder.AppendParameter(binary);
        }
        else
        {
            builder.Append("null");
        }

        builder.Append(");");
    }

    /// <summary>
    ///     The trailing single-result-set SELECT that reads the assigned sequences back. See the class
    ///     remarks for why this is a re-read rather than a RETURNING clause.
    /// </summary>
    protected override void WriteSequenceReadback(ICommandBuilder builder, IStorageSession session)
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

    private int indexOf(IEvent @event)
    {
        var events = Stream.Events;
        for (var i = 0; i < events.Count; i++)
        {
            if (ReferenceEquals(events[i], @event)) return i;
        }

        throw new InvalidOperationException("The event is not one of this stream's.");
    }

    private void Bind(ICommandBuilder builder, object value, StorageColumnType type)
    {
        // Guids are TEXT in this schema; convert before binding so the value matches what the column holds
        // rather than the 16-byte BLOB Microsoft.Data.Sqlite would otherwise write.
        var parameter = builder.AppendParameter(SqliteStorageDialect<Guid>.ToDatabaseValue(value));
        _descriptor.Dialect.SetParameterType(parameter, type);
    }

    /// <summary>
    ///     Map a SQLite primary-key violation on the stream INSERT to
    ///     <see cref="ExistingStreamIdCollisionException" />. The base calls this for an insert only.
    /// </summary>
    /// <remarks>
    ///     Matching needs the EXTENDED result code. The primary code for every constraint failure is the same
    ///     <c>SQLITE_CONSTRAINT</c> (19), so only <c>SQLITE_CONSTRAINT_PRIMARYKEY</c> (1555) identifies this.
    /// </remarks>
    protected override Exception? TransformInsertStreamException(Exception original)
    {
        var sqlite = original as SqliteException ?? original.InnerException as SqliteException;

        return sqlite is { SqliteExtendedErrorCode: SqliteConstraintPrimaryKey }
            ? new Fisher.Exceptions.ExistingStreamIdCollisionException(Stream.Key is not null ? Stream.Key : Stream.Id)
            : null;
    }

    /// <summary>SQLITE_CONSTRAINT_PRIMARYKEY — extended result code 1555.</summary>
    internal const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>SQLITE_CONSTRAINT_UNIQUE — extended result code 2067.</summary>
    internal const int SqliteConstraintUnique = 2067;
}

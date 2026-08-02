using Fisher.Exceptions;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     SQLite implementation of the closed-shape event-storage dialect seam
///     (<see cref="IEventStoreSqlDialect" />, Weasel.Storage) — the sibling of Marten's
///     <c>PostgresEventStoreDialect</c> and Polecat's <c>SqlServerEventStoreDialect</c>. It builds the
///     per-append-mode descriptors that drive the shared <c>EventStorage&lt;TId&gt;</c> hierarchy.
/// </summary>
/// <remarks>
///     Fisher is QuickAppend-only, so only the Quick descriptor is implemented. SQLite has neither
///     stored procedures nor server-side sequence objects, which is what the Rich and
///     QuickWithServerTimestamps paths exist to exploit elsewhere.
/// </remarks>
internal sealed class SqliteEventStoreDialect : IEventStoreSqlDialect
{
    public RichEventStorageDescriptor BuildRichDescriptor(EventRegistry graph, IStorageSerializer serializer)
        => throw new NotSupportedException(
            "Fisher is QuickAppend-only; the Rich (full-mode) event append path is not supported.");

    public QuickWithServerTimestampsEventStorageDescriptor BuildQuickWithServerTimestampsDescriptor(
        EventRegistry graph, IStorageSerializer serializer)
        => throw new NotSupportedException(
            "Fisher is QuickAppend-only; the QuickWithServerTimestamps event append path is not supported.");

    public QuickEventStorageDescriptor BuildQuickDescriptor(EventRegistry registry, IStorageSerializer serializer)
    {
        var graph = (EventGraph)registry;
        var isGuid = graph.StreamIdentity == StreamIdentity.AsGuid;
        var isConjoined = graph.TenancyStyle == TenancyStyle.Conjoined;
        var dialect = ResolveStorageDialect(isGuid);
        var options = graph.EventOptions;

        return new QuickEventStorageDescriptor(
            $"insert into {graph.EventsTableName} ",
            $"insert into {graph.StreamsTableName} ",
            $"update {graph.StreamsTableName} set version = ",
            // Fisher is JSON-only — there is no binary event serialization, so bdata is always null.
            e => serializer.ToJson(e.Data),
            _ => null)
        {
            IsGuidStreamIdentity = isGuid,
            Dialect = dialect,
            IsTenancyConjoined = isConjoined,
            AssertStreamVersionSql = $"select version from {graph.StreamsTableName} where id = ",
            HasCausationId = options.EnableCausationId,
            HasCorrelationId = options.EnableCorrelationId,
            HasHeaders = options.EnableHeaders,
            HasUserName = options.EnableUserName,
            ConfigureInsertStreamCommand = BuildInsertStreamCommandConfigurer(graph, isGuid, dialect),
            TransformInsertStreamException = MapInsertStreamException,
            ConfigureUpdateStreamVersionCommand =
                BuildUpdateStreamVersionCommandConfigurer(graph, isGuid, isConjoined, dialect),
            CreateQuickAppendEventsOperation = (descriptor, stream) =>
                new FisherQuickAppendEventsOperation(graph, descriptor, stream)
        };
    }

    /// <summary>
    ///     The SQLite dialect of the shared auxiliary-operation seam: archive/un-archive, tombstone,
    ///     and progression upsert.
    /// </summary>
    public EventAuxiliaryOperations? BuildAuxiliaryOperations(EventRegistry registry)
    {
        var graph = (EventGraph)registry;

        return new EventAuxiliaryOperations(
            (streamId, tenantId, archived) => new SetStreamArchivedOperation(graph, streamId, tenantId, archived),
            (streamId, tenantId) => new TombstoneStreamOperation(graph, streamId, tenantId),
            (shardIdentity, sequence, upsert) => new RecordProgressionOperation(
                graph.ProgressionTableName, shardIdentity, sequence,
                graph.EnableExtendedProgressionTracking, upsert));
    }

    /// <summary>
    ///     The <see cref="IStorageDialect" /> the descriptor threads to the shared operations for
    ///     parameter typing. The stream-identity generic only affects id typing, which the operations
    ///     set explicitly anyway.
    /// </summary>
    private static IStorageDialect ResolveStorageDialect(bool isGuid)
        => isGuid ? SqliteStorageDialect<Guid>.Instance : SqliteStorageDialect<string>.Instance;

    /// <summary>
    ///     Closure for the <c>fi_streams</c> insert. Columns are named so one statement serves both
    ///     single-tenant and conjoined tables.
    /// </summary>
    private static Action<ICommandBuilder, StreamAction> BuildInsertStreamCommandConfigurer(
        EventGraph graph, bool isGuid, IStorageDialect dialect)
    {
        var prefix = $"insert into {graph.StreamsTableName} " +
                     "(id, type, version, timestamp, created, tenant_id) values (";

        return (builder, stream) =>
        {
            builder.Append(prefix);

            var idParam = builder.AppendParameter(isGuid
                ? SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
                : stream.Key!);
            dialect.SetParameterType(idParam, isGuid ? StorageColumnType.Guid : StorageColumnType.String);

            builder.Append(", ");
            // Go through AggregateAliasFor rather than reading .Name directly. The persisted value is
            // identical — the alias IS the simple name — but the call also registers the alias→Type
            // pair on the EventGraph, which is the only thing that populates its lookup map. This
            // QuickAppend writer never goes through StreamAction.PrepareEvents, so without this the
            // process that WROTE a stream could not resolve the type it had just stamped.
            var typeParam = builder.AppendParameter(
                stream.AggregateType is null ? DBNull.Value : graph.AggregateAliasFor(stream.AggregateType));
            dialect.SetParameterType(typeParam, StorageColumnType.String);

            builder.Append(", ");
            var versionParam = builder.AppendParameter(stream.Version);
            dialect.SetParameterType(versionParam, StorageColumnType.Long);

            builder.Append($", {SqliteTimestamp.NowExpression}, {SqliteTimestamp.NowExpression}, ");
            var tenantParam = builder.AppendParameter(stream.TenantId);
            dialect.SetParameterType(tenantParam, StorageColumnType.String);

            builder.Append(")");
        };
    }

    /// <summary>
    ///     Closure for the <c>fi_streams</c> version bump. The <c>and version = @expected</c> guard is
    ///     the shared expected-version check; zero rows updated is what makes the shared operation's
    ///     postprocess raise <c>EventStreamUnexpectedMaxEventIdException</c>.
    /// </summary>
    private static Action<ICommandBuilder, StreamAction> BuildUpdateStreamVersionCommandConfigurer(
        EventGraph graph, bool isGuid, bool isConjoined, IStorageDialect dialect)
    {
        var prefix = $"update {graph.StreamsTableName} set version = ";

        return (builder, stream) =>
        {
            builder.Append(prefix);
            var versionParam = builder.AppendParameter(stream.Version);
            dialect.SetParameterType(versionParam, StorageColumnType.Long);

            builder.Append($", timestamp = {SqliteTimestamp.NowExpression} where id = ");
            var idParam = builder.AppendParameter(isGuid
                ? SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
                : stream.Key!);
            dialect.SetParameterType(idParam, isGuid ? StorageColumnType.Guid : StorageColumnType.String);

            builder.Append(" and version = ");
            var expectedParam = builder.AppendParameter(stream.ExpectedVersionOnServer!.Value);
            dialect.SetParameterType(expectedParam, StorageColumnType.Long);

            if (isConjoined)
            {
                builder.Append(" and tenant_id = ");
                var tenantParam = builder.AppendParameter(stream.TenantId);
                dialect.SetParameterType(tenantParam, StorageColumnType.String);
            }
        };
    }

    /// <summary>
    ///     Maps a SQLite primary-key violation on the <c>fi_streams</c> insert to
    ///     <see cref="ExistingStreamIdCollisionException" />; returns null for anything else.
    /// </summary>
    private static Exception? MapInsertStreamException(Exception original, StreamAction stream)
    {
        var sqlite = original as SqliteException ?? original.InnerException as SqliteException;

        if (sqlite is { SqliteExtendedErrorCode: FisherQuickAppendEventsOperation.SqliteConstraintPrimaryKey })
        {
            return new ExistingStreamIdCollisionException(stream.Key is not null ? stream.Key : stream.Id);
        }

        return null;
    }
}

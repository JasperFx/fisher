using Fisher.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Appending;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Events;

/// <summary>
///     Turns the stream actions accumulated on a session into the storage operations that write them.
/// </summary>
/// <remarks>
///     <para>
///         <b>The decisions are the shared <see cref="ClientSideAppendPlanner" />'s</b> (jasperfx#1051): which
///         stream is refused and why, how events are numbered, what metadata they are stamped with. What
///         stays here is reading the stream rows, under the write lock, and turning each written step into a
///         <see cref="Storage.FisherQuickAppendEventsOperation" />. Three rules Fisher established are rows of
///         that table now: a missing stream row refuses a non-zero expected version (fisher#378), an archived
///         stream refuses an append and a Start on an archived id is a collision (fisher#184), and a stream
///         flagged <c>AlwaysEnforceConsistency</c> with no events is still version-checked (fisher#184).
///     </para>
///     <para>
///         Running this at flush time rather than at <c>Append</c> time is what lets event versions be
///         assigned from the stream's actual current version, and it collapses several appends to one
///         stream into a single stream-row write.
///     </para>
///     <para>
///         The version read and the write must not race. Marten holds an advisory lock and Polecat
///         reads under <c>UPDLOCK, HOLDLOCK</c>; SQLite has neither, so the planner runs inside the
///         session's write transaction and takes SQLite's exclusive write lock immediately by issuing
///         <c>BEGIN IMMEDIATE</c>.
///     </para>
/// </remarks>
internal sealed class AppendPlanner
{
    private readonly EventGraph _graph;
    private readonly FisherSession _session;
    private readonly AppendOrigin _origin;

    public AppendPlanner(FisherSession session, AppendOrigin origin = AppendOrigin.UnitOfWork)
    {
        _session = session;
        _graph = session.EventGraph;
        _origin = origin;
    }

    /// <summary>
    ///     Read every stream row, plan, apply, and build one append operation per written stream.
    /// </summary>
    /// <remarks>
    ///     Must run inside the write transaction: the version read and the write it decides have to be under
    ///     one lock. A refusal throws before anything is stamped, so a refused unit of work leaves every
    ///     action as it was.
    /// </remarks>
    public async Task<IReadOnlyList<Weasel.Storage.IStorageOperation>> PlanAsync(
        IReadOnlyCollection<StreamAction> streams, SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        var context = ContextFor();
        var needed = streams
            .Where(x => ClientSideAppendPlanner.WorkFor(x, context.Tenancy) != StreamAppendWork.Nothing)
            .ToList();

        if (needed.Count == 0)
        {
            return [];
        }

        var rows = await ReadRowsAsync(needed, connection, transaction, token).ConfigureAwait(false);

        var plan = ClientSideAppendPlanner.Plan(streams, rows.For, context);
        plan.Apply();

        var operations = new List<Weasel.Storage.IStorageOperation>();

        foreach (var step in plan.Writes)
        {
            var operation = (Storage.FisherQuickAppendEventsOperation)QuickAppendEvents(step.Stream);
            operation.Mode = step.Verdict == StreamAppendVerdict.InsertStream
                ? StreamRowWrite.Insert
                : StreamRowWrite.Update;
            operations.Add(operation);
        }

        return operations;
    }

    /// <summary>
    ///     Number the session's pending events ahead of the inline projections, outside the write lock.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Inline projections need each event's version, but Fisher normally learns versions inside the
    ///         write transaction. This reads the current versions early so projections have something to work
    ///         with.
    ///     </para>
    ///     <para>
    ///         <b>This is not a weakened concurrency guard.</b> <see cref="PlanAsync" /> re-reads and
    ///         re-numbers inside the transaction and the expected-version check runs there, so a writer that
    ///         raced in between still fails the commit.
    ///     </para>
    ///     <para>
    ///         The timestamp is stamped here too (fisher#119) and only where unset, so the in-transaction pass
    ///         keeps it: what an inline projection folded is what the row persists.
    ///     </para>
    /// </remarks>
    public async Task AssignVersionsAheadOfProjectionsAsync(IReadOnlyList<StreamAction> streams,
        CancellationToken token)
    {
        var withEvents = streams.Where(x => x.Events.Count > 0).ToList();

        if (withEvents.Count == 0)
        {
            return;
        }

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);

        // The caller's transaction for an enlisted session (fisher#300), null for an owned one.
        var rows = await ReadRowsAsync(withEvents, connection, _session.EnlistedTransaction, token)
            .ConfigureAwait(false);

        ClientSideAppendPlanner.NumberAhead(withEvents, rows.For, ContextFor());
    }

    private ClientSideAppendContext ContextFor() => new()
    {
        Metadata = _session,

        // Read once per unit of work, round-tripped through SqliteTimestamp so the value an inline
        // projection sees is exactly what the millisecond-precision column will hold (fisher#119).
        Timestamp = SqliteTimestamp.FromDatabaseValue(
            SqliteTimestamp.ToDatabaseValue(_graph.TimeProvider.GetUtcNow())),
        AppendMode = EventAppendMode.Quick,
        Tenancy = _graph.TenancyStyle,
        Origin = _origin,
        Refusals = FisherAppendRefusals.Instance
    };

    private sealed class FisherAppendRefusals : AppendRefusals
    {
        internal static readonly FisherAppendRefusals Instance = new();

        public override Exception Collision(object id, Type? aggregateType)
            => new JasperFx.Events.ExistingStreamIdCollisionException(id);

        public override Exception Archived(object id) => new JasperFx.Events.ArchivedStreamException(id);
    }

    // ---- reading the stream rows ----

    private sealed class StreamRows
    {
        private readonly EventGraph _graph;
        private readonly HashSet<(string Id, string? Tenant)> _read = [];
        private readonly Dictionary<(string Id, string? Tenant), StreamRowSnapshot> _found = new();

        public StreamRows(EventGraph graph)
        {
            _graph = graph;
        }

        public void MarkRead(string id, string? tenant) => _read.Add((id, tenant));

        public void Found(string id, string? tenant, long version, bool isArchived)
            => _found[(id, tenant)] = StreamRowSnapshot.FromRow(version, isArchived);

        public StreamRowSnapshot For(StreamAction stream)
        {
            var key = (DatabaseIdFor(_graph, stream), TenantKey(_graph, stream));

            if (_found.TryGetValue(key, out var row)) return row;

            return _read.Contains(key) ? StreamRowSnapshot.Missing : StreamRowSnapshot.NotRead;
        }
    }

    private static string? TenantKey(EventGraph graph, StreamAction stream)
        => graph.TenancyStyle == TenancyStyle.Conjoined ? stream.TenantId : null;

    private async Task<StreamRows> ReadRowsAsync(IReadOnlyList<StreamAction> streams, SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var rows = new StreamRows(_graph);

        // Conjoined tenancy keys a stream row on (tenant_id, id), so one stream id can exist once per
        // tenant; reading by id alone would hand one tenant another tenant's version.
        foreach (var group in streams.GroupBy(x => TenantKey(_graph, x)))
        {
            var ids = group.Select(x => DatabaseIdFor(_graph, x)).Distinct(StringComparer.Ordinal).ToList();

            foreach (var id in ids)
            {
                rows.MarkRead(id, group.Key);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = group.Key is null
                ? $"select id, version, is_archived from {_graph.StreamsTableName} where id in (select value from json_each(@ids))"
                : $"select id, version, is_archived from {_graph.StreamsTableName} where tenant_id = @tenant_id and id in (select value from json_each(@ids))";

            // One parameter however many streams the unit of work touched, rather than a placeholder per
            // id against SQLite's host-parameter limit.
            command.Parameters.Add(new SqliteParameter("ids", SqliteJsonArrays.Write(ids))
            {
                SqliteType = SqliteType.Text
            });

            if (group.Key is not null)
            {
                command.Parameters.Add(new SqliteParameter("tenant_id", group.Key) { SqliteType = SqliteType.Text });
            }

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                rows.Found(reader.GetString(0), group.Key, reader.GetInt64(1), reader.GetInt64(2) != 0);
            }
        }

        return rows;
    }

    // The lowercase canonical form fi_streams.id holds — an uppercase Guid would match nothing under
    // SQLite's case-sensitive default collation.
    private static string DatabaseIdFor(EventGraph graph, StreamAction stream)
        => graph.StreamIdentity == StreamIdentity.AsGuid
            ? (string)SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
            : stream.Key!;

    private Weasel.Storage.IStorageOperation QuickAppendEvents(StreamAction stream)
        => _graph.StreamIdentity == StreamIdentity.AsGuid
            ? ((EventStorage<Guid>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream)
            : ((EventStorage<string>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream);
}

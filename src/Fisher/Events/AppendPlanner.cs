using Fisher.Internal;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Events;

/// <summary>
///     Turns the stream actions accumulated on a session into the storage operations that write them.
/// </summary>
/// <remarks>
///     <para>
///         Running this at flush time rather than at <c>Append</c> time is what lets event versions be
///         assigned from the stream's actual current version, and it collapses several appends to one
///         stream into a single stream-row write.
///     </para>
///     <para>
///         The version read and the write must not race. Marten holds an advisory lock and Polecat
///         reads under <c>UPDLOCK, HOLDLOCK</c>; SQLite has neither, so the planner runs inside the
///         session's write transaction and takes SQLite's exclusive write lock immediately by issuing
///         <c>BEGIN IMMEDIATE</c>. Under the default deferred transaction the lock would only be taken
///         at the first write — after the version read — leaving a window in which two sessions both
///         read version N and both try to write N+1.
///     </para>
/// </remarks>
internal sealed class AppendPlanner
{
    private readonly EventGraph _graph;
    private readonly FisherSession _session;

    public AppendPlanner(FisherSession session)
    {
        _session = session;
        _graph = session.EventGraph;
    }

    /// <summary>
    ///     Assign versions and build one append operation per stream.
    /// </summary>
    public async Task<IReadOnlyList<Weasel.Storage.IStorageOperation>> PlanAsync(
        IReadOnlyCollection<StreamAction> streams, SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        var operations = new List<Weasel.Storage.IStorageOperation>(streams.Count);

        foreach (var stream in streams)
        {
            if (stream.Events.Count == 0)
            {
                continue;
            }

            ApplySessionMetadata(stream);

            var mode = await PlanStreamAsync(stream, connection, transaction, token).ConfigureAwait(false);

            var operation = (Storage.FisherQuickAppendEventsOperation)QuickAppendEvents(stream);
            operation.Mode = mode;
            operations.Add(operation);
        }

        return operations;
    }

    /// <summary>
    ///     Number each stream's events from its current server version, before the write transaction
    ///     opens, so inline projections fold events that already know their versions.
    /// </summary>
    /// <remarks>
    ///     Deliberately does not guard anything. The authoritative version read, the optimistic
    ///     concurrency check and the final numbering all still happen inside the write transaction in
    ///     <see cref="PlanAsync" />; this only makes a best-effort assignment early so a projection has
    ///     something to read. If a racing writer moves the stream on in between, the numbers assigned
    ///     here are simply overwritten and the commit fails on the real guard.
    /// </remarks>
    public async Task AssignVersionsAheadOfProjectionsAsync(IReadOnlyList<StreamAction> streams,
        CancellationToken token)
    {
        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);

        foreach (var stream in streams)
        {
            if (stream.Events.Count == 0)
            {
                continue;
            }

            ApplySessionMetadata(stream);

            var current = await ReadCurrentVersionAsync(stream, connection, transaction: null, token)
                .ConfigureAwait(false);

            AssignVersions(stream, current ?? 0);
        }
    }

    private async Task<Storage.StreamWriteMode> PlanStreamAsync(StreamAction stream, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        var currentVersion = await ReadCurrentVersionAsync(stream, connection, transaction, token)
            .ConfigureAwait(false);

        if (stream.ActionType == StreamActionType.Start)
        {
            if (currentVersion is not null)
            {
                throw new Exceptions.ExistingStreamIdCollisionException(
                    stream.Key is not null ? stream.Key : stream.Id);
            }

            AssignVersions(stream, 0);
            return Storage.StreamWriteMode.Insert;
        }

        if (currentVersion is null)
        {
            // Appending to a stream that does not exist yet creates it, matching Marten and Polecat:
            // Append is not an assertion that the stream is already there.
            AssignVersions(stream, 0);
            stream.ExpectedVersionOnServer = null;
            return Storage.StreamWriteMode.Insert;
        }

        if (stream.ExpectedVersionOnServer is { } expected && expected != currentVersion.Value)
        {
            throw new EventStreamUnexpectedMaxEventIdException(
                stream.Key is not null ? stream.Key : stream.Id,
                stream.AggregateType,
                expected,
                currentVersion.Value);
        }

        stream.ExpectedVersionOnServer = currentVersion.Value;
        AssignVersions(stream, currentVersion.Value);

        return Storage.StreamWriteMode.Update;
    }

    /// <summary>
    ///     Copy the session's tracing and user metadata onto every event that does not already carry
    ///     its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Mirrors the private <c>StreamAction.ProcessMetadata</c>, which Fisher reaches only
    ///         through <c>StreamAction.PrepareEvents</c> — and cannot use. <c>PrepareEvents</c> assigns
    ///         event versions only when <c>ExpectedVersionOnServer</c> is already set, because Marten
    ///         and Polecat let the database assign them; Fisher numbers events client-side from the
    ///         version it just read. Forcing <c>PrepareEvents</c> to number them would mean setting
    ///         <c>ExpectedVersionOnServer</c> to the current version first, which makes the optimistic
    ///         concurrency check inside the same method compare that value against itself and pass
    ///         unconditionally. Keeping the two apart is what keeps the guard real.
    ///     </para>
    ///     <para>
    ///         Each field is gated on its own <c>Enable*</c> option, because a disabled column is not
    ///         written at all — stamping the envelope anyway would report metadata on append that no
    ///         longer exists after a round trip.
    ///     </para>
    /// </remarks>
    private void ApplySessionMetadata(StreamAction stream)
    {
        foreach (var @event in stream.Events)
        {
            if (_session.CorrelationIdEnabled)
            {
                @event.CorrelationId ??= _session.CorrelationId;
            }

            if (_session.CausationIdEnabled)
            {
                @event.CausationId ??= _session.CausationId;
            }

            if (_session.UserNameEnabled)
            {
                @event.UserName ??= _session.CurrentUserName;
            }

            if (!_session.HeadersEnabled || !(_session.Headers?.Count > 0))
            {
                continue;
            }

            foreach (var header in _session.Headers)
            {
                @event.SetHeader(header.Key, header.Value);
            }
        }
    }

    /// <summary>
    ///     Number the stream's events from <paramref name="startingVersion" /> and record the resulting
    ///     stream version.
    /// </summary>
    private static void AssignVersions(StreamAction stream, long startingVersion)
    {
        var version = startingVersion;

        foreach (var @event in stream.Events)
        {
            version++;
            @event.Version = version;
        }

        stream.Version = version;
    }

    /// <summary>
    ///     The stream's current version, or null when the stream row does not exist.
    /// </summary>
    private async Task<long?> ReadCurrentVersionAsync(StreamAction stream, SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var conjoined = _graph.TenancyStyle == TenancyStyle.Conjoined;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = conjoined
            ? $"select version from {_graph.StreamsTableName} where id = @id and tenant_id = @tenant_id"
            : $"select version from {_graph.StreamsTableName} where id = @id";

        var id = _graph.StreamIdentity == StreamIdentity.AsGuid
            ? SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
            : stream.Key!;

        command.Parameters.Add(new SqliteParameter("id", id) { SqliteType = SqliteType.Text });

        if (conjoined)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", stream.TenantId)
            {
                SqliteType = SqliteType.Text
            });
        }

        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private Weasel.Storage.IStorageOperation QuickAppendEvents(StreamAction stream)
        => _graph.StreamIdentity == StreamIdentity.AsGuid
            ? ((EventStorage<Guid>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream)
            : ((EventStorage<string>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream);
}

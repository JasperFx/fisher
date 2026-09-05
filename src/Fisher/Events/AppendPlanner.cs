using System.Text.Json;
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
        var actionable = CollectActionableStreams(streams);

        if (actionable.Count == 0)
        {
            return [];
        }

        // One set-based read for every stream in the unit of work, not one scalar query per stream.
        // This read runs inside the write transaction, holding SQLite's exclusive write lock, so
        // shrinking N round trips to one is worth the most exactly here.
        var versions = await ReadCurrentVersionsAsync(actionable, connection, transaction, token)
            .ConfigureAwait(false);

        var operations = new List<Weasel.Storage.IStorageOperation>(actionable.Count);

        foreach (var stream in actionable)
        {
            var mode = PlanStream(stream, versions[stream]);

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
        var actionable = CollectActionableStreams(streams);

        if (actionable.Count == 0)
        {
            return;
        }

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);

        // One set-based read for the whole pass, mirroring PlanAsync. Deliberately NOT merged with
        // the in-transaction read and NOT used to seed it: this pass runs outside the write lock so
        // inline projections have versions to fold, and the authoritative read, the optimistic
        // concurrency check and the final numbering must all still happen under the lock — seeding
        // one from the other is exactly the race the class remarks forbid.
        var versions = await ReadCurrentVersionsAsync(actionable, connection, transaction: null, token)
            .ConfigureAwait(false);

        foreach (var stream in actionable)
        {
            AssignVersions(stream, versions[stream] ?? 0);
        }
    }

    /// <summary>
    ///     The streams that will actually be written — everything with at least one event, with the
    ///     session's metadata applied. Both planning passes share this so they agree about which
    ///     streams a version read has to cover.
    /// </summary>
    private List<StreamAction> CollectActionableStreams(IReadOnlyCollection<StreamAction> streams)
    {
        var actionable = new List<StreamAction>(streams.Count);

        foreach (var stream in streams)
        {
            if (stream.Events.Count == 0)
            {
                continue;
            }

            ApplySessionMetadata(stream);
            actionable.Add(stream);
        }

        return actionable;
    }

    private Storage.StreamWriteMode PlanStream(StreamAction stream, long? currentVersion)
    {
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
    ///     <para>
    ///         <b>This used to stamp the stream's own identity as well, and no longer needs to.</b>
    ///         fisher#72: <c>StreamAction.Append(graph, string, …)</c> appended straight to the backing
    ///         list where the <c>Guid</c> overload beside it went through <c>AddEvent</c>, so every
    ///         event appended to a string-identified stream reached an inline projection with an empty
    ///         <c>StreamKey</c> — the normal way such a projection learns which entity it is projecting,
    ///         and silent, because the document was written with a blank key rather than anything
    ///         throwing. Fixed upstream by
    ///         <see href="https://github.com/JasperFx/jasperfx/issues/663">jasperfx#663</see> and
    ///         shipped in <b>JasperFx 2.48.0</b>, which routes both string overloads through
    ///         <c>AddEvents</c>; the workaround here is gone, and the behaviour is pinned by
    ///         <c>event_envelope_metadata_in_projections</c> rather than by this method. Do not
    ///         reintroduce it — and note that removing it is what makes 2.48.0 a real floor rather than
    ///         a preference.
    ///     </para>
    /// </remarks>
    private void ApplySessionMetadata(StreamAction stream)
    {
        // One reading per stream, not per event: the events of one append share a moment the
        // same way they share a transaction. Guarded so a caller that stamped its own value
        // (or a retry re-entering this method) is left alone — and mirrored by the append
        // operation, which persists this value instead of the column default whenever it is
        // set, so what an inline projection folded is what a rebuild will read back.
        // Round-tripped through the storage format up front: the column keeps milliseconds,
        // so stamping raw ticks here would leave the inline view sub-millisecond ahead of
        // every later read of the same event.
        var timestamp = SqliteTimestamp.FromDatabaseValue(
            SqliteTimestamp.ToDatabaseValue(_graph.TimeProvider.GetUtcNow()));

        foreach (var @event in stream.Events)
        {
            if (@event.Timestamp == default)
            {
                @event.Timestamp = timestamp;
            }

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
    ///     Every stream's current version in one set-based read — null for a stream whose row does not
    ///     exist.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Replaces one interpolated scalar query per stream, which with inline projections on cost
    ///         a multi-stream save 2N round trips — N of them under the exclusive write lock. The ids
    ///         travel as a JSON array unpacked by <c>json_each</c>, the same shape
    ///         <c>FisherDocumentStorage</c>'s load-many uses, so the parameter count does not vary with
    ///         the stream count.
    ///     </para>
    ///     <para>
    ///         Semantics are exactly the per-stream read's: a missing row reads as null (create, or
    ///         collide on <c>Start</c>), and there is deliberately no <c>is_archived</c> filter — an
    ///         archived stream's version is still its version, as before.
    ///     </para>
    ///     <para>
    ///         Under conjoined tenancy the read is one statement per distinct tenant in the unit of
    ///         work, because the row key is <c>(tenant_id, id)</c> and one save can span tenants via
    ///         <c>ForTenant</c>. A single-tenant save — the ordinary case — is still one statement.
    ///     </para>
    /// </remarks>
    private async Task<Dictionary<StreamAction, long?>> ReadCurrentVersionsAsync(
        IReadOnlyList<StreamAction> streams, SqliteConnection connection, SqliteTransaction? transaction,
        CancellationToken token)
    {
        var versions = new Dictionary<StreamAction, long?>(streams.Count);

        foreach (var stream in streams)
        {
            versions[stream] = null;
        }

        if (_graph.TenancyStyle == TenancyStyle.Conjoined)
        {
            foreach (var tenant in streams.GroupBy(x => x.TenantId))
            {
                await ReadVersionsAsync([.. tenant], tenant.Key, connection, transaction, versions, token)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await ReadVersionsAsync(streams, tenantId: null, connection, transaction, versions, token)
                .ConfigureAwait(false);
        }

        return versions;
    }

    private async Task ReadVersionsAsync(IReadOnlyList<StreamAction> streams, string? tenantId,
        SqliteConnection connection, SqliteTransaction? transaction,
        Dictionary<StreamAction, long?> versions, CancellationToken token)
    {
        // Keyed by the database's own rendering of the id — for a Guid identity that is the lowercase
        // canonical text SqliteStorageDialect writes, so the row that comes back matches the key that
        // went in without any per-row parsing.
        var byDatabaseId = new Dictionary<string, StreamAction>(streams.Count, StringComparer.Ordinal);

        foreach (var stream in streams)
        {
            byDatabaseId[DatabaseIdFor(stream)] = stream;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = tenantId is null
            ? $"select id, version from {_graph.StreamsTableName} where id in (select value from json_each(@ids))"
            : $"select id, version from {_graph.StreamsTableName} where tenant_id = @tenant_id and id in (select value from json_each(@ids))";

        command.Parameters.Add(new SqliteParameter("ids", JsonSerializer.Serialize(byDatabaseId.Keys))
        {
            SqliteType = SqliteType.Text
        });

        if (tenantId is not null)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", tenantId) { SqliteType = SqliteType.Text });
        }

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            versions[byDatabaseId[reader.GetString(0)]] = reader.GetInt64(1);
        }
    }

    private string DatabaseIdFor(StreamAction stream)
        => _graph.StreamIdentity == StreamIdentity.AsGuid
            ? (string)SqliteStorageDialect<Guid>.ToDatabaseValue(stream.Id)
            : stream.Key!;

    private Weasel.Storage.IStorageOperation QuickAppendEvents(StreamAction stream)
        => _graph.StreamIdentity == StreamIdentity.AsGuid
            ? ((EventStorage<Guid>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream)
            : ((EventStorage<string>)_graph.ClosedShapeEventStorage).QuickAppendEvents(stream);
}

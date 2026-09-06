using System.Linq.Expressions;
using System.Text;
using Fisher.Events.Internal;
using Fisher.Events.Storage;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Events;

/// <summary>
///     The DCB tag read surface.
/// </summary>
public partial class EventOperations
{
    /// <summary>
    ///     Start a batch of DCB reads to be run together.
    /// </summary>
    /// <remarks>
    ///     See <see cref="Batching.IBatchedQuery" /> for why this exists on an embedded database, where the
    ///     round-trip argument the siblings make does not apply.
    /// </remarks>
    public Batching.IBatchedQuery CreateBatchQuery() => new Batching.FisherBatchedQuery(this, _session);

    /// <summary>
    ///     Fold every event matching the tag query into an aggregate, or null when none match.
    /// </summary>
    /// <remarks>
    ///     Live aggregation over a cross-stream event set. There is no snapshot to read instead — a DCB
    ///     aggregate is defined by a tag query rather than by a stream, so nothing has stored it under
    ///     an identity this could look up.
    /// </remarks>
    public async Task<T?> AggregateByTagsAsync<T>(EventTagQuery query, CancellationToken cancellation = default)
        where T : class
    {
        var events = await QueryByTagsAsync(query, cancellation).ConfigureAwait(false);

        return events.Count == 0
            ? null
            : await Graph.AggregatorFor<T>().BuildAsync(events, _session, null, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    ///     Open a writable boundary over every stream the tag query reaches.
    /// </summary>
    /// <remarks>
    ///     The returned boundary records the highest sequence it saw. <c>SaveChangesAsync</c> re-runs
    ///     the query inside its write transaction and fails with
    ///     <see cref="DcbConcurrencyException" /> if anything matching has been appended since — see
    ///     <c>FisherSession.AssertBoundariesAreStillConsistentAsync</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The query has no conditions.</exception>
    public async Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query,
        CancellationToken cancellation = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        // An unconditioned query matches nothing, so a boundary over it would assert nothing and
        // route nothing. Refusing is better than handing back a boundary that silently cannot fail.
        if (query.Conditions.Count == 0)
        {
            throw new ArgumentException(
                "A DCB boundary needs at least one tag condition; an empty query matches no events and "
                + "would assert nothing on save.", nameof(query));
        }

        var events = await QueryByTagsAsync(query, cancellation).ConfigureAwait(false);

        var aggregate = events.Count == 0
            ? null
            : await Graph.AggregatorFor<T>().BuildAsync(events, _session, null, cancellation).ConfigureAwait(false);

        var boundary = new Tags.FisherEventBoundary<T>(_session, Graph, query, aggregate, events);
        _session.TrackBoundary(boundary.Query, boundary.LastSeenSequence);

        return boundary;
    }

    /// <summary>
    ///     Retroactively apply a tag to every already-persisted event matching a predicate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The predicate is translated by the same <c>WhereClauseParser</c> the document LINQ layer
    ///         uses, over an <see cref="EventMemberFactory" /> that resolves <see cref="IEvent" />
    ///         members to <c>fi_events</c> columns instead of <c>json_extract</c> paths. That is how
    ///         Marten builds this feature too, and it is why the LINQ layer came first: a bespoke
    ///         translator here would have been thrown away.
    ///     </para>
    ///     <para>
    ///         Queued rather than executed, so the tagging commits in the same transaction as whatever
    ///         else the session is doing.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The tag's type is not registered on this store.</exception>
    public void AssignTagWhere(Expression<Func<IEvent, bool>> expression, object tag)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(tag);

        var registration = Graph.FindTagType(tag.GetType())
                           ?? throw new InvalidOperationException(
                               $"Tag type '{tag.GetType().Name}' is not registered on this event store. Call "
                               + $"RegisterTagType<{tag.GetType().Name}>() first.");

        var predicate = new WhereClauseParser(new EventMemberFactory(Graph)).Parse(expression.Body);

        _session.QueueOperation(new AssignTagWhereOperation(Graph, registration,
            EventTagWriter.ToDatabaseValue(registration.ExtractValue(tag)), predicate));
    }

    /// <summary>
    ///     Every event matching any of the query's conditions, in global sequence order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Conditions are OR'd, as <see cref="EventTagQuery" /> defines. Each becomes a
    ///         <c>seq_id in (select seq_id from &lt;tag table&gt; where value = ?)</c> subselect —
    ///         chosen over a join because a join against several tag tables multiplies rows when an
    ///         event carries more than one matching tag, and the caller expects each event once.
    ///     </para>
    ///     <para>
    ///         Ordering is by <c>seq_id</c>: a tag query spans streams, so version is not a global
    ///         order and only the append sequence is.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<IEvent>> QueryByTagsAsync(EventTagQuery query,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Conditions.Count == 0)
        {
            return [];
        }

        var options = _session.Options.Events;

        var sql = new StringBuilder("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(options))
            .Append(" from ")
            .Append(Graph.EventsTableName)
            .Append(" where ");

        await using var command = (await _session.ConnectionAsync(cancellation).ConfigureAwait(false))
            .CreateCommand();

        AppendConditions(query, sql, command);

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
            command.Parameters.AddWithValue("@tenant_id", TenantId);
        }

        sql.Append(" order by seq_id");

        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        // Empty stream id: a tag query spans streams, so each event's identity comes off its own row
        // rather than from the context. See FisherEventsRowReader.ReadEventAcrossStreams.
        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;

        var results = new List<IEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            var @event = await FisherEventsRowReader
                .ReadEventAcrossStreams(reader, ctx, slots, isGuid, cancellation).ConfigureAwait(false);

            // Null means dotnet_type named a type this process cannot resolve; skip it, as the
            // stream reads do.
            if (@event is not null)
            {
                results.Add(@event);
            }
        }

        return results;
    }

    /// <summary>
    ///     Whether any event matches the query.
    /// </summary>
    /// <remarks>
    ///     The same predicate as <see cref="QueryByTagsAsync" /> wrapped in <c>select exists (…)</c>,
    ///     which stops at the first match instead of materialising every row. SQLite has no boolean, so
    ///     the result is an INTEGER 0/1.
    /// </remarks>
    public async Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Conditions.Count == 0)
        {
            return false;
        }

        var sql = new StringBuilder("select exists (select 1 from ")
            .Append(Graph.EventsTableName)
            .Append(" where ");

        await using var command = (await _session.ConnectionAsync(cancellation).ConfigureAwait(false))
            .CreateCommand();

        AppendConditions(query, sql, command);

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
            command.Parameters.AddWithValue("@tenant_id", TenantId);
        }

        sql.Append(')');

        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        var result = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
        return Convert.ToInt64(result) != 0;
    }

    /// <summary>
    ///     Whether any event matching the query has a sequence above <paramref name="lastSeenSequence" />
    ///     — the DCB consistency check.
    /// </summary>
    /// <remarks>
    ///     Takes the connection and transaction explicitly rather than reaching for the session's,
    ///     because this runs inside <c>SaveChangesAsync</c>'s write transaction and must be enrolled in
    ///     it. A command on the same connection but outside the transaction would not see the lock's
    ///     guarantee.
    /// </remarks>
    internal async Task<bool> AnyMatchingEventBeyondAsync(EventTagQuery query, long lastSeenSequence,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        if (query.Conditions.Count == 0)
        {
            return false;
        }

        var sql = new StringBuilder("select exists (select 1 from ")
            .Append(Graph.EventsTableName)
            .Append(" where ");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        AppendConditions(query, sql, command);

        sql.Append(" and seq_id > @last_seen");
        command.Parameters.AddWithValue("@last_seen", lastSeenSequence);

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
            command.Parameters.AddWithValue("@tenant_id", TenantId);
        }

        sql.Append(')');
        command.CommandText = sql.ToString();

        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
    }

    /// <summary>
    ///     Render the query's OR'd conditions, binding each tag value as a parameter.
    /// </summary>
    private void AppendConditions(EventTagQuery query, StringBuilder sql, SqliteCommand command)
    {
        sql.Append('(');

        for (var i = 0; i < query.Conditions.Count; i++)
        {
            var condition = query.Conditions[i];

            if (i > 0)
            {
                sql.Append(" or ");
            }

            var registration = Graph.FindTagType(condition.TagType)
                               ?? throw new InvalidOperationException(
                                   $"Tag type '{condition.TagType.Name}' is not registered on this event store. "
                                   + $"Call RegisterTagType<{condition.TagType.Name}>() before querying by it.");

            var parameterName = $"@tag{i}";

            sql.Append("(seq_id in (select seq_id from ")
                .Append(Graph.TagTableName(registration))
                .Append(" where value = ")
                .Append(parameterName)
                .Append(')');

            command.Parameters.AddWithValue(parameterName,
                EventTagWriter.ToDatabaseValue(registration.ExtractValue(condition.TagValue)));

            // A condition may additionally narrow to one event type. Matching on the stored
            // event_type_name rather than the .NET type name, so a renamed CLR type with a stable
            // alias still matches.
            if (condition.EventType != null)
            {
                var typeParameter = $"@type{i}";
                sql.Append(" and type = ").Append(typeParameter);
                command.Parameters.AddWithValue(typeParameter,
                    Graph.EventMappingFor(condition.EventType).EventTypeName);
            }

            sql.Append(')');
        }

        sql.Append(')');
    }
}

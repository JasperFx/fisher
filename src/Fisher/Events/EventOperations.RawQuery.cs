using System.Linq.Expressions;
using Fisher.Events.Internal;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     Querying <c>fi_events</c> rows directly by a predicate over <see cref="IEvent" />.
/// </summary>
public partial class EventOperations
{
    /// <summary>
    ///     Every event matching a predicate over its own metadata, in global sequence order.
    /// </summary>
    /// <param name="filter">
    ///     A predicate over <see cref="IEvent" />'s members — <c>Sequence</c>, <c>StreamId</c>,
    ///     <c>Timestamp</c>, <c>EventTypeName</c> and the rest. Members of the event <em>body</em> are
    ///     not reachable: the body is JSON of a type the row only names, so there is nothing to resolve
    ///     a path against.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Marten spells this <c>QueryAllRawEvents()</c> and returns an <c>IQueryable&lt;IEvent&gt;</c>.
    ///         Fisher takes a predicate instead, because its LINQ provider is built over document storage
    ///         and an <see cref="IEvent" /> queryable would need a parallel provider to serve one caller.
    ///         The predicate half is the part that carries the weight, and it is already shared: the same
    ///         <see cref="WhereClauseParser" /> the document layer uses, over an
    ///         <see cref="EventMemberFactory" /> that resolves <see cref="IEvent" /> members to
    ///         <c>fi_events</c> columns rather than <c>json_extract</c> paths. That is the same pair
    ///         <c>AssignTagWhere</c> runs on.
    ///     </para>
    ///     <para>
    ///         Ordering is by <c>seq_id</c>. This spans streams, so version is not a global order — the
    ///         same reason the tag queries order that way, and why rows go through
    ///         <see cref="FisherEventsRowReader.ReadEventAcrossStreams" />, which takes each event's
    ///         identity from its own row rather than from the hydration context.
    ///     </para>
    ///     <para>
    ///         An unresolvable <c>dotnet_type</c> is skipped, as the stream reads do — a deployment can
    ///         read events it does not know about. That is the opposite of the daemon's loader, which
    ///         must not skip.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<IEvent>> QueryEventsAsync(Expression<Func<IEvent, bool>> filter,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var options = _session.Options.Events;
        var predicate = new WhereClauseParser(new EventMemberFactory(Graph)).Parse(filter.Body);

        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append("select ");
        builder.Append(FisherEventsRowReader.ComposeSelectColumns(options));
        builder.Append(" from ");
        builder.Append(Graph.EventsTableName);
        builder.Append(" where ");

        predicate.Apply(builder);

        if (IsConjoined)
        {
            builder.Append(" and tenant_id = ");
            builder.AppendParameter(TenantId);
        }

        builder.Append(" order by seq_id");

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;

        var results = new List<IEvent>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var @event = FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots, isGuid);

            if (@event is not null)
            {
                results.Add(@event);
            }
        }

        return results;
    }

    /// <summary>
    ///     One page of events matching an <see cref="EventQuery" />, in global sequence order, with the
    ///     total matching count.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The paging read behind <see cref="IReadOnlyEventStore" /> — CritterWatch's Event Explorer
    ///         is the caller. Unlike the predicate overload above, <see cref="EventQuery" /> is a flat
    ///         bag of optional exact-match filters rather than an expression, so no
    ///         <see cref="Fisher.Linq.Parsing.WhereClauseParser" /> is involved: every field maps to one
    ///         <c>fi_events</c> column and an <c>=</c>.
    ///     </para>
    ///     <para>
    ///         <b>The three metadata filters are gated on the options that create their columns.</b>
    ///         <c>correlation_id</c>, <c>causation_id</c> and <c>user_name</c> only exist when the
    ///         matching <c>Enable*</c> option is on, so filtering on one otherwise is not merely
    ///         unhelpful — it is a <c>no such column</c> error. Ignoring the filter is what
    ///         <see cref="EventQuery" /> asks for ("only honored when the store advertises and captures
    ///         the metadata column"), and is what Polecat does.
    ///     </para>
    ///     <para>
    ///         <c>TenantId</c> is ignored: Fisher's multi-tenancy stops at a tenant id column and
    ///         <see cref="EventQuery" /> says a store without a tenant dimension ignores it. The
    ///         session's own tenant scope still applies on a conjoined store, as it does everywhere else.
    ///     </para>
    /// </remarks>
    public async Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var options = _session.Options.Events;

        // EventQuery's own defaults are 1 and 50, but it is a mutable class a caller can leave at zero.
        var pageNumber = query.PageNumber <= 0 ? 1 : query.PageNumber;
        var pageSize = query.PageSize <= 0 ? 50 : query.PageSize;

        var count = await CountMatchingEventsAsync(query, token).ConfigureAwait(false);

        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append("select ");
        builder.Append(FisherEventsRowReader.ComposeSelectColumns(options));
        builder.Append(" from ");
        builder.Append(Graph.EventsTableName);

        AppendEventQueryFilters(builder, query);

        // Ordering by seq_id because this spans streams, where version is not a global order — the same
        // reason the tag queries and the predicate overload order that way.
        builder.Append(" order by seq_id limit ");
        builder.AppendParameter(pageSize);
        builder.Append(" offset ");
        builder.AppendParameter((pageNumber - 1) * pageSize);

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;

        var events = new List<IEvent>();

        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var @event = FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots, isGuid);

                if (@event is not null)
                {
                    events.Add(@event);
                }
            }
        }

        return new PagedEvents
        {
            Events = events,
            TotalCount = count,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <summary>
    ///     How many events the query matches, ignoring its paging.
    /// </summary>
    /// <remarks>
    ///     A second statement rather than a window function over the page, because <c>count(*) over ()</c>
    ///     would return nothing at all for a page past the end — and "page 9 of a 3-page result" is
    ///     exactly when a tool most needs to be told the real total.
    /// </remarks>
    private async Task<int> CountMatchingEventsAsync(EventQuery query, CancellationToken token)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append("select count(*) from ");
        builder.Append(Graph.EventsTableName);

        AppendEventQueryFilters(builder, query);

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        var raw = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

        return raw is null or DBNull ? 0 : Convert.ToInt32(raw);
    }

    /// <summary>
    ///     Append the query's <c>where</c> clause, shared by the page read and the count so the two
    ///     cannot disagree about what matches.
    /// </summary>
    private void AppendEventQueryFilters(Weasel.Sqlite.CommandBuilder builder, EventQuery query)
    {
        var options = _session.Options.Events;
        var first = true;

        void Clause(string column, object value)
        {
            builder.Append(first ? " where " : " and ");
            builder.Append(column);
            builder.Append(" = ");
            builder.AppendParameter(value);
            first = false;
        }

        if (query.EventTypeName is not null)
        {
            Clause("type", query.EventTypeName);
        }

        if (query.StreamId is not null)
        {
            // Under Guid identity the parse normalises casing to the lowercase canonical form the column
            // holds — SQLite's default collation is case-sensitive, so an uppercase Guid string matches
            // nothing. Same trap SqliteGuidIdentification exists for, and the same normalisation
            // GetStreamMetadataAsync does. An unparseable value under Guid identity is left as text, so
            // it matches nothing rather than throwing at a monitoring tool.
            if (IsGuidIdentity)
            {
                if (Guid.TryParse(query.StreamId, out var streamId))
                {
                    Clause("stream_id", streamId.ToString());
                }
                else
                {
                    Clause("stream_id", query.StreamId);
                }
            }
            else
            {
                Clause("stream_id", query.StreamId);
            }
        }

        if (query.CorrelationId is not null && options.EnableCorrelationId)
        {
            Clause("correlation_id", query.CorrelationId);
        }

        if (query.CausationId is not null && options.EnableCausationId)
        {
            Clause("causation_id", query.CausationId);
        }

        if (query.UserName is not null && options.EnableUserName)
        {
            Clause("user_name", query.UserName);
        }

        if (IsConjoined)
        {
            Clause("tenant_id", TenantId);
        }
    }

    /// <summary>
    ///     The bodies of every event of one type matching a predicate over the body itself (fisher#41).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The counterpart to <see cref="QueryEventsAsync(Expression{Func{IEvent,bool}},CancellationToken)" />,
    ///         which queries an event's <em>metadata</em>. That method's doc comment says a body member
    ///         is unreachable because "the body is JSON of a type the row only names" — true of
    ///         <see cref="IEvent" /> in general, and false once the caller names the type, which is what
    ///         this overload does.
    ///     </para>
    ///     <para>
    ///         <b>It needed no new SQL machinery.</b> An event body is a JSON document in a TEXT column
    ///         called <c>data</c> — structurally identical to a document — so <c>MemberFactory</c>'s
    ///         locators apply verbatim against <c>fi_events</c>. There is no <c>DocumentMapping</c>
    ///         involved at all — most event types have no identity member, and asking for a mapping
    ///         would register the event type as a document and give it a table.
    ///     </para>
    ///     <para>
    ///         <b>This is a scan.</b> There is no index over <c>fi_events.data</c>, which is honest for a
    ///         diagnostic or reporting query. fisher#16's expression indexes are the mechanism if one
    ///         ever needs to be fast, and they would apply here unchanged.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<T>> QueryEventDataAsync<T>(Expression<Func<T, bool>> filter,
        CancellationToken token = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(filter);

        // No DocumentMapping at all: most event types have no identity member and DocumentMapping
        // refuses a type without one — and asking for a mapping would register the event type as a
        // document, giving it a table in the next migration.
        var members = new Linq.Members.MemberFactory(_session.Options);
        var predicate = new WhereClauseParser(members).Parse(filter.Body);

        var builder = new Weasel.Sqlite.CommandBuilder();
        builder.Append("select data from ");
        builder.Append(Graph.EventsTableName);

        // The type filter is the alias, not dotnet_type: the alias is short and stable where
        // dotnet_type is assembly-qualified and brittle across a rename. Same reasoning as fisher#17's
        // doc_type discriminator.
        builder.Append(" where type = ");
        builder.AppendParameter(Graph.EventMappingFor(typeof(T)).EventTypeName);
        builder.Append(" and ");

        predicate.Apply(builder);

        if (IsConjoined)
        {
            builder.Append(" and tenant_id = ");
            builder.AppendParameter(TenantId);
        }

        builder.Append(" order by seq_id");

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        var results = new List<T>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var body = _session.FisherSerializer.FromJson<T>(reader.GetString(0));

            if (body is not null)
            {
                results.Add(body);
            }
        }

        return results;
    }
}

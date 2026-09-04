using System.Linq.Expressions;
using Fisher.Events.Internal;
using Fisher.Events.Storage;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Tags;

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
    ///         bag of optional filters rather than an expression, so no
    ///         <see cref="Fisher.Linq.Parsing.WhereClauseParser" /> is involved: the exact-match fields
    ///         each map to one <c>fi_events</c> column and an <c>=</c>, the two inclusive windows map to
    ///         <c>&gt;=</c>/<c>&lt;=</c> pairs, and the tag conditions reuse the
    ///         <c>seq_id in (select …)</c> shape <see cref="QueryByTagsAsync" /> established.
    ///     </para>
    ///     <para>
    ///         <b>The three metadata filters are gated on the options that create their columns —
    ///         by REFUSAL, not by silence (jasperfx#737).</b> <c>correlation_id</c>,
    ///         <c>causation_id</c> and <c>user_name</c> only exist when the matching <c>Enable*</c>
    ///         option is on, so filtering on one otherwise is not merely unhelpful — it is a
    ///         <c>no such column</c> error. Such a filter used to be silently ignored, as
    ///         <see cref="EventQuery" />'s field docs once permitted; the jasperfx#737 guard rail
    ///         reverses that, because unfiltered results read as filtered. The store now declares only
    ///         the filters its configuration can honor and
    ///         <see cref="EventQuery.AssertFiltersAreSupported" /> throws a
    ///         <see cref="NotSupportedException" /> naming the field.
    ///     </para>
    ///     <para>
    ///         <b><c>TenantId</c> selects the tenant partition on a conjoined store.</b> When supplied it
    ///         replaces the session's own tenant scope — <see cref="IReadOnlyEventStore" /> reads through
    ///         a default-tenant session, and the whole point of the field (jasperfx#555) is to let the
    ///         Event Explorer scope that read to one tenant. When null, the session's own scope applies,
    ///         as it does everywhere else. On a store without conjoined tenancy the field is ignored,
    ///         which is what <see cref="EventQuery" /> documents for a store without a tenant dimension —
    ///         the one documented ignore, not a guard-rail violation.
    ///     </para>
    ///     <para>
    ///         The timestamp window compares ISO-8601 text. That is sound because
    ///         <see cref="SqliteTimestamp" /> renders every stored value and both bounds in one
    ///         fixed-width UTC format, which orders lexicographically exactly as it orders temporally —
    ///         the property the column format was chosen for. Bounds are rendered at the store's own
    ///         millisecond precision, so a bound taken from a read-back <see cref="IEvent.Timestamp" />
    ///         compares equal to its own row, which is what makes the window inclusive in practice.
    ///     </para>
    /// </remarks>
    public async Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // jasperfx#737: refuse — loudly, naming the field — any supplied filter this store's
        // configuration cannot honor, rather than returning unfiltered results that read as filtered.
        query.AssertFiltersAreSupported(SupportedEventQueryFilters());

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
    ///     The <see cref="EventQueryFilters" /> this store honors, given its configuration — what the
    ///     jasperfx#737 guard rail asserts against.
    /// </summary>
    /// <remarks>
    ///     Everything except the metadata columns the store was not configured to write:
    ///     <c>correlation_id</c>, <c>causation_id</c> and <c>user_name</c> only exist on
    ///     <c>fi_events</c> when the matching <c>Enable*</c> option was on when the schema was built,
    ///     so a filter over a missing one cannot be honored and must be refused.
    ///     <see cref="EventQueryFilters.TenantId" /> stays declared even without conjoined tenancy:
    ///     <see cref="EventQuery.TenantId" /> documents that a store without a tenant dimension ignores
    ///     it, so ignoring there is the field's contract rather than a silent drop.
    /// </remarks>
    private EventQueryFilters SupportedEventQueryFilters()
    {
        var options = _session.Options.Events;
        var filters = EventQueryFilters.All;

        if (!options.EnableCorrelationId)
        {
            filters &= ~EventQueryFilters.CorrelationId;
        }

        if (!options.EnableCausationId)
        {
            filters &= ~EventQueryFilters.CausationId;
        }

        if (!options.EnableUserName)
        {
            filters &= ~EventQueryFilters.UserName;
        }

        return filters;
    }

    /// <summary>
    ///     Append the query's <c>where</c> clause, shared by the page read and the count so the two
    ///     cannot disagree about what matches.
    /// </summary>
    private void AppendEventQueryFilters(Weasel.Sqlite.CommandBuilder builder, EventQuery query)
    {
        var first = true;

        void Prefix()
        {
            builder.Append(first ? " where " : " and ");
            first = false;
        }

        void Clause(string column, string comparison, object value)
        {
            Prefix();
            builder.Append(column);
            builder.Append(comparison);
            builder.AppendParameter(value);
        }

        // One code path for both spellings of the event type filter, so the single/plural union
        // semantics stay upstream in CombinedEventTypeNames (jasperfx#737): distinct names OR'd, an
        // unknown name contributing nothing. `in` gives both for free, and a duplicated name cannot
        // double-count because the filter selects rows rather than joining against them.
        var eventTypeNames = query.CombinedEventTypeNames();
        if (eventTypeNames.Count == 1)
        {
            Clause("type", " = ", eventTypeNames[0]);
        }
        else if (eventTypeNames.Count > 1)
        {
            Prefix();
            builder.Append("type in (");
            for (var i = 0; i < eventTypeNames.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.AppendParameter(eventTypeNames[i]);
            }

            builder.Append(')');
        }

        if (query.StreamId is not null)
        {
            // Under Guid identity the parse normalises casing to the lowercase canonical form the column
            // holds — SQLite's default collation is case-sensitive, so an uppercase Guid string matches
            // nothing. Same trap SqliteGuidIdentification exists for, and the same normalisation
            // GetStreamMetadataAsync does. An unparseable value under Guid identity is left as text, so
            // it matches nothing rather than throwing at a monitoring tool.
            if (IsGuidIdentity && Guid.TryParse(query.StreamId, out var streamId))
            {
                Clause("stream_id", " = ", streamId.ToString());
            }
            else
            {
                Clause("stream_id", " = ", query.StreamId);
            }
        }

        // No Enable* gates here any more: AssertFiltersAreSupported already refused a metadata filter
        // this store's schema cannot answer, so reaching one of these means the column exists.
        if (query.CorrelationId is not null)
        {
            Clause("correlation_id", " = ", query.CorrelationId);
        }

        if (query.CausationId is not null)
        {
            Clause("causation_id", " = ", query.CausationId);
        }

        if (query.UserName is not null)
        {
            Clause("user_name", " = ", query.UserName);
        }

        // The inclusive timestamp window (jasperfx#737). Text comparison over SqliteTimestamp's one
        // fixed-width UTC format, in which lexicographic and temporal order coincide — see the method
        // remarks. Bounds render at the store's own millisecond precision, so a bound equal to a
        // stored value compares equal and the window is inclusive at both ends.
        if (query.TimestampFrom is not null)
        {
            Clause("timestamp", " >= ", SqliteTimestamp.ToDatabaseValue(query.TimestampFrom.Value));
        }

        if (query.TimestampTo is not null)
        {
            Clause("timestamp", " <= ", SqliteTimestamp.ToDatabaseValue(query.TimestampTo.Value));
        }

        // The inclusive sequence window (jasperfx#737). An inverted window — floor above ceiling — is
        // two contradictory comparisons on one column: a well-formed range containing nothing, which
        // is exactly the contract (an empty page and TotalCount 0, never an error).
        if (query.SequenceFloor is not null)
        {
            Clause("seq_id", " >= ", query.SequenceFloor.Value);
        }

        if (query.SequenceCeiling is not null)
        {
            Clause("seq_id", " <= ", query.SequenceCeiling.Value);
        }

        if (query.TagConditions is not null)
        {
            Prefix();
            AppendTagConditionsFilter(builder, query.TagConditions);
        }

        if (IsConjoined)
        {
            // The query's tenant selects the partition when supplied (jasperfx#555 — the read-only
            // store reads through a default-tenant session, and this field is how the Event Explorer
            // scopes it); the session's own scope applies otherwise, as it does everywhere else.
            Clause("tenant_id", " = ", query.TenantId ?? TenantId);
        }
    }

    /// <summary>
    ///     Render <see cref="EventQuery.TagConditions" /> — the wire form of a DCB tag query — as one
    ///     parenthesised group AND-composed with the rest of the query's filters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The spec's types are resolved against this store's registered tag and event graph, the
    ///         same graph <see cref="QueryByTagsAsync" /> queries by CLR type; an unregistered type is a
    ///         loud <see cref="UnknownTagQueryTypeException" /> rather than an empty answer, because
    ///         "that tag type does not exist here" and "no event carries that tag" must not read alike.
    ///     </para>
    ///     <para>
    ///         Each condition is the same <c>seq_id in (select seq_id from &lt;tag table&gt; …)</c>
    ///         subselect <see cref="QueryByTagsAsync" /> uses, OR'd — chosen there over a join because a
    ///         join multiplies rows when an event carries more than one matching tag, and here that
    ///         choice is additionally what keeps <see cref="PagedEvents.TotalCount" /> counting distinct
    ///         events rather than condition hits.
    ///     </para>
    /// </remarks>
    private void AppendTagConditionsFilter(Weasel.Sqlite.CommandBuilder builder, EventTagQuerySpec spec)
    {
        var knownTypes = Graph.TagTypes.Select(x => x.TagType)
            .Concat(Graph.AllKnownEventTypes().Select(x => x.EventType));

        var tagQuery = spec.Resolve(EventTagQuerySpec.ResolverFor(knownTypes));

        if (tagQuery.Conditions.Count == 0)
        {
            // Zero OR'd conditions select no events — the same answer FetchForWritingByTags refuses to
            // build a boundary over and QueryByTagsAsync returns [] for. Rendered as a false predicate
            // rather than skipped, because a supplied filter must never widen the result.
            builder.Append("0 = 1");
            return;
        }

        builder.Append('(');

        for (var i = 0; i < tagQuery.Conditions.Count; i++)
        {
            var condition = tagQuery.Conditions[i];

            if (i > 0)
            {
                builder.Append(" or ");
            }

            var registration = Graph.FindTagType(condition.TagType)
                               ?? throw new InvalidOperationException(
                                   $"Tag type '{condition.TagType.Name}' is not registered on this event store. "
                                   + $"Call RegisterTagType<{condition.TagType.Name}>() before querying by it.");

            builder.Append("(seq_id in (select seq_id from ");
            builder.Append(Graph.TagTableName(registration));
            builder.Append(" where value = ");
            builder.AppendParameter(EventTagWriter.ToDatabaseValue(registration.ExtractValue(condition.TagValue)));
            builder.Append(')');

            // A condition may additionally narrow to one event type. Matching on the stored
            // event_type_name rather than the .NET type name, so a renamed CLR type with a stable
            // alias still matches — the same rule as AppendConditions on the CLR-typed path.
            if (condition.EventType is not null)
            {
                builder.Append(" and type = ");
                builder.AppendParameter(Graph.EventMappingFor(condition.EventType).EventTypeName);
            }

            builder.Append(')');
        }

        builder.Append(')');
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
        Graph.AssertBodyIsQueryable(typeof(T), "QueryEventDataAsync");

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

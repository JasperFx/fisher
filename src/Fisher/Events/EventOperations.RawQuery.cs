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
            builder.AppendParameter(_session.TenantId);
        }

        builder.Append(" order by seq_id");

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, _session.TenantId);
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
}

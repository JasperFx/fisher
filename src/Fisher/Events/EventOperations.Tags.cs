using System.Text;
using Fisher.Events.Internal;
using Fisher.Events.Storage;
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
            command.Parameters.AddWithValue("@tenant_id", _session.TenantId);
        }

        sql.Append(" order by seq_id");

        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        // Empty stream id: a tag query spans streams, so each event's identity comes off its own row
        // rather than from the context. See FisherEventsRowReader.ReadEventAcrossStreams.
        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, _session.TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;

        var results = new List<IEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            var @event = FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots, isGuid);

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
            command.Parameters.AddWithValue("@tenant_id", _session.TenantId);
        }

        sql.Append(')');

        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        var result = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
        return Convert.ToInt64(result) != 0;
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

using System.Text;
using Fisher.Events.Internal;
using Weasel.Storage;

namespace Fisher.Events.Daemon;

/// <summary>
///     Fisher's <see cref="IEventPagingDialect" />: how <c>fi_events</c> is paged by sequence for the shared
///     <see cref="EventLoaderBase" />.
/// </summary>
/// <remarks>
///     <para>
///         The column list is <see cref="FisherEventsRowReader.ComposeSelectColumns" />, which is what
///         <see cref="FisherEventLoader" /> reads back, with <c>seq_id</c> at ordinal 0.
///     </para>
///     <para>
///         ⚠️ <b>An upcast target's SOURCE names are allowed alongside it</b> (fisher#191). A projection that
///         folds the new schema declares the new types, and every row written before the migration carries
///         the OLD name, so a shard filtered on the declared set alone reads nothing from the history it was
///         pointed at, silently, and reports itself caught up. The shared
///         <see cref="EventTypeAllowList" /> is built from CLR types and has no way to name an alias whose
///         type was deleted, which is the whole point of an upcast, so the widening happens here.
///     </para>
///     <para>
///         The filter is pushed into SQL so non-matching rows never leave SQLite; the ceiling accounting
///         stays right because a page that does not fill the batch really has scanned the whole range.
///     </para>
/// </remarks>
internal sealed class SqliteEventPagingDialect : IEventPagingDialect
{
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly Dictionary<string, List<string>> _upcastSourcesByTarget = new(StringComparer.Ordinal);

    internal SqliteEventPagingDialect(StoreOptions options)
    {
        _options = options;
        _events = options.EventGraph;

        foreach (var transformation in _events.Upcasters.AllTransformations)
        {
            var target = _events.EventMappingFor(transformation.EventType).EventTypeName;
            if (!_upcastSourcesByTarget.TryGetValue(target, out var sources))
            {
                _upcastSourcesByTarget[target] = sources = [];
            }

            if (!sources.Contains(transformation.EventTypeName, StringComparer.Ordinal))
            {
                sources.Add(transformation.EventTypeName);
            }
        }
    }

    public EventPageCommand BuildPageCommand(EventPageQuery query)
    {
        var parameters = new List<EventPageParameter>();
        var sql = new StringBuilder("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(_options.Events))
            .Append(" from ").Append(_events.EventsTableName);

        appendWhere(sql, parameters, query);

        sql.Append(" order by seq_id limit @batch_size");
        parameters.Add(new EventPageParameter("@batch_size", query.BatchSize));

        return new EventPageCommand(sql.ToString(), parameters);
    }

    // `order by seq_id limit 1`, never min(seq_id), per the contract (marten#5277).
    public EventPageCommand BuildNextMatchingSequenceCommand(EventPageQuery query)
    {
        var parameters = new List<EventPageParameter>();
        var sql = new StringBuilder("select seq_id from ").Append(_events.EventsTableName);

        appendWhere(sql, parameters, query);

        sql.Append(" order by seq_id limit 1");

        return new EventPageCommand(sql.ToString(), parameters);
    }

    private void appendWhere(StringBuilder sql, List<EventPageParameter> parameters, EventPageQuery query)
    {
        sql.Append(" where seq_id > @floor and seq_id <= @ceiling");
        parameters.Add(new EventPageParameter("@floor", query.Floor));
        parameters.Add(new EventPageParameter("@ceiling", query.Ceiling));

        // Archived events are excluded unless a shard asks for them. EventsTable carries an index on
        // (is_archived, seq_id) for exactly this predicate.
        if (!query.IncludeArchivedEvents)
        {
            sql.Append(" and is_archived = 0");
        }

        if (query.TenantId is not null)
        {
            sql.Append(" and tenant_id = @tenant_id");
            parameters.Add(new EventPageParameter("@tenant_id", query.TenantId));
        }

        var aliases = AllowedAliases(query.EventTypes);
        if (aliases.Count > 0)
        {
            sql.Append(" and type in (");
            for (var i = 0; i < aliases.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append("@t").Append(i);
                parameters.Add(new EventPageParameter($"@t{i}", aliases[i]));
            }

            sql.Append(')');
        }
    }

    internal IReadOnlyList<string> AllowedAliases(EventTypeAllowList allowList)
    {
        if (allowList.IsEmpty)
        {
            return [];
        }

        var aliases = new List<string>(allowList.Aliases);
        foreach (var alias in allowList.Aliases)
        {
            if (!_upcastSourcesByTarget.TryGetValue(alias, out var sources)) continue;

            foreach (var source in sources)
            {
                if (!aliases.Contains(source, StringComparer.Ordinal))
                {
                    aliases.Add(source);
                }
            }
        }

        return aliases;
    }
}

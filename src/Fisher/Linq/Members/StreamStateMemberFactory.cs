using System.Linq.Expressions;
using Fisher.Events;
using JasperFx.Events;

namespace Fisher.Linq.Members;

/// <summary>
///     Resolves <see cref="StreamState" /> members to <c>fi_streams</c> columns — the streams-table
///     sibling of <see cref="EventMemberFactory" />, serving
///     <see cref="IReadOnlyEventStore.QueryStreamStates" /> (jasperfx#740).
/// </summary>
/// <remarks>
///     <para>
///         Same division of labor as the event factory: a document member resolves into JSON through
///         <c>json_extract</c>, while a stream-state member is a real column, and everything above this
///         — <see cref="Parsing.WhereClauseParser" />, the fragment set — is shared unchanged.
///     </para>
///     <para>
///         A member with no arm here throws naming it, and that refusal is contract, not convenience:
///         the jasperfx#740 rule is that an untranslatable member must fail the query rather than
///         silently match every row, because unfiltered streams read as filtered. Fisher's own tests
///         pin the refusal (the shared compliance suite deliberately cannot — both reference stores
///         translate the full set).
///     </para>
/// </remarks>
internal class StreamStateMemberFactory : IMemberResolver
{
    private readonly EventGraph _graph;

    public StreamStateMemberFactory(EventGraph graph)
    {
        _graph = graph;
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        var name = expression.Member.Name;

        return name switch
        {
            // Stream identity is one column under two names, picked by the configured identity
            // style — the same rule as IEvent.StreamId/StreamKey on the events table.
            nameof(StreamState.Id) => new EventQueryableMember("id", typeof(Guid), isGuid: true),
            nameof(StreamState.Key) => new EventQueryableMember("id", typeof(string)),

            nameof(StreamState.Version) => new EventQueryableMember("version", typeof(long)),
            nameof(StreamState.CompactedVersion) =>
                new EventQueryableMember("compacted_version", typeof(long)),

            nameof(StreamState.AggregateType) => new AggregateTypeMember(_graph),

            // Both timestamps are SqliteTimestamp's fixed-width UTC text, where lexicographic and
            // temporal order coincide — so range comparison is allowed, exactly as on fi_events.
            nameof(StreamState.Created) =>
                new EventQueryableMember("created", typeof(DateTimeOffset), isTimestamp: true),
            nameof(StreamState.LastTimestamp) =>
                new EventQueryableMember("timestamp", typeof(DateTimeOffset), isTimestamp: true),

            nameof(StreamState.IsArchived) =>
                new EventQueryableMember("is_archived", typeof(bool), isBoolean: true),

            _ => throw new BadLinqExpressionException(
                $"'StreamState.{name}' cannot be translated to a {_graph.StreamsTableName} column. "
                + "Supported members are Id, Key, Version, AggregateType, Created, LastTimestamp, "
                + "IsArchived and CompactedVersion.")
        };
    }

    /// <summary>
    ///     <c>fi_streams.type</c> — the stored aggregate-type identity, compared by translating a
    ///     <c>typeof(X)</c> constant to the same simple-name alias
    ///     <see cref="EventGraph.AggregateAliasFor" /> writes when the stream is started. That shared
    ///     fold is what makes the compaction policy's <c>x.AggregateType == typeof(X)</c> selector
    ///     match exactly the streams the write side tagged.
    /// </summary>
    private sealed class AggregateTypeMember : IQueryableMember
    {
        private readonly EventGraph _graph;

        internal AggregateTypeMember(EventGraph graph) => _graph = graph;

        public Type MemberType => typeof(Type);
        public string TypedLocator => "type";
        public string RawLocator => "type";
        public bool IsBoolean => false;

        /// <summary>
        ///     An alias is a name, and names order alphabetically rather than meaningfully — the same
        ///     reason a string-stored enum refuses ranges. Equality is the whole contract here.
        /// </summary>
        public bool AllowsRangeComparison => false;

        public object? ConvertValue(object? value)
            => value switch
            {
                null => null,
                Type type => _graph.AggregateAliasFor(type),
                _ => throw new BadLinqExpressionException(
                    $"StreamState.AggregateType can only be compared against a Type (typeof(X)), not "
                    + $"'{value.GetType().Name}'.")
            };
    }
}

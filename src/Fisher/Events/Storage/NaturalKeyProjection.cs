using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Events.Storage;

/// <summary>
///     Maintains the <c>fi_natural_key_&lt;alias&gt;</c> lookup — as an inline projection over the
///     append path, and as a replay over the daemon's pages (fisher#206).
/// </summary>
/// <remarks>
///     <para>
///         <b>This replaces <c>NaturalKeyWriter</c>, and the reason is the entry point it did not
///         have.</b> The writer ran from <c>FisherSession.SaveChangesAsync</c> over the unit of work's
///         <c>StreamAction</c>s, which is the only place a stream is <em>appended</em> — so a natural
///         key could never be backfilled onto a stream that already existed, and a rebuild could never
///         repopulate the lookup, because a replay reads persisted events and appends no streams. The
///         old file said so plainly and treated it as a consequence of Fisher's design; it is a
///         consequence of the shape, and Marten's inline-projection shape does not have it.
///     </para>
///     <para>
///         <b>Two entry points, one set of SQL.</b> <see cref="ApplyAsync" /> is the append path and
///         drives off <c>StreamAction</c>s; <see cref="ReplayAsync" /> is the daemon path and drives
///         off raw <see cref="IEvent" />s, taking the stream identity and tenant off the event itself
///         — every row in <c>fi_events</c> carries them. What differs between them is one statement,
///         and it is the one that matters: the append path <em>claims</em> and refuses a key already
///         mapped to a different live stream, where the replay is last-writer-wins. See
///         <see cref="NaturalKeySql" /> for why re-adjudicating on replay would be wrong.
///     </para>
///     <para>
///         <b>It is not registered on the projection graph, and that is deliberate.</b> Marten's
///         equivalent is an <c>IInlineProjection</c> plus a direct rebuild hook rather than an
///         <c>IProjectionSource</c>, so it is not a shard: it has no progression row, no name in
///         <c>projections list</c>, and nothing to rebuild <em>of its own</em>. Registering it as a
///         shard would give an operator a projection they can rebuild independently of the streams it
///         indexes, which is a worse answer than the one the daemon hook gives — every page of every
///         shard maintains it, so any rebuild backfills it as a side effect.
///     </para>
///     <para>
///         <b>Fisher's lookup still has no <c>is_archived</c> column, and that is what makes the
///         replay safe.</b> Polecat and Marten keep the flag on the lookup row, so a replay that
///         rewrites the row has to be careful not to resurrect an archived stream's key; Fisher reads
///         the flag off the join to <c>fi_streams</c>, so the row's presence says nothing about
///         whether the key resolves. A rebuild rewrites every row it sees and an archived stream stays
///         unreachable regardless — which is the <c>an_archived_stream_does_not_come_back_on_rebuild</c>
///         fact, held by the schema rather than by replay logic.
///     </para>
/// </remarks>
internal sealed class NaturalKeyProjection : IInlineProjection<IDocumentSession>
{
    private readonly EventGraph _graph;
    private readonly IReadOnlyList<NaturalKeyDefinition> _definitions;

    internal NaturalKeyProjection(EventGraph graph, IReadOnlyList<NaturalKeyDefinition> definitions)
    {
        _graph = graph;
        _definitions = definitions;
    }

    internal bool HasAny => _definitions.Count > 0;

    /// <summary>
    ///     The append path. Queues a guarded claim per key onto the session's unit of work, so the
    ///     lookup rows commit in the same transaction as the events that carry them.
    /// </summary>
    public Task ApplyAsync(IDocumentSession operations, IEnumerable<StreamAction> streams,
        CancellationToken cancellation)
    {
        if (!HasAny)
        {
            return Task.CompletedTask;
        }

        var session = (Fisher.Internal.FisherSession)operations;

        foreach (var stream in streams)
        {
            foreach (var definition in _definitions)
            {
                foreach (var @event in stream.Events)
                {
                    if (!TryExtract(definition, @event, out var key))
                    {
                        continue;
                    }

                    session.QueueOperation(new NaturalKeyClaimOperation(_graph, definition.AggregateType, key,
                        NaturalKeySql.StreamValue(_graph, stream.Id, stream.Key),
                        NaturalKeySql.Tenant(_graph, stream.TenantId)));
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     The replay path — the counterpart <c>NaturalKeyWriter</c> could not have, and the whole
    ///     reason for the conversion.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Driven from the daemon's page of raw events rather than from stream actions, because a
    ///         replay appends no streams. The stream identity and tenant come off the event, which
    ///         every row in <c>fi_events</c> carries.
    ///     </para>
    ///     <para>
    ///         <b>Every daemon page, not only a rebuild.</b> That is what makes a backfill expressible:
    ///         declaring a natural key on an aggregate whose streams already exist, then rebuilding any
    ///         projection over them (or catching a fresh async shard up from zero), populates the
    ///         lookup for the history — where before, only a stream appended after the declaration
    ///         could ever be reachable by its key.
    ///     </para>
    /// </remarks>
    internal void Replay(IDocumentSession operations, IReadOnlyList<IEvent> events)
    {
        if (!HasAny || events.Count == 0)
        {
            return;
        }

        var session = (Fisher.Internal.FisherSession)operations;

        foreach (var @event in events)
        {
            foreach (var definition in _definitions)
            {
                if (!TryExtract(definition, @event, out var key))
                {
                    continue;
                }

                session.QueueOperation(new NaturalKeyReplayOperation(_graph, definition.AggregateType, key,
                    NaturalKeySql.StreamValue(_graph, @event.StreamId, @event.StreamKey),
                    NaturalKeySql.Tenant(_graph, @event.TenantId)));
            }
        }
    }

    /// <summary>
    ///     Whether this event carries a value for this definition's key, and what it unwraps to.
    /// </summary>
    /// <remarks>
    ///     The extractor takes the whole <see cref="IEvent" /> rather than its body, so a key can be
    ///     derived from metadata as well as from the event's own members — jasperfx#569.
    /// </remarks>
    private static bool TryExtract(NaturalKeyDefinition definition, IEvent @event, out object key)
    {
        key = null!;

        var mapping = definition.EventMappings
            .FirstOrDefault(x => x.EventType.IsAssignableFrom(@event.Data.GetType()));

        if (mapping?.Extractor(@event) is not { } value)
        {
            return false;
        }

        if (definition.Unwrap(value) is not { } unwrapped)
        {
            return false;
        }

        key = unwrapped;
        return true;
    }
}

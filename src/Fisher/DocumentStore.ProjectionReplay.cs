using System.Text;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Fisher;

/// <summary>
///     Stateless projection step-through for a monitoring console (fisher#44): hand it a projection
///     name and a list of events, get the per-step state back without touching the database.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here writes. A query session is opened because an aggregation's <c>Apply</c> may
///         read reference data, and the multi-stream path's enrichment definitely does — which carries
///         the caveat JasperFx already states: <b>enrichment reads present-day data even when
///         replaying historical events</b>, so an enriched value reflects the reference data as it is
///         now, not as it was.
///     </para>
///     <para>
///         The fold itself is JasperFx's, reached through <c>EventGraph.AggregatorFor&lt;T&gt;</c> and
///         <c>ISteppableAggregation</c> — the same seam every live aggregation goes through, which is
///         what makes a replay agree with what the daemon would produce for the same events.
///     </para>
///     <para>
///         ⚠️ <b>Every state crosses the wire through the store's own serializer</b> (fisher#412). The
///         by-name path used to render state with <c>JsonSerializer</c>'s <em>default</em> options, so a
///         console saw <c>Loaded</c> where the store persists <c>loaded</c> — and the reflection those
///         defaults reach for is disabled in a Native AOT image, so the call threw there. The store's
///         serializer is what a document write uses, so it carries the application's naming policy, its
///         converters and, in a native image, its source-generated context. Its <see cref="Stream" />
///         overloads are the ones read here, being the unannotated shared contract every document read
///         already goes through.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    /// <summary>
    ///     The shared step-through (jasperfx#1049), built over Fisher's event type lookup and Fisher's own
    ///     serializer. The serializer is what keeps a step's state exactly what the store would persist —
    ///     camelCase where the store writes camelCase (fisher#412) — and what the shared copy-per-step and
    ///     round-trip checks run through.
    /// </summary>
    private ProjectionStepThrough<IQuerySession> StepThrough()
        => Options.Projections.StepThrough(ToDomainEvent, new ProjectionStateSerializer(
            state => ToElement(state)!.Value,
            (json, type) => Options.Serializer.FromJson(type, Utf8(json.GetRawText()))));

    async Task<ProjectionTimeline<TState>> IEventStore.RunProjectionAsync<TState>(
        string projectionName, object identity, IReadOnlyList<EventRecord> events,
        TState? startingState, CancellationToken ct) where TState : default
    {
        await using var session = QuerySession();

        return await StepThrough()
            .RunProjectionAsync(projectionName, identity, events, startingState, session, ct)
            .ConfigureAwait(false);
    }

    async Task<ProjectionTimelineRaw> IEventStore.RunProjectionByNameAsync(
        string projectionName, object identity, IReadOnlyList<EventRecord> events,
        JsonElement? startingState, CancellationToken ct)
    {
        await using var session = QuerySession();

        return await StepThrough()
            .RunProjectionByNameAsync(projectionName, identity, events, startingState, session, ct)
            .ConfigureAwait(false);
    }

    async Task<MultiAggregateProjectionResult> IEventStore.RunMultiStreamProjectionAsync(
        string projectionName, IReadOnlyList<EventRecord> events, CancellationToken ct)
    {
        await using var session = QuerySession();

        return await StepThrough()
            .RunMultiStreamProjectionAsync(projectionName, events, session, ct)
            .ConfigureAwait(false);
    }

    private JsonElement? ToElement(object? state)
    {
        if (state is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(Options.Serializer.ToJson(state));
        return document.RootElement.Clone();
    }

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json), writable: false);

    private IEvent? ToDomainEvent(EventRecord record)
    {
        var clrType = EventGraph.AllKnownEventTypes()
            .FirstOrDefault(x => x.EventTypeName == record.EventTypeName)?.EventType;

        if (clrType is null || Options.Serializer.FromJson(clrType, Utf8(record.Data.GetRawText())) is not { } body)
        {
            return null;
        }

        var wrapped = EventGraph.EventMappingFor(clrType).Wrap(body);

        wrapped.Id = record.EventId;
        wrapped.Sequence = record.Sequence;
        wrapped.Version = record.StreamVersion;
        wrapped.Timestamp = record.Timestamp;
        wrapped.TenantId = record.TenantId ?? JasperFx.StorageConstants.DefaultTenantId;
        wrapped.EventTypeName = record.EventTypeName;

        if (EventGraph.StreamIdentity == StreamIdentity.AsGuid && Guid.TryParse(record.StreamId, out var streamId))
        {
            wrapped.StreamId = streamId;
        }
        else
        {
            wrapped.StreamKey = record.StreamId;
        }

        return wrapped;
    }
}

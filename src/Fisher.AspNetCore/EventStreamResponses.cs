using JasperFx.Events;

namespace Fisher.AspNetCore;

/// <summary>
///     A stream's metadata, as an HTTP response body.
/// </summary>
/// <remarks>
///     A response shape rather than <see cref="StreamState" /> itself, because the JasperFx type
///     carries a <see cref="Type" /> and serializing one over the wire leaks an assembly-qualified
///     name into an API. Field for field with Polecat's, so a client written against one store reads
///     the other.
/// </remarks>
public sealed record StreamStateResponse
{
    /// <summary>The stream's Guid identity, or empty under string identity.</summary>
    public Guid Id { get; init; }

    /// <summary>The stream's string identity, or null under Guid identity.</summary>
    public string? Key { get; init; }

    /// <summary>The version of the last event appended.</summary>
    public long Version { get; init; }

    /// <summary>The aggregate type the stream was started as, by name, or null.</summary>
    public string? AggregateTypeName { get; init; }

    /// <summary>When the last event was appended.</summary>
    public DateTimeOffset LastTimestamp { get; init; }

    /// <summary>When the stream was started.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Whether the stream has been archived.</summary>
    public bool IsArchived { get; init; }

    /// <summary>Project a <see cref="StreamState" /> into its response shape.</summary>
    public static StreamStateResponse From(StreamState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new StreamStateResponse
        {
            Id = state.Id,
            Key = state.Key,
            Version = state.Version,
            AggregateTypeName = state.AggregateType?.Name,
            LastTimestamp = state.LastTimestamp,
            Created = state.Created,
            IsArchived = state.IsArchived
        };
    }
}

/// <summary>
///     One event, as an HTTP response body.
/// </summary>
/// <remarks>
///     <b>Deliberately not <see cref="IEvent" />.</b> That interface carries the store's own envelope
///     and would serialize a shape an API consumer has no business depending on. This is field for
///     field with Polecat's <c>EventResponse</c>.
/// </remarks>
public sealed record EventResponse
{
    /// <summary>The event's own identity.</summary>
    public Guid Id { get; init; }

    /// <summary>Its version within the stream.</summary>
    public long Version { get; init; }

    /// <summary>Its position in the store's global order.</summary>
    public long Sequence { get; init; }

    /// <summary>The stream's Guid identity, or empty under string identity.</summary>
    public Guid StreamId { get; init; }

    /// <summary>The stream's string identity, or null under Guid identity.</summary>
    public string? StreamKey { get; init; }

    /// <summary>The event type's alias — short and stable, not the assembly-qualified name.</summary>
    public string? EventTypeName { get; init; }

    /// <summary>When it was appended.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The tenant it belongs to.</summary>
    public string? TenantId { get; init; }

    /// <summary>Whether its stream has been archived.</summary>
    public bool IsArchived { get; init; }

    /// <inheritdoc cref="IEvent.CausationId" />
    public string? CausationId { get; init; }

    /// <inheritdoc cref="IEvent.CorrelationId" />
    public string? CorrelationId { get; init; }

    /// <inheritdoc cref="IEvent.Headers" />
    public Dictionary<string, object>? Headers { get; init; }

    /// <summary>The event body.</summary>
    public object Data { get; init; } = default!;

    /// <summary>Project an <see cref="IEvent" /> into its response shape.</summary>
    public static EventResponse From(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return new EventResponse
        {
            Id = @event.Id,
            Version = @event.Version,
            Sequence = @event.Sequence,
            StreamId = @event.StreamId,
            StreamKey = @event.StreamKey,
            EventTypeName = @event.EventTypeName,
            Timestamp = @event.Timestamp,
            TenantId = @event.TenantId,
            IsArchived = @event.IsArchived,
            CausationId = @event.CausationId,
            CorrelationId = @event.CorrelationId,
            Headers = @event.Headers,
            Data = @event.Data
        };
    }

    /// <inheritdoc cref="From(IEvent)" />
    public static EventResponse[] From(IReadOnlyList<IEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events.Select(From).ToArray();
    }
}

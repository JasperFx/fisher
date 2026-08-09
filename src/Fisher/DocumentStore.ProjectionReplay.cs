using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

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
///         what makes a replay agree with what the daemon would produce for the same events. Ported
///         from Polecat's, with its AOT annotations, because Fisher has no AOT story of its own to
///         protect here.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    [UnconditionalSuppressMessage("Trimming", "IL2091:DynamicallyAccessedMembers",
        Justification = "Forwards to ReplayReferenceTypeAsync<TState> via MakeGenericMethod. Projection step-through is a diagnostic IEventStore surface; AOT consumers avoid it or supply a source-generated dispatcher.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "MakeGenericMethod is what satisfies the aggregator's `class, new()` constraint without changing the public IEventStore.RunProjectionAsync<TState> contract.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
        Justification = "Reads Task<T>.Result reflectively; Task<T> is a framework type whose Result is intrinsically preserved.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Calls ReplayReferenceTypeAsync<TState>, which is annotated. The explicit implementation cannot propagate the requirement because the interface does not declare it.")]
    async Task<ProjectionTimeline<TState>> IEventStore.RunProjectionAsync<TState>(
        string projectionName, object identity, IReadOnlyList<EventRecord> events,
        TState? startingState, CancellationToken ct) where TState : default
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        AssertProjectionExists(projectionName);

        // The interface leaves TState unconstrained; the aggregator graph requires a reference type
        // with a parameterless constructor. Named rather than surfaced as a constraint failure from
        // inside MakeGenericMethod, which would name a type parameter and not the projection.
        if (typeof(TState).IsValueType)
        {
            throw new NotSupportedException(
                $"RunProjectionAsync needs a reference-typed aggregate state, and '{typeof(TState).Name}' "
                + "is a value type. Fisher's aggregation is JasperFx's, which builds an aggregate by "
                + "constructing and mutating it.");
        }

        var generic = typeof(DocumentStore)
            .GetMethod(nameof(ReplayReferenceTypeAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(TState));

        var task = (Task)generic.Invoke(this, [events, startingState, ct])!;
        await task.ConfigureAwait(false);

        return (ProjectionTimeline<TState>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    [RequiresUnreferencedCode("Routes events through the aggregator graph and reflective deserialization; TState's members must survive trimming.")]
    [RequiresDynamicCode("ISerializer.FromJson uses System.Text.Json's runtime code generation.")]
    private async Task<ProjectionTimeline<TState>> ReplayReferenceTypeAsync<TState>(
        IReadOnlyList<EventRecord> events, TState? startingState, CancellationToken ct)
        where TState : class, new()
    {
        var aggregator = Options.Projections.AggregatorFor<TState>();

        await using var session = QuerySession();

        var current = startingState;
        var steps = new List<ProjectionStepResult<TState>>(events.Count);

        foreach (var record in events)
        {
            var domainEvent = ToDomainEvent(record);

            // A copy, not a reference. JasperFx's aggregation mutates the aggregate in place, so every
            // step of a timeline built from live references ends up showing the *final* state — the
            // one thing a step-through exists not to do. Round-tripping through the store's own
            // serializer is also what makes the captured state exactly what would have been persisted.
            var before = Copy(current);
            var watch = Stopwatch.StartNew();

            var after = current;
            Exception? error = null;

            try
            {
                if (domainEvent is not null)
                {
                    // One event at a time, which is the whole point of a step-through: the console
                    // shows what each event did rather than what the batch did.
                    after = await aggregator.BuildAsync([domainEvent], session, current, ct)
                        .ConfigureAwait(false) ?? current;
                }
            }
            catch (Exception e)
            {
                // Recorded on the step rather than thrown. A step-through exists to show where a
                // projection breaks, and throwing would hide every step after the first bad one.
                error = e;
                after = current;
            }

            watch.Stop();

            steps.Add(new ProjectionStepResult<TState>(record, before!, Copy(after)!, watch.Elapsed, error!));
            current = after;
        }

        return new ProjectionTimeline<TState>(steps, current!);
    }

    /// <summary>
    ///     A detached copy of an aggregate, through the store's own serializer.
    /// </summary>
    [RequiresUnreferencedCode("Round-trips TState through ISerializer.")]
    [RequiresDynamicCode("ISerializer uses System.Text.Json's runtime code generation.")]
    private TState? Copy<TState>(TState? state) where TState : class
        => state is null ? null : Options.Serializer.FromJson<TState>(Options.Serializer.ToJson(state));

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "MakeGenericMethod over the projection's published state type, to dispatch into the strong-typed replay.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "System.Text.Json over the projection's published state type. Diagnostic surface only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
        Justification = "Reads closed-generic Task / ProjectionTimeline properties reflectively; both are preserved by this class' own use of them.")]
    async Task<ProjectionTimelineRaw> IEventStore.RunProjectionByNameAsync(
        string projectionName, object identity, IReadOnlyList<EventRecord> events,
        JsonElement? startingState, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        var source = AssertProjectionExists(projectionName);

        var stateType = source.PublishedTypes().FirstOrDefault()
                        ?? throw new NotSupportedException(
                            $"Projection '{projectionName}' publishes no strong-typed state, so there is "
                            + "nothing to show per step. A flat-table projection or a subscription is the "
                            + "usual case — neither produces a document.");

        var generic = typeof(IEventStore).GetMethod(nameof(IEventStore.RunProjectionAsync))!
            .MakeGenericMethod(stateType);

        var typedStart = startingState.HasValue
            ? JsonSerializer.Deserialize(startingState.Value.GetRawText(), stateType)
            : null;

        var task = (Task)generic.Invoke(this, [projectionName, identity, events, typedStart, ct])!;
        await task.ConfigureAwait(false);

        var timeline = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var typedSteps = (System.Collections.IEnumerable)timeline.GetType().GetProperty("Steps")!.GetValue(timeline)!;
        var final = timeline.GetType().GetProperty("FinalState")!.GetValue(timeline);

        var raw = new List<ProjectionStepResultRaw>(events.Count);

        foreach (var step in typedSteps)
        {
            var type = step.GetType();
            var record = (EventRecord)type.GetProperty("Event")!.GetValue(step)!;
            var before = type.GetProperty("Before")!.GetValue(step);
            var after = type.GetProperty("After")!.GetValue(step);
            var elapsed = (TimeSpan)type.GetProperty("Elapsed")!.GetValue(step)!;
            var error = (Exception?)type.GetProperty("Error")!.GetValue(step);

            raw.Add(new ProjectionStepResultRaw(record,
                before is null ? null : JsonSerializer.SerializeToElement(before, stateType),
                after is null ? null : JsonSerializer.SerializeToElement(after, stateType),
                elapsed, error?.Message!));
        }

        return new ProjectionTimelineRaw(raw,
            final is null ? null : JsonSerializer.SerializeToElement(final, stateType));
    }

    /// <remarks>
    ///     The multi-stream form, which drives the projection's real slice → group → enrich → fold path
    ///     rather than folding one aggregate — so a multi-stream projection produces one timeline per
    ///     identity the events touch, and a single-stream one produces exactly one. The fold lives in
    ///     JasperFx on <c>JasperFxAggregationProjectionBase</c>, so this is a thin adapter.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Calls ToDomainEvent (annotated) and System.Text.Json over the store serializer's output. The explicit implementation cannot propagate the requirement.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "ToDomainEvent and JsonSerializer both use runtime code generation. Diagnostic surface only.")]
    async Task<MultiAggregateProjectionResult> IEventStore.RunMultiStreamProjectionAsync(
        string projectionName, IReadOnlyList<EventRecord> events, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        var source = AssertProjectionExists(projectionName);

        if (source is not ISteppableAggregation<IQuerySession> steppable)
        {
            throw new NotSupportedException(
                $"Projection '{projectionName}' is not an aggregation projection, so it has no aggregate "
                + "to step through. Flat-table projections, event projections and subscriptions write "
                + "directly and have no intermediate state to show.");
        }

        // Keyed by reference back to the record each domain event came from, because the fold hands
        // back the same IEvent instances and the console needs to know which wire event a step was.
        var recordFor = new Dictionary<IEvent, EventRecord>(ReferenceEqualityComparer.Instance);
        var domainEvents = new List<IEvent>(events.Count);

        foreach (var record in events)
        {
            if (ToDomainEvent(record) is not { } domainEvent)
            {
                continue;
            }

            recordFor[domainEvent] = record;
            domainEvents.Add(domainEvent);
        }

        await using var session = QuerySession();

        return await steppable.BuildTimelinesAsync(domainEvents, session,
            state => state is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(Options.Serializer.ToJson(state)),
            e => recordFor[e], observer: null, ct).ConfigureAwait(false);
    }

    private JasperFx.Events.Projections.IProjectionSource<IDocumentSession, IQuerySession> AssertProjectionExists(
        string projectionName)
        => Options.Projections.TryFindProjection(projectionName, out var source)
            ? source!
            : throw new ArgumentException(
                $"Unknown projection '{projectionName}'. Register it on StoreOptions.Projections before "
                + "replaying — a replay folds through the registered projection, not through a copy.",
                nameof(projectionName));

    /// <summary>
    ///     A wire <see cref="EventRecord" /> as an <see cref="IEvent" /> the aggregator can apply, or
    ///     null when this process does not know the event type.
    /// </summary>
    /// <remarks>
    ///     Skipping an unknown type rather than throwing is the stream reads' policy, not the daemon's,
    ///     and that is the right one here: a console replaying events it fetched from a store may well
    ///     be pointed at a deployment that knows fewer types than the store holds, and one unknown
    ///     event should not blank the whole timeline.
    /// </remarks>
    [RequiresUnreferencedCode("Resolves an event type name to a CLR type and deserializes through ISerializer.")]
    [RequiresDynamicCode("ISerializer.FromJson uses System.Text.Json's runtime code generation.")]
    private IEvent? ToDomainEvent(EventRecord record)
    {
        var clrType = EventGraph.AllKnownEventTypes()
            .FirstOrDefault(x => x.EventTypeName == record.EventTypeName)?.EventType;

        if (clrType is null || Options.Serializer.FromJson(clrType, record.Data.GetRawText()) is not { } body)
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

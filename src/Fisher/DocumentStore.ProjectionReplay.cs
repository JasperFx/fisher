using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
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
    async Task<ProjectionTimeline<TState>> IEventStore.RunProjectionAsync<TState>(
        string projectionName, object identity, IReadOnlyList<EventRecord> events,
        TState? startingState, CancellationToken ct) where TState : default
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        AssertProjectionExists(projectionName);

        // The constrained method returns exactly this type, so the result is cast rather than read off
        // Task<T>.Result by reflection.
        return await ((Task<ProjectionTimeline<TState>>)InvokeReplay(nameof(ReplayReferenceTypeAsync),
            typeof(TState), [events, startingState, ct])).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reach a replay method constrained to a reference-typed state, from a caller that has the state
    ///     only as an unconstrained generic argument or as a <see cref="Type" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>Deliberately non-generic</b> (fisher#412). The obvious spelling,
    ///         <c>MakeGenericMethod(typeof(TState))</c> inside the generic interface method, crashes ILC
    ///         10.0.1 outright once the method is reachable — an <c>IndexOutOfRangeException</c> from
    ///         <c>MakeGenericMethodSite.InstantiateDependencies</c> while it tries to instantiate the call
    ///         site over the caller's own type parameter — so a native publish of any application calling
    ///         either step-through method failed before producing a binary. Handed a plain
    ///         <see cref="Type" />, the analyzer warns rather than instantiating, which the suppressions
    ///         below answer.
    ///     </para>
    /// </remarks>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "IEventStore leaves the state unconstrained (or names it only by Type) while the aggregator graph needs a reference type, so the constrained replay is reached with one MakeGenericMethod. A value type is refused first, and Native AOT serves a reference-typed instantiation from the shared canonical form — smoke/aot-consumer runs both step-through methods natively.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification = "The only requirement of either target is the `class` constraint, checked first.")]
    private Task InvokeReplay(string method, Type stateType, object?[] arguments)
    {
        AssertReferenceTypedState(stateType);

        var replay = typeof(DocumentStore)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(stateType);

        return (Task)replay.Invoke(this, arguments)!;
    }

    /// <summary>
    ///     The interface leaves <c>TState</c> unconstrained; the aggregator graph requires a reference
    ///     type. Named rather than surfaced as a constraint failure from inside <c>MakeGenericMethod</c>,
    ///     which would name a type parameter and not the projection.
    /// </summary>
    private static void AssertReferenceTypedState(Type stateType)
    {
        if (stateType.IsValueType)
        {
            throw new NotSupportedException(
                $"RunProjectionAsync needs a reference-typed aggregate state, and '{stateType.Name}' "
                + "is a value type. Fisher's aggregation is JasperFx's, which builds an aggregate by "
                + "constructing and mutating it.");
        }
    }

    private async Task<ProjectionTimeline<TState>> ReplayReferenceTypeAsync<TState>(
        IReadOnlyList<EventRecord> events, TState? startingState, CancellationToken ct)
        where TState : class
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
    private TState? Copy<TState>(TState? state) where TState : class
        => state is null ? null : Options.Serializer.FromJson<TState>(Utf8(Options.Serializer.ToJson(state)));

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

        // One hop into a method generic over the state, which then reaches the typed replay with
        // ordinary generic calls and hands back the untyped timeline — so nothing reads a step's
        // properties by reflection on the way out.
        return await ((Task<ProjectionTimelineRaw>)InvokeReplay(nameof(ReplayAsJsonAsync), stateType,
            [events, startingState, ct])).ConfigureAwait(false);
    }

    private async Task<ProjectionTimelineRaw> ReplayAsJsonAsync<TState>(IReadOnlyList<EventRecord> events,
        JsonElement? startingState, CancellationToken ct) where TState : class
    {
        var typedStart = startingState.HasValue
            ? Options.Serializer.FromJson<TState>(Utf8(startingState.Value.GetRawText()))
            : null;

        var timeline = await ReplayReferenceTypeAsync(events, typedStart, ct).ConfigureAwait(false);

        var raw = timeline.Steps
            .Select(step => new ProjectionStepResultRaw(step.Event, ToElement(step.Before), ToElement(step.After),
                step.Elapsed, step.Error?.Message!))
            .ToList();

        return new ProjectionTimelineRaw(raw, ToElement(timeline.FinalState));
    }

    /// <summary>
    ///     A state as the JSON the store would persist for it.
    /// </summary>
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

    /// <remarks>
    ///     The multi-stream form, which drives the projection's real slice → group → enrich → fold path
    ///     rather than folding one aggregate — so a multi-stream projection produces one timeline per
    ///     identity the events touch, and a single-stream one produces exactly one. The fold lives in
    ///     JasperFx on <c>JasperFxAggregationProjectionBase</c>, so this is a thin adapter.
    /// </remarks>
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

        return await steppable.BuildTimelinesAsync(domainEvents, session, ToElement,
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

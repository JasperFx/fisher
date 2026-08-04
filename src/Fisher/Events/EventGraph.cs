using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Fisher.Events.Schema;
using Fisher.Serialization;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Events.Tags;

namespace Fisher.Events;

/// <summary>
///     Central configuration and registry for the Fisher event store — the analogue of Marten's and
///     Polecat's <c>EventGraph</c>. Owns event type registration, aggregate alias resolution, the
///     physical table names, and the cached closed-shape event storage.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: extends JasperFx.Events.EventRegistry (annotated RUC) for type-aliased event construction. Event types are preserved by registration on the caller side per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2057:UnrecognizedTypeName",
    Justification =
        "Class-level: ResolveEventType uses Type.GetType(string) to resolve the dotnet_type name persisted on each event row. Event types are preserved by EventGraph registration on the caller side.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: event-type registration uses Type.MakeGenericType. AOT consumers register concrete event types ahead of time.")]
public class EventGraph : EventRegistry, IAggregationSourceFactory<IQuerySession>
{
    private readonly ConcurrentDictionary<string, Type> _aggregateTypes = new();
    private readonly ConcurrentDictionary<Type, FisherEventType> _eventTypes = new();
    private readonly StoreOptions _options;
    private readonly List<ITagTypeRegistration> _tagTypes = new();

    private object? _closedShapeEventStorage;

    internal EventGraph(StoreOptions options)
    {
        _options = options;

        // Fisher is QuickAppend-only, like Polecat. SQLite has no stored procedures and no server-side
        // sequence objects, so the Rich per-row path buys nothing it does not already have.
        AppendMode = EventAppendMode.Quick;
    }

    /// <summary>
    ///     Controls whether streams are identified by Guid or string.
    /// </summary>
    public override StreamIdentity StreamIdentity
    {
        get => _options.Events.StreamIdentity;
        set => _options.Events.StreamIdentity = value;
    }

    /// <summary>
    ///     Controls the tenancy style for event store tables.
    /// </summary>
    public TenancyStyle TenancyStyle
    {
        get => _options.Events.TenancyStyle;
        set => _options.Events.TenancyStyle = value;
    }

    /// <summary>
    ///     The logical schema name for event store tables, falling back to
    ///     <see cref="StoreOptions.DatabaseSchemaName" />. Folded into the table prefix rather than
    ///     qualifying anything — see <see cref="FisherTableNaming" />.
    /// </summary>
    public string DatabaseSchemaName => _options.Events.DatabaseSchemaName ?? _options.DatabaseSchemaName;

    internal ISerializer Serializer => _options.Serializer;

    internal EventStoreOptions EventOptions => _options.Events;

    internal StoreOptions Options => _options;

    /// <summary>
    ///     Whether extended progression tracking columns are enabled.
    /// </summary>
    public bool EnableExtendedProgressionTracking => _options.Events.EnableExtendedProgressionTracking;

    /// <summary>
    ///     Process projection side effects when running projections under the Inline lifecycle.
    /// </summary>
    public bool EnableSideEffectsOnInlineProjections => _options.Events.EnableSideEffectsOnInlineProjections;

    /// <summary>
    ///     Use a session-level identity map for aggregates fetched via <c>FetchForWriting()</c> or
    ///     <c>FetchLatest()</c>. Only appropriate for immutable aggregations.
    /// </summary>
    public bool UseIdentityMapForAggregates { get; set; }

    internal string StreamsTableName => FisherTableNaming.QuotedTableName(DatabaseSchemaName, "streams");
    internal string EventsTableName => FisherTableNaming.QuotedTableName(DatabaseSchemaName, "events");

    internal string ProgressionTableName =>
        FisherTableNaming.QuotedTableName(DatabaseSchemaName, "event_progression");

    /// <summary>
    ///     The shared closed-shape <c>Weasel.Storage.EventStorage&lt;TId&gt;</c> for this event graph,
    ///     built once from <see cref="Storage.SqliteEventStoreDialect" /> and cached. Boxed as
    ///     <see cref="object" /> because <c>TId</c> is fixed by <see cref="StreamIdentity" />; callers
    ///     downcast to the concrete closure.
    /// </summary>
    internal object ClosedShapeEventStorage => _closedShapeEventStorage ??= BuildClosedShapeEventStorage();

    private Weasel.Storage.EventStorage<Guid> GuidEventStorage
        => (Weasel.Storage.EventStorage<Guid>)ClosedShapeEventStorage;

    private Weasel.Storage.EventStorage<string> StringEventStorage
        => (Weasel.Storage.EventStorage<string>)ClosedShapeEventStorage;

    /// <summary>
    ///     Every tag type registered for Dynamic Consistency Boundary support.
    /// </summary>
    public IReadOnlyList<ITagTypeRegistration> TagTypes => _tagTypes;

    private object BuildClosedShapeEventStorage()
    {
        var dialect = new Storage.SqliteEventStoreDialect();
        var serializer = StorageSerializerAdapter.For(Serializer);

        return StreamIdentity == StreamIdentity.AsGuid
            ? Weasel.Storage.EventStorageBuilder.Build<Guid>(dialect, AppendMode, this, serializer)
            : Weasel.Storage.EventStorageBuilder.Build<string>(dialect, AppendMode, this, serializer);
    }

    internal Weasel.Storage.IStorageOperation ArchiveStreamOperation(object streamId, string tenantId, bool archived)
        => StreamIdentity == StreamIdentity.AsGuid
            ? GuidEventStorage.ArchiveStream(streamId, tenantId, archived)
            : StringEventStorage.ArchiveStream(streamId, tenantId, archived);

    internal Weasel.Storage.IStorageOperation TombstoneStreamOperation(object streamId, string tenantId)
        => StreamIdentity == StreamIdentity.AsGuid
            ? GuidEventStorage.TombstoneStream(streamId, tenantId)
            : StringEventStorage.TombstoneStream(streamId, tenantId);

    internal Weasel.Storage.IStorageOperation UpdateProgressOperation(string shardIdentity, long sequence, bool upsert)
        => StreamIdentity == StreamIdentity.AsGuid
            ? GuidEventStorage.UpdateProgress(shardIdentity, sequence, upsert)
            : StringEventStorage.UpdateProgress(shardIdentity, sequence, upsert);

    /// <summary>
    ///     Build an aggregator source on the fly for an aggregate type that was never registered as a
    ///     projection — the auto-discovery half of live aggregation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Projections.SingleStreamProjection{TDoc,TId}" /> is closed over the aggregate's
    ///         own identity type rather than the stream identity primitive. For a plain
    ///         <c>Guid Id</c> the two are the same, but a strong-typed id is a wrapper struct, and the
    ///         evolver JasperFx's source generator emits is keyed on that wrapper. Closing over
    ///         <c>Guid</c> there would leave the generated dispatcher unmatched and trip JasperFx's
    ///         fail-fast.
    ///     </para>
    ///     <para>
    ///         Every event type the projection handles is registered as a side effect, so a process that
    ///         only ever reads a stream still knows how to resolve its event type names.
    ///     </para>
    /// </remarks>
    IAggregatorSource<IQuerySession>? IAggregationSourceFactory<IQuerySession>.Build<TDoc>()
    {
        var idType = AggregateIdentity.ResolveIdType(typeof(TDoc), StreamIdentity);
        var projectionType = typeof(Projections.SingleStreamProjection<,>)
            .MakeGenericType(typeof(TDoc), idType);

#pragma warning disable CS8714 // TDoc is unconstrained here but SingleStreamProjection requires notnull
        var projection = (ProjectionBase)Activator.CreateInstance(projectionType)!;
#pragma warning restore CS8714

        projection.Lifecycle = ProjectionLifecycle.Live;
        projection.AssembleAndAssertValidity();

        foreach (var eventType in projection.IncludedEventTypes)
        {
            AddEventType(eventType);
        }

        return projection as IAggregatorSource<IQuerySession>;
    }

    /// <summary>
    ///     The cached live aggregator for an aggregate type.
    /// </summary>
    /// <remarks>
    ///     The single seam every live aggregation goes through. It defers to the projection graph,
    ///     which checks registered projections first and only then falls back to
    ///     <see cref="IAggregationSourceFactory{TQuerySession}" /> — that is, to the method above. Fisher
    ///     has no way to register a projection yet, so today auto-discovery is still the whole story;
    ///     routing through the graph is what makes a registered projection win once there is one.
    /// </remarks>
    internal IAggregator<T, IQuerySession> AggregatorFor<T>() where T : class
        => _options.Projections.AggregatorFor<T>();

    /// <summary>
    ///     Wrap raw event data into an <see cref="IEvent" /> carrying type metadata.
    /// </summary>
    public override IEvent BuildEvent(object eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData is IEvent e)
        {
            var mapping = EventMappingFor(e.EventType);
            e.EventTypeName = mapping.EventTypeName;
            e.DotNetTypeName = mapping.DotNetTypeName;
            return e;
        }

        return EventMappingFor(eventData.GetType()).Wrap(eventData);
    }

    public override FisherEventType EventMappingFor(Type eventType)
        => _eventTypes.GetOrAdd(eventType, static type => new FisherEventType(type));

    public override void AddEventType(Type eventType) => EventMappingFor(eventType);

    /// <summary>
    ///     Every event type this graph has been told about.
    /// </summary>
    public IReadOnlyList<FisherEventType> AllKnownEventTypes() => _eventTypes.Values.ToList();

    public override Type AggregateTypeFor(string aggregateTypeName)
    {
        if (_aggregateTypes.TryGetValue(aggregateTypeName, out var type))
        {
            return type;
        }

        throw new ArgumentOutOfRangeException(nameof(aggregateTypeName),
            $"Unknown aggregate type name '{aggregateTypeName}'.");
    }

    /// <summary>
    ///     Resolve the alias persisted in <c>fi_streams.type</c> back to its aggregate type, or null when
    ///     this deployment has no registration for it.
    /// </summary>
    /// <remarks>
    ///     Unlike <see cref="AggregateTypeFor" /> this never throws: a stream tagged by a deployment that
    ///     knew a type this one does not must still be able to report its version and timestamps. Note
    ///     the alias is the SIMPLE name, because that is what the column stores — two aggregate types
    ///     with the same <c>Name</c> in different namespaces share an alias and the first seen wins.
    /// </remarks>
    internal Type? TryResolveAggregateType(string? aggregateTypeName)
    {
        if (string.IsNullOrEmpty(aggregateTypeName))
        {
            return null;
        }

        return _aggregateTypes.TryGetValue(aggregateTypeName, out var known) ? known : null;
    }

    public override string AggregateAliasFor(Type aggregateType)
    {
        _aggregateTypes.TryAdd(aggregateType.Name, aggregateType);
        return aggregateType.Name;
    }

    /// <summary>
    ///     Resolve a .NET type from the <c>dotnet_type</c> name stored on an event row.
    /// </summary>
    internal Type? ResolveEventType(string? dotNetTypeName)
        => string.IsNullOrEmpty(dotNetTypeName) ? null : Type.GetType(dotNetTypeName);

    internal StreamsTable BuildStreamsTable() => new(this);

    internal EventsTable BuildEventsTable() => new(this);

    internal EventProgressionTable BuildEventProgressionTable() => new(this);

    /// <summary>
    ///     Register a tag type for Dynamic Consistency Boundary support, deriving the table suffix from
    ///     the type name.
    /// </summary>
    public ITagTypeRegistration RegisterTagType<TTag>() where TTag : notnull
        => RegisterTagType<TTag>(ToEventTypeName(typeof(TTag).Name));

    /// <summary>
    ///     Register a tag type for Dynamic Consistency Boundary support with an explicit table suffix.
    /// </summary>
    public ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull
    {
        var existing = _tagTypes.FirstOrDefault(t => t.TagType == typeof(TTag));
        if (existing != null)
        {
            return existing;
        }

        var registration = TagTypeRegistration.Create<TTag>(tableSuffix);
        _tagTypes.Add(registration);
        return registration;
    }

    /// <summary>
    ///     The registration for a tag type, or null when it was never registered.
    /// </summary>
    public ITagTypeRegistration? FindTagType(Type tagType)
        => _tagTypes.FirstOrDefault(t => t.TagType == tagType);

    /// <summary>
    ///     Convert a PascalCase type name to a snake_case alias — <c>QuestStarted</c> becomes
    ///     <c>quest_started</c>.
    /// </summary>
    internal static string ToEventTypeName(string typeName)
    {
        var result = new StringBuilder();

        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}

/// <summary>
///     Metadata and wrapping logic for a single event type.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: Wrap uses Type.MakeGenericType(typeof(Event<>), eventType) to construct Event<T> envelopes. Event types are preserved by registration on the caller side.")]
public class FisherEventType : IEventType
{
    public FisherEventType(Type eventType)
    {
        EventType = eventType;
        EventTypeName = EventGraph.ToEventTypeName(eventType.Name);
        DotNetTypeName = $"{eventType.FullName}, {eventType.Assembly.GetName().Name}";
    }

    public Type EventType { get; }
    public string EventTypeName { get; set; }
    public string DotNetTypeName { get; set; }
    public string Alias => EventTypeName;

    /// <summary>
    ///     Wrap raw event data into an <c>Event&lt;T&gt;</c> envelope carrying type metadata.
    /// </summary>
    public IEvent Wrap(object eventData)
    {
        var genericType = typeof(Event<>).MakeGenericType(EventType);
        var @event = (IEvent)Activator.CreateInstance(genericType, eventData)!;
        @event.EventTypeName = EventTypeName;
        @event.DotNetTypeName = DotNetTypeName;
        return @event;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Fisher.Events.Schema;
using Fisher.Serialization;
using Fisher.Storage;
using JasperFx.Core.Reflection;
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
public partial class EventGraph : EventRegistry, IAggregationSourceFactory<IQuerySession>
{
    private readonly ConcurrentDictionary<Type, FisherEventType> _eventTypes = new();
    private readonly StoreOptions _options;

    /// <summary>
    ///     Builds each aggregate's single-stream projection, closed statically wherever it can be
    ///     (fisher#398), including over a strong-typed id the configuration declared (fisher#423).
    /// </summary>
    internal Projections.SingleStreamProjectionFactory AggregateProjections { get; } = new();

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
    ///     How often an idle high-water agent re-stamps its progression row's <c>last_updated</c>.
    /// </summary>
    /// <remarks>See <see cref="EventStoreOptions.HighWaterLivenessInterval" /> — fisher#60.</remarks>
    public TimeSpan HighWaterLivenessInterval => _options.Events.HighWaterLivenessInterval;

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

    internal string ProjectionFloorsTableName =>
        FisherTableNaming.QuotedTableName(DatabaseSchemaName, Schema.ProjectionFloorsTable.TableSuffix);

    internal string DeadLetterTableName =>
        FisherTableNaming.QuotedTableName(DatabaseSchemaName, Schema.DeadLetterTable.TableSuffix);

    /// <summary>
    ///     <c>fi_dcb_tag_version</c> (fisher#635). See <see cref="Schema.DcbTagVersionTable" />.
    /// </summary>
    internal string DcbTagVersionTableName =>
        FisherTableNaming.QuotedTableName(DatabaseSchemaName, Schema.DcbTagVersionTable.TableSuffix);

    /// <summary>
    ///     <c>fi_dcb_tag_version_sequence</c> (fisher#635). See <see cref="Schema.DcbTagVersionSequenceTable" />.
    /// </summary>
    internal string DcbTagVersionSequenceTableName =>
        FisherTableNaming.QuotedTableName(DatabaseSchemaName, Schema.DcbTagVersionSequenceTable.TableSuffix);

    /// <summary>
    ///     The quoted table name holding one DCB tag type's rows.
    /// </summary>
    internal string TagTableName(ITagTypeRegistration registration)
        => FisherTableNaming.QuotedTableName(DatabaseSchemaName, EventTagTable.SuffixFor(registration));

    /// <summary>
    ///     The <c>fi_natural_key_&lt;alias&gt;</c> object name for one aggregate type (fisher#40).
    /// </summary>
    internal Weasel.Sqlite.SqliteObjectName NaturalKeyTableName(Type aggregateType)
        => FisherTableNaming.ObjectFor(DatabaseSchemaName, NaturalKeySuffixFor(aggregateType));

    /// <inheritdoc cref="NaturalKeyTableName" />
    internal string QuotedNaturalKeyTableName(Type aggregateType)
        => FisherTableNaming.QuotedTableName(DatabaseSchemaName, NaturalKeySuffixFor(aggregateType));

    private static string NaturalKeySuffixFor(Type aggregateType)
        => $"natural_key_{ToEventTypeName(aggregateType.Name)}";

    /// <summary>
    ///     The natural keys the registered aggregate projections declare.
    /// </summary>
    internal IReadOnlyList<NaturalKeyDefinition> NaturalKeyDefinitions() => _options.Projections.NaturalKeys;

    internal IEnumerable<NaturalKeyTable> BuildNaturalKeyTables(
        IEnumerable<NaturalKeyDefinition> definitions)
        => definitions.Select(definition => new NaturalKeyTable(this, definition));

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

    private object BuildClosedShapeEventStorage()
    {
        var dialect = new Storage.SqliteEventStoreDialect();
        var serializer = Weasel.Storage.StorageSerializerAdapter.For(Serializer);

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

        // fisher#398: ordinary generic calls for the canonical identity types, so live aggregation
        // works in a Native AOT image. It used to be MakeGenericType + Activator.CreateInstance, hidden
        // from ILC by this class's IL3050 suppression.
#pragma warning disable CS8714 // TDoc is unconstrained here but SingleStreamProjection requires notnull
        var projection = AggregateProjections.Create<TDoc>(idType);
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

        // A C# 15 union is stored as its active case (jasperfx#1166, fisher#466): each case gets its own
        // event type name, and the JSON is unchanged because System.Text.Json writes the bare case
        // anyway. JasperFx's base BuildEvent does this, and this override does not end in it, so it has
        // to unwrap for itself. Before .NET 11 no type is a union and this hands the value back.
        eventData = UnionTypes.Unwrap(eventData);

        return EventMappingFor(eventData.GetType()).Wrap(eventData);
    }

    /// <remarks>
    ///     A new mapping is announced to <see cref="EventRegistry" />'s name cache
    ///     (<c>RegisterDotNetTypeNames</c>), because nothing else tells the base class a registration
    ///     happened and <see cref="EventRegistry.ResolveEventType" /> is the base's (fisher#574). Its
    ///     serializer is <see cref="EventRegistry.FindBinarySerializerFor" />: null for a marked type with
    ///     nothing configured, since mappings are built on read paths too.
    /// </remarks>
    public override FisherEventType EventMappingFor(Type eventType)
    {
        if (_eventTypes.TryGetValue(eventType, out var existing))
        {
            return existing;
        }

        var mapping = _eventTypes.GetOrAdd(eventType, type => new FisherEventType(type, FindBinarySerializerFor(type)));
        RegisterDotNetTypeNames(mapping);
        return mapping;
    }

    // ---- binary event serialization (fisher#93) ----

    // The registry is EventRegistry's (fisher#574): DefaultBinarySerializer, UseBinarySerializer<TEvent>,
    // FindBinarySerializerFor and ResolveBinarySerializerFor are the base's, so a serializer configured
    // through a base-typed reference is the one Fisher writes with. Fisher keeps the resolved answer on
    // each mapping, which is why a change has to reach the mappings already built.

    /// <summary>
    ///     A serializer registration changed, so refresh every mapping already built. Registration order is
    ///     not fixed: <c>AddEventType&lt;T&gt;()</c> or a projection may build a mapping before either way
    ///     of configuring a serializer.
    /// </summary>
    protected override void OnBinarySerializersChanged()
    {
        foreach (var mapping in _eventTypes.Values)
        {
            mapping.BinarySerializer = FindBinarySerializerFor(mapping.EventType);
        }
    }

    /// <summary>
    ///     The encoded body for an event stored through an <see cref="IEventBinarySerializer" />, or
    ///     null when it is stored as JSON (fisher#93).
    /// </summary>
    /// <remarks>
    ///     A type marked <see cref="BinaryEventAttribute" /> with no serializer configured is refused
    ///     by name here rather than reverting to JSON. Silently writing JSON would put rows in the
    ///     store in a format the operator did not choose and believes they are not using — and the
    ///     next process to run <em>with</em> a serializer configured would read them correctly only
    ///     because the row, not the type, decides.
    /// </remarks>
    internal byte[]? BinaryEncoderFor(IEvent @event)
    {
        var eventType = @event.EventType;
        var mapping = EventMappingFor(eventType);

        if (mapping.BinarySerializer is { } serializer)
        {
            // The byte[] overload, not the IBufferWriter one (jasperfx#1187), deliberately (fisher#555).
            // The BLOB parameter has to be an exact-length byte[] that lives until the command runs, so
            // writing into a pooled buffer still ends in a copy to a new array. For a serializer that
            // implements only the byte[] pair, the buffer-writer default adds a second copy on top.
            return serializer.Serialize(eventType, @event.Data);
        }

        if (mapping.IsMarkedBinary)
        {
            throw new InvalidOperationException(
                $"'{eventType.Name}' is marked [BinaryEvent] but this store has no binary serializer "
                + "for it. Set StoreOptions.Events.DefaultBinarySerializer, or register one for this "
                + $"type with StoreOptions.Events.UseBinarySerializer<{eventType.Name}>(...).");
        }

        return null;
    }

    /// <summary>
    ///     Refuse an operation that reads into an event body for a type stored as a BLOB.
    /// </summary>
    /// <remarks>
    ///     The trade <see cref="BinaryEventAttribute" /> names, enforced where it bites: a binary body
    ///     is not readable by <c>json_extract</c>, so a query over one would quietly match nothing —
    ///     <c>data</c> is null for those rows and <c>json_extract(null, …)</c> is null. Returning an
    ///     empty result would be the wrong kind of answer to a question that cannot be asked.
    /// </remarks>
    /// <summary>
    ///     Refuse an operation that rewrites an event body for a type stored as a BLOB.
    /// </summary>
    /// <remarks>
    ///     <b>The single most likely way this feature could corrupt data</b>, and the reason fisher#43
    ///     called it out. Both rewrite operations write the <c>data</c> column; against a binary event
    ///     that leaves the row carrying a JSON body <em>and</em> a BLOB body, which every reader then
    ///     resolves by the event type — so the JSON is invisible and the row is quietly wrong. Refusing
    ///     is an acceptable answer here; writing both is not.
    /// </remarks>
    internal void AssertBodyIsRewritable(Type eventType, string operation)
    {
        var mapping = EventMappingFor(eventType);

        if (mapping.IsBinary)
        {
            throw new InvalidOperationException(
                $"'{eventType.Name}' is marked [BinaryEvent], so {operation} cannot rewrite it — the "
                + "rewrite writes the JSON data column, which would leave the row holding a JSON body "
                + "and a BLOB body at once. Archive or compact the stream instead.");
        }

        // A rewrite stores the event's current shape under its current name, so a transformation on
        // that same name would upcast the rewritten row on its next read (fisher#494). There is no
        // other name to store it under, so this one is refused rather than left to read back wrong.
        if (Upcasters.HasAny && Upcasters.TryFindTransformation(mapping.EventTypeName, out _))
        {
            throw new InvalidOperationException(
                $"'{eventType.Name}' is stored as event type '{mapping.EventTypeName}', and an upcast is "
                + $"registered on that same name, so {operation} cannot rewrite it: the next read would "
                + "upcast the current shape as though it were the old one. Give the current event type "
                + "its own name, so the upcast's source and target differ.");
        }
    }

    internal void AssertBodyIsQueryable(Type eventType, string operation)
    {
        if (EventMappingFor(eventType).IsBinary)
        {
            throw new InvalidOperationException(
                $"'{eventType.Name}' is marked [BinaryEvent], so its body is a BLOB in data_binary and "
                + $"{operation} cannot reach into it — json_extract reads the JSON column, which is null "
                + "for a binary event. Query its metadata with QueryEventsAsync, or drop [BinaryEvent] "
                + "from the type.");
        }
    }

    public override void AddEventType(Type eventType)
    {
        // A union is never stored as itself (BuildEvent unwraps it), so registering one means registering
        // each of its cases, which is what the base does and what this override has to repeat.
        foreach (var type in eventType.ExpandToCaseTypes())
        {
            EventMappingFor(type);
        }
    }

    /// <summary>
    ///     Every event type this graph has been told about.
    /// </summary>
    public IReadOnlyList<FisherEventType> AllKnownEventTypes() => _eventTypes.Values.ToList();

    /// <summary>
    ///     The alias persisted in <c>fi_streams.type</c>: the aggregate type's SIMPLE name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the name is Fisher's. The cache, <see cref="EventRegistry.AggregateTypeFor" /> and
    ///         <see cref="EventRegistry.TryResolveAggregateType" /> are the base's (fisher#574), so a
    ///         base-typed caller resolves the aliases Fisher recorded. Two aggregate types with the same
    ///         <c>Name</c> in different namespaces share an alias and the first seen wins.
    ///     </para>
    ///     <para>
    ///         <see cref="EventRegistry.ResolveEventType" /> is the base's too, memoized per name with misses
    ///         cached (fisher#156), which <see cref="EventMappingFor" /> feeds.
    ///     </para>
    /// </remarks>
    protected override string AliasFor(Type aggregateType) => aggregateType.Name;

    internal StreamsTable BuildStreamsTable() => new(this);

    internal EventsTable BuildEventsTable() => new(this);

    internal EventProgressionTable BuildEventProgressionTable() => new(this);

    internal DeadLetterTable BuildDeadLetterTable() => new(this);

    internal ProjectionFloorsTable BuildProjectionFloorsTable() => new(this);

    /// <summary>
    ///     One table per registered tag type.
    /// </summary>
    internal IEnumerable<EventTagTable> BuildTagTables()
        => TagTypes.Select(registration => new EventTagTable(this, registration));

    /// <summary>
    ///     The DCB tag version tables (fisher#635), only when a tag type is registered.
    /// </summary>
    internal IEnumerable<Weasel.Sqlite.Tables.Table> BuildDcbTagVersionTables()
    {
        if (TagTypes.Count == 0) yield break;

        yield return new DcbTagVersionTable(this);
        yield return new DcbTagVersionSequenceTable(this);
    }

    internal const string TagTypeInferenceUnreferencedCode =
        "Discovers the tag type's inner value through ValueTypeInfo, which reflects over its members. Under trimming or Native AOT use RegisterTagType<TTag, TInner>(Func<TTag, TInner>), which wraps TagTypeRegistration.FromUnwrapper<TTag, TInner>.";

    internal const string TagTypeInferenceDynamicCode =
        "Closes TagTypeRegistration<TTag, TInner> with MakeGenericType. Under Native AOT use RegisterTagType<TTag, TInner>(Func<TTag, TInner>), which wraps TagTypeRegistration.FromUnwrapper<TTag, TInner> and closes nothing at runtime.";

    /// <summary>
    ///     The table suffix of a tag type registered without one: the type name in Fisher's snake_case,
    ///     <c>HarbourId</c> becoming <c>harbour_id</c>.
    /// </summary>
    /// <remarks>
    ///     The registration API (<c>RegisterTagType</c>, <c>FindTagType</c>, <c>TagTypes</c>) is
    ///     <see cref="EventRegistry" />'s (fisher#574), so a tag type registered through a base-typed
    ///     reference gets its table. Only the suffix rule is Fisher's, because it names a table in a
    ///     deployed store and JasperFx's default spelling differs for some names.
    /// </remarks>
    protected override string DefaultTagTableSuffixFor(Type tagType) => ToEventTypeName(tagType.Name);

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
public class FisherEventType : IEventType
{
    public FisherEventType(Type eventType, IEventBinarySerializer? binarySerializer = null)
    {
        EventType = eventType;
        EventTypeName = EventGraph.ToEventTypeName(eventType.Name);
        DotNetTypeName = $"{eventType.FullName}, {eventType.Assembly.GetName().Name}";

        // Read once, here, because every append of this type asks — and because the answer cannot
        // change: it is an attribute on the type. The serializer is not the same question and can be
        // registered after the mapping exists, which is why it is settable.
        IsMarkedBinary = eventType.IsDefined(typeof(BinaryEventAttribute), false);
        BinarySerializer = binarySerializer;
    }

    public Type EventType { get; }

    /// <summary>
    ///     The serializer this event type's body is stored through, or null when it is stored as JSON
    ///     text in <c>data</c> (fisher#93).
    /// </summary>
    /// <remarks>
    ///     Settable because registration order is not fixed: <c>UseBinarySerializer&lt;T&gt;</c> and
    ///     <c>DefaultBinarySerializer</c> may both arrive after a mapping has been built by an
    ///     <c>AddEventType</c> or a projection registration.
    /// </remarks>
    public IEventBinarySerializer? BinarySerializer { get; internal set; }

    /// <summary>
    ///     Whether the type carries <see cref="BinaryEventAttribute" />. Distinct from
    ///     <see cref="IsBinary" />: marked with no serializer configured is a configuration error the
    ///     append refuses, not a silent reversion to JSON.
    /// </summary>
    public bool IsMarkedBinary { get; }

    /// <summary>
    ///     Whether new appends of this event type write a BLOB into <c>data_binary</c> rather than JSON
    ///     text into <c>data</c> (fisher#93).
    /// </summary>
    /// <remarks>
    ///     A write-side answer only. Reads dispatch per row on whether <c>data_binary</c> is null, so
    ///     rows written before this type opted in still read through the JSON path — which is what
    ///     makes turning a type binary an in-place change with no migration of existing event data.
    /// </remarks>
    public bool IsBinary => BinarySerializer is not null;
    public string EventTypeName { get; set; }
    public string DotNetTypeName { get; set; }
    public string Alias => EventTypeName;

    private Func<object, IEvent>? _wrapper;

    /// <summary>
    ///     Wrap raw event data into an <c>Event&lt;T&gt;</c> envelope carrying type metadata.
    /// </summary>
    /// <remarks>
    ///     Runs once per event hydrated by <see cref="Internal.FisherEventsRowReader" /> and once per
    ///     raw event appended through <see cref="EventGraph.BuildEvent" />, so the
    ///     <c>MakeGenericType</c> + <c>Activator.CreateInstance</c> pair is paid exactly once per
    ///     event type and the per-call cost is a compiled delegate invocation (fisher#156). JasperFx's
    ///     own <c>EventTypeData&lt;T&gt;</c> gets the fast shape by being generic; this type cannot
    ///     close over <c>T</c> without changing how <see cref="EventGraph.EventMappingFor" /> builds
    ///     mappings, so it caches the constructor as a delegate instead — the same pattern
    ///     JasperFx's <c>MessagePublishing</c> uses for <c>IMessageSink.PublishAsync&lt;T&gt;</c>, with the same
    ///     BCL <c>Expression.Compile</c>. The lazy init races benignly: two threads may each compile,
    ///     both delegates are correct, and one wins the field.
    /// </remarks>
    public IEvent Wrap(object eventData)
    {
        var wrapper = _wrapper ??= CompileWrapper(EventType);
        var @event = wrapper(eventData);
        @event.EventTypeName = EventTypeName;
        @event.DotNetTypeName = DotNetTypeName;
        return @event;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification =
            "Event<T> over a reference type T is served by Native AOT's shared canonical instantiation, so MakeGenericType finds native code for it, and Expression.Compile falls back to the interpreter. Measured in smoke/aot-consumer, which reads back and folds appended events (fisher#398). A value-type event would need its own instantiation and is not covered; was a class-level suppression until fisher#398.")]
    private static Func<object, IEvent> CompileWrapper(Type eventType)
    {
        var genericType = typeof(Event<>).MakeGenericType(eventType);
        var constructor = genericType.GetConstructor(new[] { eventType })
                          ?? throw new InvalidOperationException(
                              $"Event<{eventType.Name}> has no ({eventType.Name}) constructor.");

        var data = Expression.Parameter(typeof(object), "data");
        var body = Expression.Convert(
            Expression.New(constructor, Expression.Convert(data, eventType)),
            typeof(IEvent));

        return Expression.Lambda<Func<object, IEvent>>(body, data).Compile();
    }
}

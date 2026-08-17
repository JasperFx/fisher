using System.Text.Json;
using Fisher.Events;
using Fisher.Serialization;
using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Fetching;
using JasperFx.Events.Tags;
using Polly;
using Weasel.Core;

namespace Fisher;

/// <summary>
///     Configuration options for a Fisher <see cref="DocumentStore" />.
/// </summary>
public class StoreOptions
{
    public const int DefaultTimeout = 30;

    private AutoCreate? _autoCreate;
    private string _connectionString = string.Empty;
    private string _databaseSchemaName = FisherTableNaming.DefaultSchemaName;
    private Serialization.ISerializer? _serializer;

    public StoreOptions()
    {
        EventGraph = new EventGraph(this);
        Events.EventGraph = EventGraph;
        Projections = new Fisher.Projections.FisherProjectionOptions(EventGraph);
        Schema = new DocumentSchema(this);
        ResiliencePipeline = new ResiliencePipelineBuilder().AddFisherDefaults().Build();
    }

    /// <summary>
    ///     Document type registration, mirroring Marten's <c>StoreOptions.Schema</c>. Registering a
    ///     type here is what lets its table be created up front rather than on first use.
    /// </summary>
    public DocumentSchema Schema { get; }

    /// <summary>
    ///     The event graph configuration and registry. Created at construction time so projections can
    ///     register event types during configuration.
    /// </summary>
    public EventGraph EventGraph { get; }

    /// <summary>
    ///     Projection registration, the live aggregator cache, and the registry of aggregate types
    ///     discovered from source-generated evolvers.
    /// </summary>
    public Fisher.Projections.FisherProjectionOptions Projections { get; }

    /// <summary>
    ///     The connection string to the SQLite database, e.g. <c>Data Source=app.db</c>.
    /// </summary>
    public string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     Supply the connection string to the SQLite database. Provided for API parity with Marten's
    ///     <c>StoreOptions.Connection(string)</c> — equivalent to setting <see cref="ConnectionString" />.
    /// </summary>
    public void Connection(string connectionString) => ConnectionString = connectionString;

    /// <summary>
    ///     The logical schema name for this store's tables. Defaults to <c>main</c>.
    /// </summary>
    /// <remarks>
    ///     SQLite has no schemas, so unlike Marten and Polecat this does not qualify anything. It is
    ///     folded into the table name prefix instead — see <see cref="FisherTableNaming" />. Two stores
    ///     over the same database file with different values here do not collide.
    /// </remarks>
    public string DatabaseSchemaName
    {
        get => _databaseSchemaName;
        set => _databaseSchemaName = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     A logical name for this store, used to build a distinct <see cref="IEventStore" /> identity so
    ///     multiple Fisher stores in one application are distinguishable. Defaults to "Main".
    /// </summary>
    public string StoreName { get; set; } = "Main";

    /// <summary>
    ///     Whether Fisher should create or update database schema objects at runtime. Defaults to
    ///     <see cref="AutoCreate.CreateOrUpdate" /> for development convenience.
    /// </summary>
    public AutoCreate AutoCreateSchemaObjects
    {
        get => _autoCreate ?? AutoCreate.CreateOrUpdate;
        set => _autoCreate = value;
    }

    /// <summary>
    ///     Default command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = DefaultTimeout;

    /// <summary>
    ///     The Hi-Lo settings applied to any document type with an <c>int</c> or <c>long</c> identity
    ///     that has no <see cref="DocumentMapping.HiloSettings" /> of its own.
    /// </summary>
    public Weasel.Core.Sequences.HiloSettings HiloSequenceDefaults { get; } = new();

    /// <summary>
    ///     Event store configuration.
    /// </summary>
    public EventStoreOptions Events { get; } = new();

    /// <summary>
    ///     Settings for the async projection daemon.
    /// </summary>
    public DaemonSettings DaemonSettings { get; } = new();

    /// <summary>
    ///     The concurrent-connection ceiling this store assumes, used to derive
    ///     <see cref="JasperFx.Events.IEventStore.MaxConcurrentRebuildsPerDatabase" /> when that is not
    ///     configured explicitly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>This is not a connection-string setting, and that is the divergence.</strong>
    ///         Marten and Polecat derive the rebuild cap from a real pool ceiling — Npgsql's and
    ///         SqlClient's <c>Max Pool Size</c> keyword. <c>Microsoft.Data.Sqlite</c> has no such
    ///         keyword; its <c>SqliteConnectionStringBuilder</c> exposes only a boolean
    ///         <c>Pooling</c>. So Fisher carries the ceiling as a store option instead, and nothing
    ///         folds it into the connection string.
    ///     </para>
    ///     <para>
    ///         The default of 8 is chosen for the cap it produces, not as a pooling recommendation:
    ///         <c>max(1, 8 / 8)</c> is 1, and one is the honest answer for SQLite. Writers serialize at
    ///         the file level, so concurrent rebuild cells contend for the same write lock rather than
    ///         running in parallel. Raise it only against a measurement.
    ///     </para>
    /// </remarks>
    public int MaxPoolSize { get; set; } = 8;

    /// <summary>
    ///     Get or set the serializer. Defaults to Fisher's System.Text.Json <see cref="Serializer" />.
    /// </summary>
    public Serialization.ISerializer Serializer
    {
        get => _serializer ??= new Serializer();
        set => _serializer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     PRAGMA settings applied to every connection this store opens. Defaults to Weasel's
    ///     general-purpose profile (WAL journaling, NORMAL synchronous, 64MB cache).
    /// </summary>
    /// <remarks>
    ///     WAL matters more here than a tuning knob normally would: it is what lets Fisher's async
    ///     daemon read while a session writes, which under SQLite's default rollback journal would
    ///     block.
    /// </remarks>
    public Weasel.Sqlite.SqlitePragmaSettings PragmaSettings { get; set; }
        = Weasel.Sqlite.SqlitePragmaSettings.Default;

    /// <summary>
    ///     Set by <c>ApplyAllDatabaseChangesOnStartup()</c>; consumed by the hosted service.
    /// </summary>
    internal bool ShouldApplyChangesOnStartup { get; set; }

    /// <summary>
    ///     Back-reference to the store's database, set during <see cref="DocumentStore" /> construction so
    ///     sessions can expose it through the shared <c>Weasel.Storage.IStorageSession.Database</c> seam.
    /// </summary>
    internal FisherDatabase? StorageDatabase { get; set; }

    /// <summary>
    ///     The Polly resilience pipeline wrapped around all SQL execution. Defaults to retrying the
    ///     transient SQLite busy/locked errors.
    /// </summary>
    internal ResiliencePipeline ResiliencePipeline { get; set; }

    /// <summary>
    ///     Additional tables to be managed by this store alongside Fisher's own schema objects.
    /// </summary>
    public List<ISchemaObject> ExtendedSchemaObjects { get; } = new();

    /// <summary>
    ///     Give each tenant its own SQLite file, rather than a <c>tenant_id</c> column in one
    ///     (fisher#47).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The alternative to conjoined tenancy, not an addition to it — a store picks one, as it
    ///         does on both siblings. See <see cref="Storage.ITenancy" /> for why file-per-tenant is
    ///         arguably SQLite's <em>best</em> tenancy story: a tenant is a file, and tenants write
    ///         concurrently instead of contending for one write lock.
    ///     </para>
    ///     <para>
    ///         Tenants are fixed at configuration time here. For a set that changes while the store is
    ///         running, use <see cref="MultiTenantedDatabasesFrom" /> (fisher#58).
    ///     </para>
    /// </remarks>
    public void MultiTenantedDatabases(Action<Storage.TenantDatabases> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var databases = new Storage.TenantDatabases();
        configure(databases);

        TenantDatabases = databases;
    }

    internal Storage.TenantDatabases? TenantDatabases { get; private set; }

    /// <summary>
    ///     A database file per tenant, with the set of tenants asked for rather than declared
    ///     (fisher#58).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A tenant that did not exist when the store was built resolves anyway, and its file and
    ///         schema are created the first time anything connects to it — which is only a reasonable
    ///         offer because provisioning here is a file plus a migration rather than a
    ///         <c>CREATE DATABASE</c>.
    ///     </para>
    ///     <para>
    ///         <see cref="Storage.DirectoryTenantSource" /> is the convention form and needs no
    ///         registration step at all; <see cref="Storage.InMemoryTenantSource" /> is for an
    ///         application pushing its own tenants table in. A source reading that table directly is an
    ///         application's to write — <see cref="Storage.ITenantSource" /> is two members so that it
    ///         can be.
    ///     </para>
    /// </remarks>
    public void MultiTenantedDatabasesFrom(Storage.ITenantSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        TenantSource = source;
    }

    /// <inheritdoc cref="MultiTenantedDatabasesFrom(Storage.ITenantSource)" />
    /// <param name="directory">Where the tenant files live, named <c>&lt;tenantId&gt;.db</c>.</param>
    public void MultiTenantedDatabasesInDirectory(string directory)
        => MultiTenantedDatabasesFrom(new Storage.DirectoryTenantSource(directory));

    internal Storage.ITenantSource? TenantSource { get; private set; }

    /// <summary>
    ///     Configuration applied to every document mapping as it is created (fisher#39).
    /// </summary>
    /// <remarks>
    ///     The weakest of the four configuration layers — policy, then metadata interface, then schema
    ///     attribute, then <c>Schema.For&lt;T&gt;()</c>. See <see cref="Storage.StorePolicies" />.
    /// </remarks>
    public Storage.StorePolicies Policies { get; } = new();

    /// <summary>
    ///     Data seeded at startup, after the schema has been applied (fisher#39).
    /// </summary>
    /// <remarks>
    ///     Run by <c>AddFisher</c>'s hosted services. Building a store by hand runs nothing — there is
    ///     no startup to hang it off — which is why <see cref="IInitialData" /> says the seeders belong
    ///     in a hosted application.
    /// </remarks>
    public InitialDataCollection InitialData { get; } = new();

    /// <summary>
    ///     Unit-of-work hooks applied to every session this store opens (fisher#32).
    /// </summary>
    /// <remarks>
    ///     A session runs these first and then any on its own <see cref="SessionOptions.Listeners" />.
    ///     Read once per session and cached, so adding one after a session is open does not reach it.
    /// </remarks>
    public IList<IDocumentSessionListener> Listeners { get; } = new List<IDocumentSessionListener>();

    /// <summary>
    ///     Replace the default Polly resilience pipeline with a custom one.
    /// </summary>
    public void ConfigurePolly(Action<ResiliencePipelineBuilder> configure)
    {
        var builder = new ResiliencePipelineBuilder();
        configure(builder);
        ResiliencePipeline = builder.Build();
    }

    /// <summary>
    ///     Extend the default Polly resilience pipeline. The default transient retry is applied first,
    ///     then your additions.
    /// </summary>
    public void ExtendPolly(Action<ResiliencePipelineBuilder> configure)
    {
        var builder = new ResiliencePipelineBuilder();
        builder.AddFisherDefaults();
        configure(builder);
        ResiliencePipeline = builder.Build();
    }

    /// <summary>
    ///     Configure the serialization settings for the document store.
    /// </summary>
    public void ConfigureSerialization(
        EnumStorage enumStorage = EnumStorage.AsInteger,
        Casing casing = Casing.CamelCase,
        CollectionStorage collectionStorage = CollectionStorage.Default,
        NonPublicMembersStorage nonPublicMembersStorage = NonPublicMembersStorage.Default,
        Action<JsonSerializerOptions>? configure = null)
    {
        var serializer = new Serializer
        {
            Casing = casing,
            EnumStorage = enumStorage,
            CollectionStorage = collectionStorage,
            NonPublicMembersStorage = nonPublicMembersStorage
        };

        configure?.Invoke(serializer.Options);
        Serializer = serializer;
    }

    /// <summary>
    ///     Register a strong-typed identifier — a wrapper around a single <see cref="Guid" />,
    ///     <c>string</c>, <c>int</c> or <c>long</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Fisher discovers these on its own, so calling this is never required.</b> It exists
    ///         because Marten's <c>StoreOptions.RegisterValueType&lt;T&gt;()</c> does and Polecat gained
    ///         one in polecat#459, and store configuration ought to be portable: a shared block that
    ///         reads <c>opts.RegisterValueType&lt;AlertId&gt;()</c> on two stores should not have to drop
    ///         the line for the third and explain the omission in a comment (fisher#75).
    ///     </para>
    ///     <para>
    ///         It is not quite a no-op, and the difference is what makes it worth having rather than
    ///         merely accepting. Discovery has to treat "not a wrapper" as the ordinary answer, because
    ///         it is asked about every candidate identity member of every type. Naming a type here is an
    ///         <em>assertion</em> that it is one, so the same answer becomes a configuration error and is
    ///         reported at configuration time — rather than surfacing much later as "has no identity
    ///         member" from a place that cannot mention the wrapper.
    ///     </para>
    /// </remarks>
    /// <typeparam name="TValueType">The wrapper, e.g. <c>readonly record struct OrderId(Guid Value)</c>.</typeparam>
    /// <returns>The resolved shape, matching Marten's and Polecat's return type.</returns>
    /// <exception cref="JasperFx.Core.Reflection.InvalidValueTypeException">
    ///     <typeparamref name="TValueType" /> is not a usable wrapper, or wraps something Fisher cannot
    ///     store as an identity.
    /// </exception>
    public JasperFx.Core.Reflection.ValueTypeInfo RegisterValueType<TValueType>() where TValueType : notnull
        => Storage.StrongTypedId.Register(typeof(TValueType));

    /// <summary>
    ///     Register a strong-typed identifier by <see cref="Type" />. See
    ///     <see cref="RegisterValueType{TValueType}" /> for why this exists.
    /// </summary>
    /// <exception cref="JasperFx.Core.Reflection.InvalidValueTypeException">
    ///     <paramref name="type" /> is not a usable wrapper, or wraps something Fisher cannot store as
    ///     an identity.
    /// </exception>
    public JasperFx.Core.Reflection.ValueTypeInfo RegisterValueType(Type type)
        => Storage.StrongTypedId.Register(type);

    internal void AssertValid()
    {
        // Under database-per-tenant there is no store-level connection string to set: every tenant
        // names its own file, and a store-level one would be a database nothing writes to (fisher#47).
        if (string.IsNullOrWhiteSpace(_connectionString) && TenantDatabases is null && TenantSource is null)
        {
            throw new InvalidOperationException(
                "A connection string must be configured. Set StoreOptions.ConnectionString, or name a "
                + "database per tenant with MultiTenantedDatabases(...) or MultiTenantedDatabasesFrom(...).");
        }
    }
}

/// <summary>
///     Configuration specific to the event store.
/// </summary>
public class EventStoreOptions : IEventStoreInstrumentation
{
    internal EventGraph? EventGraph { get; set; }

    /// <summary>
    ///     Controls whether streams are identified by Guid or string. Defaults to
    ///     <see cref="StreamIdentity.AsGuid" />.
    /// </summary>
    public StreamIdentity StreamIdentity { get; set; } = StreamIdentity.AsGuid;

    /// <summary>
    ///     Controls the tenancy style for the event store. Defaults to <see cref="TenancyStyle.Single" />.
    /// </summary>
    public TenancyStyle TenancyStyle { get; set; } = TenancyStyle.Single;

    /// <summary>
    ///     Override the schema name for event store tables. When null, uses
    ///     <see cref="StoreOptions.DatabaseSchemaName" />.
    /// </summary>
    public string? DatabaseSchemaName { get; set; }

    /// <summary>
    ///     Enable tracking of correlation id metadata on events.
    /// </summary>
    public bool EnableCorrelationId { get; set; }

    /// <summary>
    ///     Enable tracking of causation id metadata on events.
    /// </summary>
    public bool EnableCausationId { get; set; }

    /// <summary>
    ///     Enable tracking of custom headers metadata on events.
    /// </summary>
    public bool EnableHeaders { get; set; }

    /// <summary>
    ///     Enable tracking of the user / last-modified-by metadata on events, persisted to the opt-in
    ///     <c>user_name</c> column.
    /// </summary>
    public bool EnableUserName { get; set; }

    /// <summary>
    ///     The store-wide fallback serializer for event types marked
    ///     <see cref="JasperFx.Events.BinaryEventAttribute" /> (fisher#93). Null — the default —
    ///     means every such type must name its own serializer through
    ///     <see cref="UseBinarySerializer{TEvent}" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not a schema decision.</b> <c>fi_events.data_binary</c> is always present, so
    ///         turning a single event type binary is an in-place change on a live store with no
    ///         migration of existing event data, and turning it back off is equally safe: reads
    ///         dispatch per row on whether that column is null, not on the type's current setting.
    ///     </para>
    ///     <para>
    ///         Fisher ships no implementation — see
    ///         <see cref="JasperFx.Events.IEventBinarySerializer" />. A binary encoding is a choice
    ///         with real consequences for schema evolution, and picking one would be Fisher deciding
    ///         how the application's data ages. The seam is here; the encoding is yours. Because the
    ///         interface lives in <c>JasperFx.Events</c>, one implementation serves Fisher, Marten
    ///         and Polecat alike.
    ///     </para>
    /// </remarks>
    public IEventBinarySerializer? DefaultBinarySerializer
    {
        get => EventGraph!.DefaultBinarySerializer;
        set => EventGraph!.DefaultBinarySerializer = value;
    }

    /// <summary>
    ///     Store this one event type's body through <paramref name="serializer" /> rather than as JSON
    ///     text (fisher#93), whether or not it carries
    ///     <see cref="JasperFx.Events.BinaryEventAttribute" />.
    /// </summary>
    /// <remarks>
    ///     Beats the attribute plus <see cref="DefaultBinarySerializer" />, and is the route for an
    ///     event type whose source you do not own.
    /// </remarks>
    public EventStoreOptions UseBinarySerializer<TEvent>(IEventBinarySerializer serializer)
        where TEvent : notnull
    {
        EventGraph!.UseBinarySerializer<TEvent>(serializer);
        return this;
    }

    /// <summary>
    ///     Keep recently fetched snapshots of <typeparamref name="T" /> in a node-local cache, so that a
    ///     later <c>FetchForWriting&lt;T&gt;</c> folds only the events after the cached one rather than
    ///     the stream's whole history (fisher#97 / jasperfx#674). <b>Off for every aggregate type by
    ///     default.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The cached snapshot is only ever a baseline.</b> The stream's version and every event
    ///         after the cached one are still read on every call, and the optimistic concurrency
    ///         assertion on append is untouched — so a stale entry costs a larger delta fold, never a
    ///         wrong aggregate and never a suppressed concurrency failure.
    ///     </para>
    ///     <para>
    ///         <b>Worth more here than the same option is on Marten or Polecat.</b> There the cache
    ///         removes a snapshot <em>load</em>; Fisher's <c>FetchForWriting</c> folds the stream on
    ///         every call by design, so what a hit removes is the fold itself. That also makes it per
    ///         aggregate type rather than store-wide: the win is proportional to how often one stream is
    ///         fetched for writing, so it is real on a hot aggregate and only overhead on one written
    ///         once.
    ///     </para>
    ///     <para>
    ///         The cache is node-local and deliberately incoherent between processes, which is sound for
    ///         exactly the reason above. Supply your own implementation — or a size limit for the
    ///         default bounded one — through <see cref="AggregateWriteCaching" />.
    ///     </para>
    /// </remarks>
    public EventStoreOptions CacheAggregatesForWriting<T>(int sizeLimit = 1000) where T : class
    {
        EventGraph!.CacheAggregatesForWriting<T>(sizeLimit);
        return this;
    }

    /// <summary>
    ///     The <c>FetchForWriting</c> snapshot cache's own configuration — which types are enrolled, the
    ///     default cache's size limit, and the seam for supplying a cache of your own.
    /// </summary>
    /// <inheritdoc cref="CacheAggregatesForWriting{T}" />
    public AggregateWriteCacheOptions AggregateWriteCaching => EventGraph!.AggregateWriteCaching;

    /// <summary>
    ///     Run inline projections' side effects when an inline projection is applied during
    ///     <c>SaveChangesAsync</c>. Off by default.
    /// </summary>
    public bool EnableSideEffectsOnInlineProjections { get; set; }

    /// <summary>
    ///     Opt into extended columns on the event progression table for monitoring tools.
    /// </summary>
    public bool EnableExtendedProgressionTracking { get; set; }

    /// <summary>
    ///     How often an idle high-water agent re-stamps <c>last_updated</c> on its progression row to
    ///     show its poll loop is still cycling. Five seconds by default; zero or less turns it off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         fisher#60. The mark's <c>last_updated</c> otherwise moves only when the mark
    ///         <em>advances</em>, so a quiet store is indistinguishable from a dead daemon and the
    ///         high-water health check has nothing but the sequence-gap heuristic to go on. This is what
    ///         makes the column an honest liveness signal, and it needs no extra column and no dependency
    ///         on <see cref="EnableExtendedProgressionTracking" /> — which does not help here anyway, as
    ///         JasperFx's progression writer drops high-water states outright.
    ///     </para>
    ///     <para>
    ///         <b>It is a periodic write, and on SQLite that means the file's one write lock.</b> Small
    ///         and brief, but the reason it is throttled rather than done on every poll: at the daemon's
    ///         default one-second idle cadence an otherwise read-only store would become a permanent
    ///         1 Hz writer. Keep it comfortably below the health check's staleness threshold — the
    ///         defaults are five seconds against thirty.
    ///     </para>
    /// </remarks>
    public TimeSpan HighWaterLivenessInterval { get; set; } = TimeSpan.FromSeconds(5);

    bool IEventStoreInstrumentation.ExtendedProgressionEnabled
    {
        get => EnableExtendedProgressionTracking;
        set => EnableExtendedProgressionTracking = value;
    }

    /// <summary>
    ///     Optional observer invoked best-effort after each successful commit with the events appended in
    ///     that unit of work.
    /// </summary>
    public Action<IReadOnlyList<IEvent>>? AppendObserver { get; set; }

    /// <summary>
    ///     Where a projection's side-effect messages go. Defaults to a no-op that drops them.
    /// </summary>
    /// <remarks>
    ///     Fisher itself has no message bus and no outbox table, so the default
    ///     <c>NulloMessageOutbox</c> discards every published message rather than throwing. Replace this
    ///     with a bus integration's implementation to give <c>PublishMessage</c> somewhere to go — the
    ///     batch it vends chooses its own delivery guarantee through
    ///     <see cref="Events.Messaging.IMessageBatch.BeforeCommitAsync" /> (transactional) or
    ///     <see cref="Events.Messaging.IMessageBatch.AfterCommitAsync" /> (post-commit).
    /// </remarks>
    public Events.Messaging.IMessageOutbox MessageOutbox { get; set; }
        = Events.Messaging.NulloMessageOutbox.Instance;

    /// <summary>
    ///     Pre-register an event type. Not strictly necessary — event types are registered on the fly as
    ///     they are appended — but pre-registration lets the async daemon resolve an event type name
    ///     before that process has ever appended one.
    /// </summary>
    public void AddEventType<TEvent>() where TEvent : notnull => EventGraph!.AddEventType(typeof(TEvent));

    /// <inheritdoc cref="AddEventType{TEvent}" />
    public void AddEventType(Type eventType) => EventGraph!.AddEventType(eventType);

    /// <inheritdoc cref="AddEventType{TEvent}" />
    public void AddEventTypes(IEnumerable<Type> eventTypes)
    {
        foreach (var eventType in eventTypes)
        {
            EventGraph!.AddEventType(eventType);
        }
    }

    /// <summary>
    ///     Register a tag type for Dynamic Consistency Boundary (DCB) support with an explicit table
    ///     suffix.
    /// </summary>
    public ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull
        => EventGraph!.RegisterTagType<TTag>(tableSuffix);

    /// <inheritdoc cref="RegisterTagType{TTag}(string)" />
    public ITagTypeRegistration RegisterTagType<TTag>() where TTag : notnull
        => EventGraph!.RegisterTagType<TTag>();

    /// <inheritdoc cref="EventGraph.AddMaskingRuleForProtectedInformation{T}(Action{T})" />
    public void AddMaskingRuleForProtectedInformation<T>(Action<T> masking) where T : notnull
        => EventGraph!.AddMaskingRuleForProtectedInformation(masking);

    /// <inheritdoc cref="EventGraph.AddMaskingRuleForProtectedInformation{T}(Func{T,T})" />
    public void AddMaskingRuleForProtectedInformation<T>(Func<T, T> masking) where T : notnull
        => EventGraph!.AddMaskingRuleForProtectedInformation(masking);
}

using System.Text.Json;
using Fisher.Events;
using Fisher.Serialization;
using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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

    internal void AssertValid()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "A connection string must be configured. Set StoreOptions.ConnectionString.");
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
    ///     Run inline projections' side effects when an inline projection is applied during
    ///     <c>SaveChangesAsync</c>. Off by default.
    /// </summary>
    public bool EnableSideEffectsOnInlineProjections { get; set; }

    /// <summary>
    ///     Opt into extended columns on the event progression table for monitoring tools.
    /// </summary>
    public bool EnableExtendedProgressionTracking { get; set; }

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

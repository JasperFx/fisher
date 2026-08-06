using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Fisher.Internal;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using Weasel.Sqlite.Tables;

namespace Fisher.Projections.Flattened;

/// <summary>
///     Projects events into a plain relational table rather than into a document, through declarative
///     column mappings.
/// </summary>
/// <remarks>
///     <para>
///         Usage — the table's shape is declared in the constructor, the mappings alongside it:
///     </para>
///     <code>
///     public class QuestMetricsProjection : FlatTableProjection
///     {
///         public QuestMetricsProjection() : base("quest_metrics")
///         {
///             Table.AddColumn("id", "TEXT").AsPrimaryKey();
///
///             Project&lt;QuestStarted&gt;(map =>
///             {
///                 map.Map(x => x.Name, "quest_name");
///                 map.SetValue("member_count", 0);
///             });
///
///             Project&lt;MembersJoined&gt;(map => map.Increment("member_count"));
///
///             Delete&lt;QuestEnded&gt;();
///         }
///     }
///     </code>
///     <para>
///         Three things differ from Marten's and Polecat's flat tables, all of them SQLite-shaped:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <strong>The table name folds the store's schema in rather than being qualified by
///                 it</strong> (<see cref="Storage.FisherTableNaming.UserTableName" />), because SQLite
///                 has no schemas — the same rule every other Fisher table follows, minus the
///                 <c>fi_</c> family prefix, which marks a table Fisher owns the shape of. A flat
///                 table's shape is the projection's.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <strong>The table is created by the migration, not lazily on first write.</strong>
///                 Registering the projection puts a <see cref="FlatTableFeatureSchema" /> into the
///                 store's feature set, so <c>ApplyAllConfiguredChangesToDatabaseAsync</c> creates it
///                 with everything else and <c>AutoCreate.None</c> is honoured for free. Polecat issues
///                 a CREATE TABLE from inside the first apply instead, which quietly bypasses the
///                 store's schema policy.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <strong>Rebuild teardown is told the table name directly.</strong> A flat table is
///                 not a document, so <see cref="PublishedTypes" /> is empty and the mapped-type sweep
///                 that empties a snapshot's table cannot see it — see
///                 <see cref="IPublishesTables" />.
///             </description>
///         </item>
///     </list>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: the mapping API compiles accessor delegates over the event types registered against it, which are preserved per the AOT publishing guide.")]
public abstract class FlatTableProjection : ProjectionBase,
    IProjectionSource<IDocumentSession, IQuerySession>,
    ISubscriptionFactory<IDocumentSession, IQuerySession>,
    IInlineProjection<IDocumentSession>,
    IJasperFxProjection<IDocumentSession>,
    IPublishesTables
{
    private readonly Dictionary<Type, IFlatTableEventHandler> _handlers = new();
    private readonly FlatTable _table;
    private readonly string _declaredTableName;
    private readonly string? _declaredSchemaName;
    private bool _compiled;

    /// <param name="tableName">
    ///     The unqualified table name. The store's <see cref="StoreOptions.DatabaseSchemaName" /> is
    ///     folded into it unless <paramref name="schemaName" /> pins one.
    /// </param>
    /// <param name="schemaName">
    ///     Pin the logical schema whose prefix to fold in, instead of taking the store's. Only needed
    ///     when a projection must land in a specific logical store's namespace regardless of which
    ///     store registers it — which is how the shared compliance suite addresses its table.
    /// </param>
    protected FlatTableProjection(string tableName, string? schemaName = null)
    {
        _declaredTableName = tableName;
        _declaredSchemaName = schemaName;
        _table = new FlatTable(Storage.FisherTableNaming.UserObjectFor(schemaName, tableName));

        Name = GetType().FullNameInCode();
    }

    /// <summary>
    ///     The table definition. Declare its primary key here; the mappings add the rest of the
    ///     columns as they name them.
    /// </summary>
    public Table Table => _table;

    /// <summary>
    ///     Fold the store's logical schema into the table's physical name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called once by <see cref="DocumentStore" />'s constructor, which is the first moment
    ///         <see cref="StoreOptions.DatabaseSchemaName" /> is final — a projection is usually
    ///         registered inside the same configuration lambda that sets it, in either order, and the
    ///         constructor cannot see the store at all.
    ///     </para>
    ///     <para>
    ///         Defaulting to the unprefixed name instead would be the quiet kind of wrong: SQLite has
    ///         no schemas, so the prefix <em>is</em> the isolation boundary between two logical stores
    ///         in one file, and a flat table that skipped it would be silently shared by both. A
    ///         projection that pinned its own schema keeps it.
    ///     </para>
    /// </remarks>
    internal void ResolveTableName(string storeSchemaName)
    {
        if (_declaredSchemaName is null)
        {
            _table.RenameTo(Storage.FisherTableNaming.UserTableName(storeSchemaName, _declaredTableName));
        }
    }

    /// <summary>
    ///     Map an event type onto the table's columns.
    /// </summary>
    /// <param name="configure">The column mappings for this event type.</param>
    /// <param name="primaryKeySource">
    ///     Which member of the event identifies the row. Defaults to the stream's identity.
    /// </param>
    protected void Project<TEvent>(Action<StatementMap<TEvent>> configure,
        Expression<Func<TEvent, object>>? primaryKeySource = null)
    {
        var map = new StatementMap<TEvent>(this, FlatTableExpressions.MemberPath(primaryKeySource));
        configure(map);

        _handlers[typeof(TEvent)] = map;

        IncludeType<TEvent>();
    }

    /// <summary>
    ///     Delete the row when this event type arrives.
    /// </summary>
    /// <param name="primaryKeySource">
    ///     Which member of the event identifies the row. Defaults to the stream's identity.
    /// </param>
    protected void Delete<TEvent>(Expression<Func<TEvent, object>>? primaryKeySource = null)
    {
        _handlers[typeof(TEvent)] = new EventDeleter<TEvent>(this, FlatTableExpressions.MemberPath(primaryKeySource));

        IncludeType<TEvent>();
    }

    public override void AssembleAndAssertValidity()
    {
        if (Table.PrimaryKeyColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"The flat table '{Table.Identifier.Name}' projected by {GetType().FullNameInCode()} has no "
                + "primary key column. Declare one with Table.AddColumn(...).AsPrimaryKey() in the "
                + "projection's constructor — every mapping upserts against it.");
        }

        if (_handlers.Count == 0)
        {
            throw new InvalidOperationException(
                $"{GetType().FullNameInCode()} maps no events. Register at least one Project<T>() or "
                + "Delete<T>().");
        }
    }

    /// <inheritdoc />
    /// <remarks>A flat table's rows are not documents, so nothing here is a document type.</remarks>
    public override IEnumerable<Type> PublishedTypes() => [];

    IEnumerable<string> IPublishesTables.PublishedTableNames() => [Table.Identifier.Name];

    // ---- applying events ----

    /// <summary>Inline: the rows land in the same transaction as the events that produced them.</summary>
    public Task ApplyAsync(IDocumentSession operations, IEnumerable<StreamAction> streams,
        CancellationToken cancellation)
        => ApplyAsync(operations, streams.SelectMany(x => x.Events).ToList(), cancellation);

    /// <summary>Async: the rows land in the same transaction as the shard's progression row.</summary>
    public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation)
    {
        if (operations is not FisherSession session)
        {
            return Task.CompletedTask;
        }

        CompileOnce(session.EventGraph);

        foreach (var e in events)
        {
            if (_handlers.TryGetValue(e.EventType, out var handler))
            {
                session.QueueOperation(handler.CreateOperation(e));
            }
        }

        return Task.CompletedTask;
    }

    /// <remarks>
    ///     Deferred to the first apply rather than done at registration because the statements depend
    ///     on the store's stream identity, which the projection's constructor cannot see.
    /// </remarks>
    private void CompileOnce(Events.EventGraph events)
    {
        if (_compiled)
        {
            return;
        }

        foreach (var handler in _handlers.Values)
        {
            handler.Compile(events);
        }

        _compiled = true;
    }

    // ---- projection source / subscription plumbing ----

    public IInlineProjection<IDocumentSession> BuildForInline() => this;

    /// <summary>
    ///     No optimised replay path: a rebuild tears the table down and replays through the ordinary
    ///     apply, which is what makes each event land exactly once.
    /// </summary>
    public bool TryBuildReplayExecutor(IEventStore<IDocumentSession, IQuerySession> store,
        IEventDatabase database, [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = null;
        return false;
    }

    public SubscriptionType Type => SubscriptionType.EventProjection;

    public Type ImplementationType => GetType();

    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];

    public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);

    IReadOnlyList<AsyncShard<IDocumentSession, IQuerySession>>
        ISubscriptionSource<IDocumentSession, IQuerySession>.Shards()
        =>
        [
            new AsyncShard<IDocumentSession, IQuerySession>(Options, ShardRole.Projection,
                new ShardName(Name, ShardName.All, Version), this, this)
        ];

    public ISubscriptionExecution BuildExecution(IEventStore<IDocumentSession, IQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
        => BuildExecution(store, database, loggerFactory.CreateLogger(GetType()), shardName);

    public ISubscriptionExecution BuildExecution(IEventStore<IDocumentSession, IQuerySession> store,
        IEventDatabase database, ILogger logger, ShardName shardName)
        => new ProjectionExecution<IDocumentSession, IQuerySession>(shardName, Options, store, database, this, logger);
}

/// <summary>
///     A projection whose output lives in tables the schema has no document mapping for.
/// </summary>
/// <remarks>
///     Rebuild teardown clears a projection's output by looking up each published type's document
///     table. A flat table publishes no types, so without this it would be replayed on top of the rows
///     the previous run left — see <c>DocumentStore.TeardownExistingProjectionStateAsync</c>.
/// </remarks>
internal interface IPublishesTables
{
    IEnumerable<string> PublishedTableNames();
}

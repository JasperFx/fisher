using Fisher.Linq;
using Fisher.Projections;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fisher.Tests.Projections;

/// <summary>
///     <c>AddProjectionWithServices</c> / <c>AddSubscriptionWithServices</c> — projections and
///     subscriptions built by the application's IoC container (fisher#194).
/// </summary>
/// <remarks>
///     <para>
///         <b>The load-bearing assertions are the scope-lifetime ones</b>, not the "it runs" ones. A
///         projection that merely works is the easy half: resolving it once at startup and holding it
///         passes every correctness test written against a single commit, and then fails in production
///         on the second daemon batch, when the scope it was resolved from has long been disposed. So
///         <see cref="a_scoped_projection_gets_one_scope_per_unit_of_work" /> counts scopes and
///         disposals rather than trusting the result, and
///         <see cref="a_scoped_projection_survives_many_batches" /> is the one that would actually
///         catch a captured provider.
///     </para>
///     <para>
///         Fisher writes none of the projection wrappers — they are
///         <c>JasperFx.Events.Projections.ContainerScoped</c>'s, shared with Marten — so these tests
///         are about Fisher's registration surface reaching the right one, and about the table mapping
///         that Fisher's own <c>Add(ProjectionBase, ...)</c> does and the base graph's narrower
///         overload does not.
///     </para>
/// </remarks>
public class container_scoped_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("container-scoped");

    public ValueTask InitializeAsync()
    {
        ScopeLedger.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private ServiceProvider ProviderFor(Action<FisherConfigurationExpression> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(x => x.SetMinimumLevel(LogLevel.Warning));
        services.AddScoped<GreetingSource>();

        var expression = services.AddFisher(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        configure(expression);

        return services.BuildServiceProvider();
    }

    private static async Task<DocumentStore> ReadyStoreAsync(ServiceProvider provider)
    {
        var store = provider.GetRequiredService<DocumentStore>();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        return store;
    }

    // ---------- the round trip ----------

    [Fact]
    public async Task an_inline_scoped_projection_runs_with_its_injected_service()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(id, new PersonNamed("Alice"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        var greeted = await query.LoadAsync<Greeted>(id, TestContext.Current.CancellationToken);

        greeted.ShouldNotBeNull();

        // The greeting is the injected service's, not a constant the projection could have carried —
        // so this fails if the projection was built with `new` rather than by the container.
        greeted.Greeting.ShouldBe("Hello, Alice");
    }

    /// <summary>
    ///     The published document type's table exists, which is Fisher's own contribution rather than
    ///     the shared wrapper's.
    /// </summary>
    /// <remarks>
    ///     A wrapper handed to <c>ProjectionGraph.Add(IProjectionSource, ...)</c> — which is what
    ///     Marten's equivalent calls — never passes the <c>PublishedTypes()</c> sweep that maps the
    ///     document type, so its table is never migrated. Silent at registration, and the projection
    ///     then writes to a table that does not exist. See fisher#111 for the same failure on the
    ///     non-DI path.
    /// </remarks>
    [Fact]
    public async Task the_projected_document_type_is_mapped_into_the_schema()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = provider.GetRequiredService<DocumentStore>();

        store.Options.Schema.HasMappingFor(typeof(Greeted)).ShouldBeTrue();
    }

    [Fact]
    public async Task the_projections_event_types_reach_the_event_graph()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = provider.GetRequiredService<DocumentStore>();

        store.Options.EventGraph.AllKnownEventTypes().Select(x => x.EventType)
            .ShouldContain(typeof(PersonNamed));
    }

    // ---------- scope lifetime, which is the design ----------

    /// <summary>
    ///     One scope per unit of work, opened and disposed inside it.
    /// </summary>
    [Fact]
    public async Task a_scoped_projection_gets_one_scope_per_unit_of_work()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);

        // Registration itself resolves the projection once, to read its name and filtering off it, so
        // the baseline is taken after the store is built rather than assumed to be zero.
        var before = ScopeLedger.Created;

        for (var i = 0; i < 3; i++)
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed($"Person {i}"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (ScopeLedger.Created - before).ShouldBe(3);

        // And every one of them was let go of. A wrapper that opened a scope per batch and kept it
        // would pass the count above and leak all three.
        ScopeLedger.Disposed.ShouldBe(ScopeLedger.Created);
    }

    /// <summary>
    ///     The failure a captured scope actually produces: fine on the first commit, dead on the next.
    /// </summary>
    [Fact]
    public async Task a_scoped_projection_survives_many_batches()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);
        var ids = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);

            await using var session = store.LightweightSession();
            session.Events.StartStream<Greeted>(id, new PersonNamed($"Person {i}"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();

        foreach (var id in ids)
        {
            var greeted = await query.LoadAsync<Greeted>(id, TestContext.Current.CancellationToken);
            greeted.ShouldNotBeNull();
        }
    }

    /// <summary>
    ///     Transient is treated as Scoped, which is what makes the two behave identically rather than
    ///     one of them silently capturing.
    /// </summary>
    [Fact]
    public async Task transient_behaves_as_scoped()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Transient));

        var store = await ReadyStoreAsync(provider);
        var before = ScopeLedger.Created;

        for (var i = 0; i < 2; i++)
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed($"Person {i}"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (ScopeLedger.Created - before).ShouldBe(2);
        ScopeLedger.Disposed.ShouldBe(ScopeLedger.Created);
    }

    /// <summary>
    ///     A Singleton registration is the projection itself, with no wrapper — so it opens no scopes.
    /// </summary>
    [Fact]
    public async Task a_singleton_projection_is_registered_directly()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<SingletonGreetedProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Singleton));

        var store = await ReadyStoreAsync(provider);

        store.Options.Projections.All
            .ShouldContain(x => ReferenceEquals(x, provider.GetRequiredService<SingletonGreetedProjection>()));

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(id, new PersonNamed("Bob"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        (await query.LoadAsync<Greeted>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Greeting.ShouldBe("Hi, Bob");
    }

    // ---------- the other projection kinds ----------

    [Fact]
    public async Task an_event_projection_can_take_services()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetingLogProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed("Carol"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        var entries = await query.Query<GreetingLogEntry>().ToListAsync(TestContext.Current.CancellationToken);

        entries.Single().Text.ShouldBe("Hello, Carol");
    }

    [Fact]
    public async Task a_multi_stream_projection_can_take_services()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetingRollupProjection>(
            ProjectionLifecycle.Inline, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed("Dave"));
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed("Erin"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        var rollup = await query.LoadAsync<GreetingRollup>(GreetingRollup.OnlyId,
            TestContext.Current.CancellationToken);

        rollup.ShouldNotBeNull();
        rollup.Count.ShouldBe(2);

        // Through the injected service, so this is not a count the projection could have kept alone.
        rollup.Last.ShouldBe("Hello, Erin");
    }

    // ---------- the async daemon, where a captured scope would be fatal ----------

    [Fact]
    public async Task a_scoped_projection_runs_under_the_async_daemon()
    {
        await using var provider = ProviderFor(x => x.AddProjectionWithServices<GreetedProjection>(
            ProjectionLifecycle.Async, ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(id, new PersonNamed("Frank"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using (var query = store.QuerySession())
        {
            (await query.LoadAsync<Greeted>(id, TestContext.Current.CancellationToken))
                .ShouldNotBeNull().Greeting.ShouldBe("Hello, Frank");
        }

        // A second batch, after the daemon has already run one — this is the shape that catches a
        // provider captured on the first page.
        var second = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(second, new PersonNamed("Grace"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using (var query = store.QuerySession())
        {
            (await query.LoadAsync<Greeted>(second, TestContext.Current.CancellationToken))
                .ShouldNotBeNull().Greeting.ShouldBe("Hello, Grace");
        }

        await daemon.StopAllAsync();
    }

    // ---------- subscriptions ----------

    [Fact]
    public async Task a_scoped_subscription_runs_with_its_injected_service()
    {
        RecordingSubscription.Seen.Clear();

        await using var provider = ProviderFor(x =>
            x.AddSubscriptionWithServices<RecordingSubscription>(ServiceLifetime.Scoped));

        var store = await ReadyStoreAsync(provider);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Greeted>(Guid.NewGuid(), new PersonNamed("Heidi"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();

        RecordingSubscription.Seen.ShouldContain("Hello, Heidi");
    }

    /// <summary>
    ///     The wrapper carries the inner subscription's own name and options rather than dropping them.
    /// </summary>
    /// <remarks>
    ///     marten#4318 over there: a subscription that set its batch size or its starting position in
    ///     its constructor had those silently discarded on the Scoped path, because it is the wrapper
    ///     the daemon reads them off, not the subscription.
    /// </remarks>
    [Fact]
    public async Task a_scoped_subscription_keeps_its_own_name_and_options()
    {
        await using var provider = ProviderFor(x =>
            x.AddSubscriptionWithServices<RecordingSubscription>(ServiceLifetime.Scoped));

        var store = provider.GetRequiredService<DocumentStore>();

        var source = store.Options.Projections.AllShards()
            .Single(x => x.Name.Name == "recorder");

        source.Options.BatchSize.ShouldBe(37);
    }
}

#region Projections, subscriptions and the services they take

/// <summary>
///     Counts the IoC scopes a container-scoped registration opens, and the ones it closes.
/// </summary>
/// <remarks>
///     Static because the thing being measured is the wrapper's behaviour over the life of a store, and
///     the wrapper is handed the root provider — there is no per-test instance for it to be given.
///     <c>InitializeAsync</c> resets it, and the suite's facts do not run concurrently within a class.
/// </remarks>
public static class ScopeLedger
{
    private static int _created;
    private static int _disposed;

    public static int Created => Volatile.Read(ref _created);
    public static int Disposed => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Volatile.Write(ref _created, 0);
        Volatile.Write(ref _disposed, 0);
    }

    public static void Opened() => Interlocked.Increment(ref _created);
    public static void Closed() => Interlocked.Increment(ref _disposed);
}

/// <summary>
///     The injected service. Scoped and disposable, so it records both halves of a scope's life.
/// </summary>
public class GreetingSource : IDisposable
{
    public GreetingSource() => ScopeLedger.Opened();

    public string Greet(string name) => $"Hello, {name}";

    public void Dispose() => ScopeLedger.Closed();
}

public record PersonNamed(string Name);

public class Greeted
{
    public Guid Id { get; set; }
    public string Greeting { get; set; } = string.Empty;
}

/// <summary>
///     A single stream projection that cannot be constructed without the container.
/// </summary>
public partial class GreetedProjection : SingleStreamProjection<Greeted, Guid>
{
    private readonly GreetingSource _greetings;

    public GreetedProjection(GreetingSource greetings) => _greetings = greetings;

    public Greeted Create(IEvent<PersonNamed> e) =>
        new() { Id = e.StreamId, Greeting = _greetings.Greet(e.Data.Name) };
}

/// <summary>
///     The same shape with a different greeting, so the Singleton fact reads a value only this type
///     produces.
/// </summary>
public partial class SingletonGreetedProjection : SingleStreamProjection<Greeted, Guid>
{
    public Greeted Create(IEvent<PersonNamed> e) =>
        new() { Id = e.StreamId, Greeting = $"Hi, {e.Data.Name}" };
}

public class GreetingLogEntry
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public partial class GreetingLogProjection : EventProjection
{
    private readonly GreetingSource _greetings;

    public GreetingLogProjection(GreetingSource greetings) => _greetings = greetings;

    public GreetingLogEntry Create(IEvent<PersonNamed> e) =>
        new() { Id = e.Id, Text = _greetings.Greet(e.Data.Name) };
}

public class GreetingRollup
{
    public static readonly Guid OnlyId = new("a0f6d4c1-7b2e-4f0a-9c8d-5e1b3a2f6c47");

    public Guid Id { get; set; }
    public int Count { get; set; }
    public string Last { get; set; } = string.Empty;
}

public partial class GreetingRollupProjection : MultiStreamProjection<GreetingRollup, Guid>
{
    private readonly GreetingSource _greetings;

    public GreetingRollupProjection(GreetingSource greetings)
    {
        _greetings = greetings;
        Identity<PersonNamed>(_ => GreetingRollup.OnlyId);
    }

    public void Apply(PersonNamed e, GreetingRollup rollup)
    {
        rollup.Count++;
        rollup.Last = _greetings.Greet(e.Name);
    }
}

/// <summary>
///     A subscription that needs the container, and sets its own options in its constructor.
/// </summary>
public class RecordingSubscription : SubscriptionBase
{
    public static readonly List<string> Seen = [];

    private readonly GreetingSource _greetings;

    public RecordingSubscription(GreetingSource greetings)
    {
        _greetings = greetings;
        Name = "recorder";
        Options.BatchSize = 37;
    }

    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
    {
        foreach (var e in page.Events.Select(x => x.Data).OfType<PersonNamed>())
        {
            lock (Seen)
            {
                Seen.Add(_greetings.Greet(e.Name));
            }
        }

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

#endregion

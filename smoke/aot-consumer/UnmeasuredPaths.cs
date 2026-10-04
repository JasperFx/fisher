using System.Text.Json.Serialization;
using Fisher;
using Fisher.Events.Messaging;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// fisher#430. The paths docs/configuration/native-aot.md listed as never measured in a native image:
// the hosted daemon, a subscription, projection side effects (a published message and a raised event),
// raw SQL, and a second store registered with AddFisherStore<T>. Each section uses a store of its own
// on a file of its own, so a failure here names the path rather than tangling with Program.cs.
internal static class UnmeasuredPaths
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    public static async Task RunAsync()
    {
        await HostedDaemonAsync();
        await AdvancedSqlAsync();
        await SecondStoreAsync();
    }

    // ---- the hosted daemon, a subscription, a published message and a raised event ----

    private static async Task HostedDaemonAsync()
    {
        var path = TempFile("hosted");
        var outbox = new SmokeOutbox();
        var subscription = new CountingSubscription();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddFisher(options =>
            {
                options.Connection($"Data Source={path}");
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.ConfigureSerialization(configure: json => json.TypeInfoResolver = UnmeasuredJson.Default);

                options.Events.MessageOutbox = outbox;
                options.Projections.Add(new HarbourProjection(), ProjectionLifecycle.Async);
                options.Projections.Subscribe(subscription);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            var harbour = Guid.NewGuid();

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<Harbour>(harbour, new HarbourOpened("Stromness"));
                await session.SaveChangesAsync();
            }

            await subscription.Seen.Task.WaitAsync(Wait);
            await outbox.Published.Task.WaitAsync(Wait);

            // The raised event lands on the harbour's own stream, after the one the test appended.
            var deadline = DateTime.UtcNow + Wait;
            while (true)
            {
                await using var query = store.QuerySession();
                var events = await query.Events.FetchStreamAsync(harbour);
                if (events.Any(x => x.Data is HarbourInspected)) break;

                Expect(DateTime.UtcNow < deadline, "a projection raised an event under the hosted daemon");
                await Task.Delay(100);
            }

            Expect(outbox.Published.Task.Result is HarbourAnnounced { Name: "Stromness" },
                "a projection published a message under the hosted daemon");
        }
        finally
        {
            await host.StopAsync();
            Delete(path);
        }
    }

    // ---- raw SQL ----

    private static async Task AdvancedSqlAsync()
    {
        var path = TempFile("sql");
        await using var store = DocumentStore.For(options =>
        {
            options.Connection($"Data Source={path}");
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(configure: json => json.TypeInfoResolver = UnmeasuredJson.Default);
            options.Schema.For<Berth>();
        });

        try
        {
            var berth = new Berth { Id = Guid.NewGuid(), Name = "North Quay" };

            await using (var session = store.LightweightSession())
            {
                session.QueueSqlCommand("create table port (code text primary key, name text)");
                session.QueueSqlCommand("insert into port values (?, ?)", "SX", "Stromness");
                session.Store(berth);
                await session.SaveChangesAsync();
            }

            await using var query = store.QuerySession();

            var names = await query.AdvancedSql.QueryAsync<string>(
                "select name from port where code = ?", CancellationToken.None, "SX");
            Expect(names.SingleOrDefault() == "Stromness", "AdvancedSql reads a scalar");

            var ids = await query.AdvancedSql.QueryAsync<Guid>("select id from fi_doc_berth", CancellationToken.None);
            Expect(ids.SingleOrDefault() == berth.Id, "AdvancedSql reads a Guid");

            var columns = string.Join(", ", query.AdvancedSql.SelectFieldsFor<Berth>());
            var berths = await query.AdvancedSql.QueryAsync<Berth>($"select {columns} from fi_doc_berth", CancellationToken.None);
            Expect(berths.SingleOrDefault()?.Name == "North Quay", "AdvancedSql materializes a document");
        }
        finally
        {
            Delete(path);
        }
    }

    // ---- a second store ----

    private static async Task SecondStoreAsync()
    {
        var primary = TempFile("primary");
        var archive = TempFile("archive");

        var services = new ServiceCollection();
        services.AddFisher(options =>
        {
            options.Connection($"Data Source={primary}");
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(configure: json => json.TypeInfoResolver = UnmeasuredJson.Default);
        });
        // A declared class rather than the one-argument overload's DispatchProxy, which cannot be created
        // in a native image.
        services.AddFisherStore<IArchiveStore, ArchiveStore>(options =>
        {
            options.Connection($"Data Source={archive}");
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(configure: json => json.TypeInfoResolver = UnmeasuredJson.Default);
        });

        await using var provider = services.BuildServiceProvider();

        try
        {
            var store = provider.GetRequiredService<IArchiveStore>();
            var berth = new Berth { Id = Guid.NewGuid(), Name = "Archived" };

            await using (var session = store.LightweightSession())
            {
                session.Store(berth);
                await session.SaveChangesAsync();
            }

            await using var query = store.QuerySession();
            Expect((await query.LoadAsync<Berth>(berth.Id))?.Name == "Archived",
                "a second store registered with AddFisherStore<T> round-trips a document");

            // The one-argument overload's DispatchProxy cannot exist here, and is refused by name.
            var proxied = new ServiceCollection();
            proxied.AddFisherStore<IProxiedStore>(options => options.Connection($"Data Source={archive}"));
            await using var proxiedProvider = proxied.BuildServiceProvider();
            try
            {
                proxiedProvider.GetRequiredService<IProxiedStore>();
                throw new Exception("Expected: the DispatchProxy registration is refused in a native image");
            }
            catch (NotSupportedException e) when (e.Message.Contains("AddFisherStore<IProxiedStore, ProxiedStore>"))
            {
            }
        }
        finally
        {
            Delete(primary);
            Delete(archive);
        }
    }

    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"fisher_aot_{name}_{Guid.NewGuid():n}.db");

    private static void Delete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // A leftover temp file is not the smoke's failure to report.
            }
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition) throw new Exception($"Expected: {what}");
    }
}

public interface IArchiveStore : IDocumentStore;

public sealed class ArchiveStore(StoreOptions options) : DocumentStore(options), IArchiveStore;

public interface IProxiedStore : IDocumentStore;

public class Berth
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public record HarbourOpened(string Name);

public record HarbourInspected(string Name);

public record HarbourAnnounced(string Name);

public class Harbour
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Inspected { get; set; }

    public static Harbour Create(HarbourOpened opened) => new() { Name = opened.Name };

    public void Apply(HarbourInspected _) => Inspected = true;
}

// Publishes a message and raises an event from the aggregation pipeline's side-effect hook, which is
// what the daemon's AggregationRunner drains into the batch.
public class HarbourProjection : Fisher.Projections.SingleStreamProjection<Harbour, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentSession operations, IEventSlice<Harbour> slice)
    {
        if (slice.Events().Any(x => x.Data is HarbourInspected))
        {
            return ValueTask.CompletedTask;
        }

        slice.PublishMessage(new HarbourAnnounced(slice.Snapshot?.Name ?? "unknown"));
        slice.AppendEvent(new HarbourInspected(slice.Snapshot?.Name ?? "unknown"));
        return ValueTask.CompletedTask;
    }
}

public class CountingSubscription : SubscriptionBase
{
    public TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
    {
        if (page.Events.Any(x => x.Data is HarbourOpened))
        {
            Seen.TrySetResult();
        }

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

public class SmokeOutbox : IMessageOutbox
{
    public TaskCompletionSource<object> Published { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
        => new(new Batch(this));

    private sealed class Batch(SmokeOutbox outbox) : IMessageBatch
    {
        private readonly List<object> _messages = [];

        public ValueTask PublishAsync<T>(T message, string tenantId)
        {
            lock (_messages)
            {
                _messages.Add(message!);
            }

            return ValueTask.CompletedTask;
        }

        public Task BeforeCommitAsync(CancellationToken token) => Task.CompletedTask;

        public Task AfterCommitAsync(CancellationToken token)
        {
            lock (_messages)
            {
                if (_messages.FirstOrDefault() is { } first) outbox.Published.TrySetResult(first);
            }

            return Task.CompletedTask;
        }
    }
}

[JsonSerializable(typeof(Berth))]
[JsonSerializable(typeof(Harbour))]
[JsonSerializable(typeof(HarbourOpened))]
[JsonSerializable(typeof(HarbourInspected))]
[JsonSerializable(typeof(HarbourAnnounced))]
internal partial class UnmeasuredJson : JsonSerializerContext;

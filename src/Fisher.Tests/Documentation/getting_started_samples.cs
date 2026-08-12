using Fisher.Linq;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/getting-started.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds.
 */

#region sample_getting_started_document_type
public class User
{
    public Guid Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public bool Internal { get; set; }
}
#endregion

#region sample_getting_started_events
public record OrderPlaced(string Customer, decimal Total);

public record OrderShipped(DateTimeOffset ShippedAt);
#endregion

#region sample_getting_started_aggregate
public class Order
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
    public bool Shipped { get; set; }

    public void Apply(OrderPlaced e)
    {
        Customer = e.Customer;
        Total = e.Total;
    }

    public void Apply(OrderShipped e) => Shipped = true;
}
#endregion

public class getting_started_samples
{
    private const string ConnectionString = "Data Source=app.db";

    public static void registering_fisher(IServiceCollection services)
    {
        #region sample_getting_started_add_fisher
        services.AddFisher(options =>
            {
                // Any Microsoft.Data.Sqlite connection string. This one is a file beside the
                // application.
                options.Connection("Data Source=app.db");

                // SQLite has no schemas, so this folds into the table *prefix* instead:
                // "main" gives fi_events, anything else gives <name>_fi_events.
                options.DatabaseSchemaName = "main";
            })
            // Run the Weasel migration at startup so the tables exist before the first session.
            .ApplyAllDatabaseChangesOnStartup();
        #endregion
    }

    public static void registering_fisher_with_the_daemon(IServiceCollection services)
    {
        #region sample_getting_started_add_daemon
        services.AddFisher(options =>
            {
                options.Connection("Data Source=app.db");
                options.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);
        #endregion
    }

    public static async Task working_with_documents(IDocumentSession session, CancellationToken token)
    {
        #region sample_getting_started_store_a_document
        var user = new User { FirstName = "Jane", LastName = "Doe", Internal = true };

        session.Store(user);
        await session.SaveChangesAsync(token);
        #endregion

        #region sample_getting_started_query_documents
        var internalUsers = await session.Query<User>()
            .Where(x => x.Internal)
            .OrderBy(x => x.LastName)
            .ToListAsync(token);
        #endregion

        #region sample_getting_started_load_by_id
        var loaded = await session.LoadAsync<User>(user.Id, token);
        #endregion

        _ = internalUsers;
        _ = loaded;
    }

    public static async Task working_with_events(IDocumentStore store, CancellationToken token)
    {
        #region sample_getting_started_events_round_trip
        await using var session = store.LightweightSession();

        // StartStream hands back a StreamAction; its Id is the stream's identity.
        var stream = session.Events.StartStream<Order>(
            new OrderPlaced("Acme Corp", 199.95m),
            new OrderShipped(DateTimeOffset.UtcNow));

        await session.SaveChangesAsync(token);

        var order = await session.Events.AggregateStreamAsync<Order>(stream.Id, token: token);
        #endregion

        _ = order;
    }

    public static async Task standalone_store(CancellationToken token)
    {
        #region sample_getting_started_standalone_store
        await using var store = DocumentStore.For("Data Source=app.db");
        #endregion

        #region sample_getting_started_standalone_store_configured
        await using var configured = DocumentStore.For(opts =>
        {
            opts.Connection("Data Source=app.db");
            opts.DatabaseSchemaName = "reporting";
            opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        });

        await configured.ApplyAllConfiguredChangesToDatabaseAsync(token);
        #endregion
    }

    // Referenced so the connection string constant is not an unused field in a sample-only class.
    public static string DefaultConnectionString => ConnectionString;
}

using JasperFx;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#279 — how a Fisher store identifies itself to anything outside it:
///     <see cref="IEventStore.Subject" />, <see cref="IEventStore.Identity" />, and the
///     <see cref="StoreOptions.StoreName" /> both are built from.
/// </summary>
/// <remarks>
///     <para>
///         <b>The property was documented as making several stores distinguishable and reached neither
///         member.</b> The subject was the database uri and the identity was built from
///         <see cref="StoreOptions.DatabaseSchemaName" />, so the one knob a host had for telling its
///         stores apart did nothing for the two surfaces consumers actually key on.
///     </para>
///     <para>
///         That is not cosmetic. CritterWatch resolves every explorer read through
///         <c>store.Subject.ToString()</c> and builds its shard progression id from
///         <c>(serviceName, storeUri, databaseIdentifier, tenantId, shardName)</c>, so two stores
///         answering the same subject share a progression id and clobber each other's recorded
///         progress. Polecat hit exactly this and fixed it in polecat#320; the comment there names
///         CritterWatch's poller as the thing that broke.
///     </para>
///     <para>
///         Every test below fails against the previous behaviour except the two marked as regression
///         guards, which cover the paths that were already right and must stay right.
///     </para>
/// </remarks>
public class store_identity
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore StoreFor(string connectionString, string? storeName = null,
        string? schemaName = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = connectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            if (storeName is not null) options.StoreName = storeName;
            if (schemaName is not null) options.DatabaseSchemaName = schemaName;
        });

    // ---- Subject ----

    /// <summary>
    ///     The headline regression: two logical stores in ONE file are the layout
    ///     <c>AddFisherStore&lt;T&gt;</c> exists to support, and they used to be indistinguishable.
    /// </summary>
    /// <remarks>
    ///     Both stores really do share a file here, isolated by table prefix, which is why they carry
    ///     different schema names — fisher#46 refuses two stores over one file on one schema. The old
    ///     subject was the database uri, so it was identical for both no matter what else differed.
    /// </remarks>
    [Fact]
    public async Task two_stores_over_one_file_do_not_share_a_subject()
    {
        using var database = TemporaryDatabase.Create("identity-shared");

        await using var ledgers = StoreFor(database.ConnectionString, "Ledgers", "ledgers");
        await using var archive = StoreFor(database.ConnectionString, "Archive", "archive");

        var first = ((IEventStore)ledgers).Subject;
        var second = ((IEventStore)archive).Subject;

        first.ShouldNotBe(second);
        first.ShouldBe(new Uri("fisher://ledgers"));
        second.ShouldBe(new Uri("fisher://archive"));
    }

    /// <summary>
    ///     The subject identifies the store, not the file underneath it — so it does not move when the
    ///     database does, and two stores on two files with one name still collide, which is correct.
    /// </summary>
    [Fact]
    public async Task the_subject_names_the_store_rather_than_the_database()
    {
        using var first = TemporaryDatabase.Create("identity-a");
        using var second = TemporaryDatabase.Create("identity-b");

        await using var one = StoreFor(first.ConnectionString, "Ledgers");
        await using var two = StoreFor(second.ConnectionString, "Ledgers");

        ((IEventStore)one).Subject.ShouldBe(((IEventStore)two).Subject);
        ((IEventStore)one).Subject.Scheme.ShouldBe("fisher");
    }

    /// <summary>
    ///     An unnamed store keeps the default, which is what the three stores agree on:
    ///     <c>marten://main</c>, <c>polecat://main</c>, <c>fisher://main</c>.
    /// </summary>
    [Fact]
    public async Task an_unnamed_store_is_main()
    {
        using var database = TemporaryDatabase.Create("identity-default");
        await using var store = StoreFor(database.ConnectionString);

        ((IEventStore)store).Subject.ShouldBe(new Uri("fisher://main"));
    }

    /// <summary>
    ///     A store name is user-supplied text and a uri host is not. Verified against .NET 10 before
    ///     this was written: <c>new Uri("fisher://my store")</c> throws <c>UriFormatException</c> and
    ///     <c>new Uri("fisher://a/b")</c> silently parses the tail as a PATH — so a naive concatenation
    ///     would turn naming a store into a crash at construction.
    /// </summary>
    /// <remarks>
    ///     The generic case is not hypothetical here: an ancillary store takes its name from its marker
    ///     type's <c>Name</c>, so a CLOSED GENERIC marker arrives carrying a backtick and arity. Marten
    ///     met the same thing in marten#5039.
    /// </remarks>
    [Theory]
    [InlineData("My Store", "fisher://my-store")]
    [InlineData("Orders/Archive", "fisher://orders-archive")]
    [InlineData("IStore`1", "fisher://istore-1")]
    [InlineData("Orders_v2", "fisher://orders_v2")]
    public async Task a_store_name_a_uri_host_cannot_carry_is_folded_rather_than_thrown_on(
        string name, string expected)
    {
        using var database = TemporaryDatabase.Create("identity-exotic");
        await using var store = StoreFor(database.ConnectionString, name);

        ((IEventStore)store).Subject.ShouldBe(new Uri(expected));
    }

    /// <summary>
    ///     Identity is folded the same way as the subject, so the two cannot disagree about a name the
    ///     uri had to change.
    /// </summary>
    [Fact]
    public async Task the_identity_is_folded_the_same_way_as_the_subject()
    {
        using var database = TemporaryDatabase.Create("identity-fold");
        await using var store = StoreFor(database.ConnectionString, "My Store");

        ((IEventStore)store).Identity.Name.ShouldBe("my-store");
        ((IEventStore)store).Subject.Host.ShouldBe("my-store");
    }

    // ---- Identity ----

    /// <summary>
    ///     Two stores on their own files, both on the default schema, which is the ordinary shape of a
    ///     host with a primary and an ancillary store. The identity used to be the schema name, so both
    ///     answered <c>main:fisher</c>.
    /// </summary>
    /// <remarks>
    ///     Identity feeds <c>EventStoreUsage.PopulateAgentUris</c>, so a collision here is two stores'
    ///     daemon agents answering to one address.
    /// </remarks>
    [Fact]
    public async Task two_stores_on_their_own_files_do_not_share_an_identity()
    {
        using var first = TemporaryDatabase.Create("identity-c");
        using var second = TemporaryDatabase.Create("identity-d");

        await using var primary = StoreFor(first.ConnectionString);
        await using var archive = StoreFor(second.ConnectionString, "Archive");

        var one = ((IEventStore)primary).Identity;
        var two = ((IEventStore)archive).Identity;

        one.ShouldNotBe(two);
        one.Name.ShouldBe("main");
        two.Name.ShouldBe("archive");
        two.Type.ShouldBe("fisher");
    }

    /// <summary>
    ///     The identity no longer moves when the schema does, which is what made it the wrong field to
    ///     read: a store's schema name is an isolation detail, not its name.
    /// </summary>
    [Fact]
    public async Task the_identity_does_not_follow_the_schema_name()
    {
        using var database = TemporaryDatabase.Create("identity-e");

        await using var store = StoreFor(database.ConnectionString, "Ledgers", "some_other_schema");

        ((IEventStore)store).Identity.Name.ShouldBe("ledgers");
    }

    // ---- the ancillary default ----

    /// <summary>
    ///     The gap this issue found by reading: the factory overload seeded nothing, so every store
    ///     registered through it stayed on the default and was indistinguishable from the primary in
    ///     traces and in every OpenTelemetry measurement.
    /// </summary>
    [Fact]
    public async Task an_ancillary_store_registered_by_factory_is_named_after_its_marker()
    {
        using var database = TemporaryDatabase.Create("identity-factory");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisherStore<IFactoryStore>(_ => new StoreOptions
            {
                ConnectionString = database.ConnectionString,
                AutoCreateSchemaObjects = AutoCreate.None
            }))
            .StartAsync(Token);

        host.Services.GetRequiredService<IFactoryStore>()
            .Options.StoreName.ShouldBe(nameof(IFactoryStore));
    }

    /// <summary>
    ///     Regression guard — the action overload already seeded the name, and must keep doing so.
    /// </summary>
    [Fact]
    public async Task an_ancillary_store_registered_by_action_is_named_after_its_marker()
    {
        using var database = TemporaryDatabase.Create("identity-action");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisherStore<IActionStore>(options =>
            {
                options.ConnectionString = database.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.None;
            }))
            .StartAsync(Token);

        host.Services.GetRequiredService<IActionStore>()
            .Options.StoreName.ShouldBe(nameof(IActionStore));
    }

    /// <summary>
    ///     The default is applied BEFORE the <c>IConfigureFisher</c> chain, so a contribution can still
    ///     name the store. Same ordering the action overload's seed already had, and the same reasoning
    ///     polecat#207 records for setting it ahead of <c>IConfigurePolecat&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    ///     Moving the default after the chain passes every other test in this file and fails this one,
    ///     which is why it is here rather than left to the ordering being obvious.
    /// </remarks>
    [Fact]
    public async Task a_contribution_can_still_name_an_ancillary_store()
    {
        using var database = TemporaryDatabase.Create("identity-override");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisherStore<IOverriddenStore>(options =>
                {
                    options.ConnectionString = database.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.None;
                });

                services.ConfigureFisher<IOverriddenStore>(options => options.StoreName = "Chosen");
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IOverriddenStore>()
            .Options.StoreName.ShouldBe("Chosen");
    }

    /// <summary>
    ///     Regression guard — the primary store is not an ancillary one and keeps the default, so the
    ///     new rule cannot reach it. It is gated on the marker type being present rather than on the
    ///     name, which this is what pins.
    /// </summary>
    [Fact]
    public async Task the_primary_store_keeps_the_default_name()
    {
        using var database = TemporaryDatabase.Create("identity-primary");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisher(options =>
            {
                options.ConnectionString = database.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.None;
            }))
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.StoreName.ShouldBe(StoreOptions.DefaultStoreName);
    }
}

public interface IFactoryStore : IDocumentStore;

public interface IActionStore : IDocumentStore;

public interface IOverriddenStore : IDocumentStore;

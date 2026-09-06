using JasperFx;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core.CommandLine;
using Weasel.Core.Migrations;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#172 — the command-line seam. A Fisher store was invisible to every JasperFx and Weasel
///     CLI surface, because <c>AddFisher</c> registered no <see cref="ISystemPart" /> and no
///     <see cref="IDatabaseSource" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two registrations, not one, because the two families resolve different things.</b>
///         <c>resources setup / list / check</c> and <c>AddResourceSetupOnStartup()</c> go through
///         <see cref="ISystemPart" />; <c>db-apply</c> / <c>db-assert</c> / <c>db-patch</c> /
///         <c>db-dump</c> go through Weasel's <see cref="IDatabaseSource" />. Registering one leaves the
///         other family reporting an application with no databases in it — which is why several tests
///         here run the commands rather than asserting on the container.
///     </para>
///     <para>
///         Running them is the point. A registration test passes against a source that resolves,
///         enumerates nothing and reports success, and that is exactly the failure mode this closes:
///         <c>db-assert</c> answering "everything matches" about a store it never looked at.
///     </para>
/// </remarks>
[Collection(ConsoleWritingCollection.Name)]
public class command_line_integration : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cli");
    private readonly TemporaryDatabase _second = TemporaryDatabase.Create("cli-second");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        _second.Dispose();

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IHostBuilder Builder(bool applyOnStartup = false) =>
        Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            var expression = services.AddFisher(options =>
            {
                options.ConnectionString = _database.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Schema.For<CliTarget>();
            });

            if (applyOnStartup)
            {
                expression.ApplyAllDatabaseChangesOnStartup();
            }
        });

    // ---- the registrations themselves ----

    [Fact]
    public async Task add_fisher_registers_a_system_part_over_the_stores_databases()
    {
        using var host = await Builder().StartAsync(Token);

        var part = host.Services.GetServices<ISystemPart>().Single(x => x.SubjectUri.Scheme == "fisher");

        part.SubjectUri.ShouldBe(new Uri("fisher://store"));
        part.Title.ShouldBe("Fisher");

        var resources = await part.FindResources();
        var resource = resources.ShouldHaveSingleItem().ShouldBeOfType<DatabaseResource>();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        resource.Database.ShouldBeSameAs(store.Tenancy.Default);
    }

    [Fact]
    public async Task add_fisher_registers_a_weasel_database_source()
    {
        using var host = await Builder().StartAsync(Token);

        var source = host.Services.GetServices<IDatabaseSource>().ShouldHaveSingleItem();
        var databases = await source.BuildDatabases();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        databases.ShouldHaveSingleItem().ShouldBeSameAs(store.Tenancy.Default);
    }

    /// <remarks>
    ///     Under database-per-tenant the source has to hand back <em>every</em> file, or <c>db-apply</c>
    ///     migrates one tenant and reports success for the store.
    /// </remarks>
    [Fact]
    public async Task the_database_source_spans_every_tenant_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-cli-tenants-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .ConfigureServices(services => services.AddFisher(options =>
                {
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                    options.MultiTenantedDatabases(x => x.InDirectory(directory).AddTenants("one", "two", "*DEFAULT*"));
                    options.Schema.For<CliTarget>();
                })).StartAsync(Token);

            var source = host.Services.GetServices<IDatabaseSource>().ShouldHaveSingleItem();
            var databases = await source.BuildDatabases();

            databases.Select(x => x.Identifier).OrderBy(x => x, StringComparer.Ordinal)
                .ShouldBe(["*DEFAULT*", "one", "two"]);

            var usage = await source.DescribeDatabasesAsync(Token);
            usage.Databases.Count.ShouldBe(3);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    ///     An ancillary store is usually a second <em>file</em> here, so collapsing the two onto one
    ///     subject uri would hide one from <c>resources list</c> outright — a sharper consequence than
    ///     the same mistake has on either sibling, where a second store is a second schema.
    /// </remarks>
    [Fact]
    public async Task an_ancillary_store_contributes_its_own_part_and_database_source()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                });

                services.AddFisherStore<ICliOtherStore>(options =>
                {
                    options.ConnectionString = _second.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                });
            }).StartAsync(Token);

        var parts = host.Services.GetServices<ISystemPart>()
            .Where(x => x.SubjectUri.Scheme == "fisher")
            .ToArray();

        parts.Select(x => x.SubjectUri.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            .ShouldBe(["fisher://icliotherstore/", "fisher://store/"]);

        var ancillary = parts.Single(x => x.SubjectUri != FisherSystemPartUri);
        ancillary.Title.ShouldBe("Fisher ICliOtherStore");

        var ancillaryResource = (await ancillary.FindResources()).ShouldHaveSingleItem()
            .ShouldBeOfType<DatabaseResource>();

        var other = host.Services.GetRequiredService<ICliOtherStore>();
        ancillaryResource.Database.ShouldBeSameAs(other.Tenancy.Default);

        // And both files reach db-apply, which is the half polecat#501 was actually about.
        var sources = host.Services.GetServices<IDatabaseSource>().ToArray();
        sources.Length.ShouldBe(2);

        var all = new List<IDatabase>();
        foreach (var source in sources)
        {
            all.AddRange(await source.BuildDatabases());
        }

        all.Count.ShouldBe(2);
    }

    private static Uri FisherSystemPartUri => new("fisher://store");

    // ---- the commands, actually invoked ----

    [Fact]
    public async Task db_apply_creates_the_schema()
    {
        var exitCode = await Builder().RunJasperFxCommands(["db-apply"]);

        exitCode.ShouldBe(0);
        (await TableExistsAsync("fi_events")).ShouldBeTrue();
        (await TableExistsAsync("fi_doc_clitarget")).ShouldBeTrue();
    }

    /// <remarks>
    ///     The CI shape the whole issue was about: apply the schema in a release step, then have a later
    ///     step (or the next deploy) prove the deployed database still matches what the code configures.
    /// </remarks>
    [Fact]
    public async Task db_assert_passes_once_the_schema_has_been_applied_and_fails_before()
    {
        // Before anything is applied the assertion has to fail, or "matches" means nothing.
        (await Builder().RunJasperFxCommands(["db-assert"])).ShouldNotBe(0);

        (await Builder().RunJasperFxCommands(["db-apply"])).ShouldBe(0);

        (await Builder().RunJasperFxCommands(["db-assert"])).ShouldBe(0);
    }

    [Fact]
    public async Task db_patch_writes_the_outstanding_ddl_to_a_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "fisher-cli-patch-" + Guid.NewGuid().ToString("N") + ".sql");

        try
        {
            var exitCode = await Builder().RunJasperFxCommands(["db-patch", file]);

            exitCode.ShouldBe(0);
            File.Exists(file).ShouldBeTrue();
            (await File.ReadAllTextAsync(file, Token)).ShouldContain("fi_events");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task db_dump_writes_the_whole_creation_script()
    {
        var file = Path.Combine(Path.GetTempPath(), "fisher-cli-dump-" + Guid.NewGuid().ToString("N") + ".sql");

        try
        {
            var exitCode = await Builder().RunJasperFxCommands(["db-dump", file]);

            exitCode.ShouldBe(0);

            var sql = await File.ReadAllTextAsync(file, Token);
            sql.ShouldContain("fi_events");
            sql.ShouldContain("fi_doc_clitarget");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task describe_reports_the_store()
    {
        (await Builder().RunJasperFxCommands(["describe"])).ShouldBe(0);
    }

    /// <remarks>
    ///     The idiomatic route: no Fisher-specific call at all, just the host-level resource opt-in,
    ///     which finds Fisher's databases through <see cref="ISystemPart" />.
    /// </remarks>
    [Fact]
    public async Task add_resource_setup_on_startup_creates_the_schema()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                    options.Schema.For<CliTarget>();
                });

                services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        (await TableExistsAsync("fi_events")).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    // ---- AssertDatabaseMatchesConfigurationOnStartup ----

    [Fact]
    public async Task assert_on_startup_stops_the_host_against_an_unapplied_schema()
    {
        var builder = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.Schema.For<CliTarget>();
                })
                .AssertDatabaseMatchesConfigurationOnStartup());

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(Token);
        });
    }

    [Fact]
    public async Task assert_on_startup_is_happy_once_the_schema_has_been_applied()
    {
        await using (var store = DocumentStore.For(options =>
                     {
                         options.ConnectionString = _database.ConnectionString;
                         options.Schema.For<CliTarget>();
                     }))
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        using var host = await Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.Schema.For<CliTarget>();
                })
                .AssertDatabaseMatchesConfigurationOnStartup()).StartAsync(Token);

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     Both orders, because which call came first must not decide whether the contradiction is
    ///     reported.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void applying_and_asserting_on_startup_are_alternatives(bool assertFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var expression = services.AddFisher(options => options.ConnectionString = _database.ConnectionString);

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            if (assertFirst)
            {
                expression.AssertDatabaseMatchesConfigurationOnStartup().ApplyAllDatabaseChangesOnStartup();
            }
            else
            {
                expression.ApplyAllDatabaseChangesOnStartup().AssertDatabaseMatchesConfigurationOnStartup();
            }
        });

        ex.Message.ShouldContain("alternatives");
    }

    /// <remarks>
    ///     A seeder needs the tables to exist by the time it runs, and an assertion that passed is
    ///     exactly that claim — so it satisfies the ordering guard the same way applying does.
    /// </remarks>
    [Fact]
    public void seeding_may_follow_the_assertion_as_well_as_the_migration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Should.NotThrow(() => services.AddFisher(options => options.ConnectionString = _database.ConnectionString)
            .AssertDatabaseMatchesConfigurationOnStartup()
            .SeedInitialDataOnStartup());
    }

    [Fact]
    public async Task an_ancillary_store_can_assert_its_own_schema_on_startup()
    {
        var builder = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddFisherStore<ICliOtherStore>(options =>
                {
                    options.ConnectionString = _second.ConnectionString;
                    options.Schema.For<CliTarget>();
                })
                .AssertDatabaseMatchesConfigurationOnStartup());

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(Token);
        });
    }

    private async Task<bool> TableExistsAsync(string name)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", name);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token)) > 0;
    }
}

public interface ICliOtherStore : IDocumentStore;

public class CliTarget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

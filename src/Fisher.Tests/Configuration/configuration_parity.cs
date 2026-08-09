using Fisher.Attributes;
using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#39 — the declarative and store-wide halves of the configuration DSL: schema attributes,
///     <c>AddSubClassHierarchy()</c>, <c>StorePolicies</c> and <c>IInitialData</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The four configuration layers and their order are what most of this pins.</b> A policy,
///         then the JasperFx metadata interfaces, then the schema attributes, then
///         <c>Schema.For&lt;T&gt;()</c> — each overriding the one before, weakest first, because a
///         policy was written without knowing about the type and the DSL names it.
///     </para>
///     <para>
///         Partitioning is not here and never will be: SQLite has no table partitioning, and the
///         nearest thing carries none of the operational properties that make Polecat's worth having.
///         Said in <c>StorePolicies</c> so it is not rediscovered as a gap.
///     </para>
/// </remarks>
public class configuration_parity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("config-parity");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StoreFor(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    // ---- attributes ----

    /// <remarks>
    ///     The claim worth checking is not "an index exists" but "the same index the DSL would have
    ///     made", so this compares the two stores' rendered index SQL rather than asserting a name.
    /// </remarks>
    [Fact]
    public async Task the_index_attribute_produces_what_the_dsl_produces()
    {
        await using var attributed = StoreFor(options => options.Schema.For<Angler>());
        await attributed.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var fromAttribute = await IndexSqlAsync("fi_doc_angler");

        await using var other = TemporaryDatabase.Create("config-parity-dsl");
        await using var declared = DocumentStore.For(options =>
        {
            options.ConnectionString = other.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<PlainAngler>().Index(x => x.Licence).UniqueIndex(x => x.Email);
        });

        await declared.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var fromDsl = await IndexSqlAsync("fi_doc_plainangler", other.ConnectionString);

        // Same expressions, same uniqueness — only the table name differs.
        fromAttribute.Select(x => x.Replace("fi_doc_angler", "T"))
            .ShouldBe(fromDsl.Select(x => x.Replace("fi_doc_plainangler", "T")));
    }

    [Fact]
    public async Task the_unique_index_attribute_is_enforced()
    {
        await using var store = StoreFor(options => options.Schema.For<Angler>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Email = "frodo@shire", Licence = "A" });
            await session.SaveChangesAsync(Token);
        }

        await using var second = store.LightweightSession();
        second.Store(new Angler { Id = Guid.NewGuid(), Email = "frodo@shire", Licence = "B" });

        var ex = await Should.ThrowAsync<SqliteException>(async () => await second.SaveChangesAsync(Token));
        ex.Message.ShouldContain("UNIQUE constraint failed");
    }

    /// <remarks>
    ///     Members sharing an explicit name become one composite index, which is Polecat's rule and the
    ///     only reason <c>IndexName</c> exists on a per-member attribute.
    /// </remarks>
    [Fact]
    public async Task members_sharing_an_index_name_become_one_composite_index()
    {
        await using var store = StoreFor(options => options.Schema.For<Catch>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var indexes = await IndexSqlAsync("fi_doc_catch");
        var composite = indexes.Single(x => x.Contains("idx_water_and_species", StringComparison.Ordinal));

        composite.ShouldContain("$.Water");
        composite.ShouldContain("$.Species");
    }

    [Fact]
    public async Task the_duplicate_field_attribute_adds_the_column()
    {
        await using var store = StoreFor(options => options.Schema.For<Catch>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ColumnsAsync("fi_doc_catch")).ShouldContain("weight");
    }

    [Fact]
    public void the_hilo_attribute_configures_the_sequence()
    {
        using var store = StoreFor(options => options.Schema.For<Ledger>());

        var settings = store.Options.Schema.MappingFor(typeof(Ledger)).HiloSettings;

        settings.ShouldNotBeNull();
        settings!.MaxLo.ShouldBe(7);
        settings.SequenceName.ShouldBe("ledgers");
    }

    // ---- precedence ----

    /// <remarks>
    ///     All four layers on one setting, in one store, so the order is asserted rather than implied:
    ///     the policy sets an alias, the DSL overrides it, and the DSL wins.
    /// </remarks>
    [Fact]
    public void the_dsl_overrides_a_policy()
    {
        using var store = StoreFor(options =>
        {
            options.Policies.ForAllDocuments(mapping => mapping.Alias = "from_the_policy");
            options.Schema.For<PlainAngler>().DocumentAlias("from_the_dsl");
        });

        store.Options.Schema.MappingFor(typeof(PlainAngler)).Alias.ShouldBe("from_the_dsl");
    }

    /// <remarks>
    ///     A policy is applied to a type it was written without knowing about, which is exactly why it
    ///     is the weakest layer — but it still reaches a type nothing else configures.
    /// </remarks>
    [Fact]
    public void a_policy_reaches_a_type_nothing_else_configures()
    {
        using var store = StoreFor(options => options.Policies.AllDocumentsSoftDeleted());

        store.Options.Schema.MappingFor(typeof(PlainAngler)).DeleteStyle.ShouldBe(DeleteStyle.SoftDelete);
    }

    [Fact]
    public void a_policy_can_be_scoped_to_one_document_type()
    {
        using var store = StoreFor(options
            => options.Policies.ForDocument<PlainAngler>(mapping => mapping.UseOptimisticConcurrency = true));

        store.Options.Schema.MappingFor(typeof(PlainAngler)).UseOptimisticConcurrency.ShouldBeTrue();
        store.Options.Schema.MappingFor(typeof(Ledger)).UseOptimisticConcurrency.ShouldBeFalse();
    }

    [Fact]
    public async Task the_multi_tenanted_policy_reshapes_every_table()
    {
        await using var store = StoreFor(options =>
        {
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Schema.For<PlainAngler>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ColumnsAsync("fi_doc_plainangler")).ShouldContain("tenant_id");
    }

    // ---- AddSubClassHierarchy ----

    /// <remarks>
    ///     A three-level tree with an abstract intermediate: <c>Lure</c> is abstract and must not get an
    ///     alias, because nothing is ever stored as one and a discriminator names something a row can be
    ///     read back as.
    /// </remarks>
    [Fact]
    public async Task add_sub_class_hierarchy_registers_every_concrete_type()
    {
        await using var store = StoreFor(options => options.Schema.For<Tackle>().AddSubClassHierarchy());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var aliases = store.Options.Schema.MappingFor(typeof(Tackle)).SubClasses
            .Select(x => x.Alias).OrderBy(x => x, StringComparer.Ordinal).ToList();

        aliases.ShouldBe(["deepdiver", "shallowdiver", "spinnerbait"]);

        // And it works end to end, which registration alone would not prove.
        await using (var session = store.LightweightSession())
        {
            session.Store<Tackle>(new DeepDiver { Id = Guid.NewGuid(), Name = "Rapala", Metres = 6 });
            session.Store<Tackle>(new SpinnerBait { Id = Guid.NewGuid(), Name = "Mepps", BladeSize = 3 });
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession();
        (await query.Query<Tackle>().ToListAsync(Token)).Select(x => x.GetType().Name)
            .OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["DeepDiver", "SpinnerBait"]);
    }

    /// <remarks>
    ///     Ordering is by full name rather than by reflection order, because two sub-classes whose
    ///     default aliases collide have to fail the same way on every run —
    ///     <c>Assembly.GetTypes()</c> promises no order, and a collision that appeared on one machine
    ///     and not another would be the worst version of that error.
    /// </remarks>
    [Fact]
    public void add_sub_class_hierarchy_is_deterministic()
    {
        using var first = StoreFor(options => options.Schema.For<Tackle>().AddSubClassHierarchy());
        using var second = StoreFor(options => options.Schema.For<Tackle>().AddSubClassHierarchy());

        first.Options.Schema.MappingFor(typeof(Tackle)).SubClasses.Select(x => x.Alias)
            .ShouldBe(second.Options.Schema.MappingFor(typeof(Tackle)).SubClasses.Select(x => x.Alias));
    }

    [Fact]
    public void an_explicit_alias_survives_add_sub_class_hierarchy()
    {
        using var store = StoreFor(options => options.Schema.For<Tackle>()
            .AddSubClass<DeepDiver>("deep")
            .AddSubClassHierarchy());

        store.Options.Schema.MappingFor(typeof(Tackle)).SubClasses
            .Single(x => x.DocumentType == typeof(DeepDiver)).Alias.ShouldBe("deep");
    }

    // ---- initial data ----

    [Fact]
    public async Task initial_data_is_seeded_at_startup_after_the_schema()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                    {
                        options.ConnectionString = _database.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Schema.For<PlainAngler>();

                        options.InitialData.Add(async (store, token) =>
                        {
                            await using var session = store.LightweightSession();
                            session.Store(new PlainAngler { Id = SeedId, Email = "seeded@shire" });
                            await session.SaveChangesAsync(token);
                        });
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .SeedInitialDataOnStartup();
            })
            .StartAsync(Token);

        await using var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        (await session.LoadAsync<PlainAngler>(SeedId, Token))!.Email.ShouldBe("seeded@shire");

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     Hosted services start in registration order, so seeding before the schema is applied writes
    ///     to tables that do not exist. Refused by name rather than left to present as
    ///     <c>no such table</c> at startup, which would name the table and not the mistake.
    /// </remarks>
    [Fact]
    public void seeding_before_the_schema_is_applied_is_refused_by_name()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(()
            => services.AddFisher(options => options.ConnectionString = _database.ConnectionString)
                .SeedInitialDataOnStartup());

        ex.Message.ShouldContain("ApplyAllDatabaseChangesOnStartup");
    }

    private static readonly Guid SeedId = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    private async Task<List<string>> IndexSqlAsync(string table, string? connectionString = null)
    {
        await using var connection = new SqliteConnection(connectionString ?? _database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select sql from sqlite_master where type = 'index' and tbl_name = '{table}' and sql is not null "
            + "order by name";

        var sql = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            sql.Add(reader.GetString(0));
        }

        return sql;
    }

    private async Task<List<string>> ColumnsAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select name from pragma_table_xinfo('{table}')";

        var columns = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

public class Angler
{
    public Guid Id { get; set; }

    [UniqueIndex]
    public string Email { get; set; } = string.Empty;

    [Index]
    public string Licence { get; set; } = string.Empty;
}

public class PlainAngler
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Licence { get; set; } = string.Empty;
}

public class Catch
{
    public Guid Id { get; set; }

    [Index(IndexName = "idx_water_and_species")]
    public string Water { get; set; } = string.Empty;

    [Index(IndexName = "idx_water_and_species")]
    public string Species { get; set; } = string.Empty;

    [DuplicateField]
    public decimal Weight { get; set; }
}

[HiloSequence(MaxLo = 7, SequenceName = "ledgers")]
public class Ledger
{
    public long Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

public abstract class Tackle
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public abstract class Lure : Tackle
{
    public int Metres { get; set; }
}

public class DeepDiver : Lure
{
}

public class ShallowDiver : Lure
{
}

public class SpinnerBait : Tackle
{
    public int BladeSize { get; set; }
}

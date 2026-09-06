using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Core.Identity;
using Weasel.Core.Sequences;

namespace Fisher.Tests.Configuration;

/// <summary>
///     The <c>MartenRegistry</c> members Fisher's <c>DocumentMappingExpression</c> lacked — identity
///     strategies, index refinement and the missing attributes (fisher#218).
/// </summary>
/// <remarks>
///     Deliberately not everything Marten's registry carries. Partitioning, row-level security, GIN
///     indexes, full-text indexes and <c>PropertySearching</c> have no SQLite counterpart; the
///     <c>UniqueIndexType</c> / <c>TenancyScope</c> / <c>IsConcurrent</c> / sort-order knobs describe a
///     computed column and a Postgres index, where a Fisher index is an expression index over a
///     generated column that cannot drift. Those are recorded as decisions in the migration guide
///     rather than implemented as no-ops.
/// </remarks>
public class mapping_registry_parity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("registry-parity");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StoreFor(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    // ---- identity ----

    /// <summary>
    ///     <c>Identity(x =&gt; x.Member)</c> — the DSL form of JasperFx's <c>[Identity]</c>, for a type
    ///     you would rather not annotate.
    /// </summary>
    [Fact]
    public async Task the_identity_member_can_be_named_in_the_dsl()
    {
        await using var store = StoreFor(o => o.Schema.For<Boat>().Identity(x => x.Registration));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Schema.For<Boat>().Mapping.IdMember.Name.ShouldBe(nameof(Boat.Registration));
        store.Options.Schema.For<Boat>().Mapping.IdType.ShouldBe(typeof(string));

        await using var session = store.LightweightSession();
        session.Store(new Boat { Registration = "SF-1", Name = "Sea Fox" });
        await session.SaveChangesAsync(Token);

        (await session.LoadAsync<Boat>("SF-1"))!.Name.ShouldBe("Sea Fox");
    }

    [Fact]
    public async Task naming_an_identity_member_of_an_unsupported_type_is_refused()
    {
        await using var store = StoreFor(_ => { });

        var refusal = Should.Throw<ArgumentException>(
            () => store.Options.Schema.For<Boat>().Identity(x => x.Length));

        refusal.Message.ShouldContain("Length");
        refusal.Message.ShouldContain("Double");
    }

    /// <summary>
    ///     <c>IdStrategy</c> — Fisher's honest translation of Marten's <c>IdStrategy(IIdGeneration)</c>.
    ///     There is no code-generation contract here; a strategy is an ordinary object from the shared
    ///     Weasel identity runtime, so implementing one takes two members.
    /// </summary>
    [Fact]
    public async Task a_caller_supplied_identity_strategy_assigns_the_id()
    {
        await using var store = StoreFor(o =>
            o.Schema.For<Boat>().Identity(x => x.Registration).IdStrategy(new PrefixedKeys()));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        var boat = new Boat { Name = "Sea Fox" };
        session.Store(boat);
        await session.SaveChangesAsync(Token);

        boat.Registration.ShouldStartWith("boat-");
        (await session.LoadAsync<Boat>(boat.Registration))!.Name.ShouldBe("Sea Fox");
    }

    /// <summary>
    ///     ⚠️ A Guid strategy is wrapped rather than taken raw. The lowercase-canonical conversion lives
    ///     in the identity strategy, so replacing the strategy is precisely where it could be lost — and
    ///     losing it writes rows that can never be read back, silently and only for Guid-identified
    ///     types.
    /// </summary>
    [Fact]
    public async Task a_guid_strategy_still_stores_lowercase_canonical_text()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>().IdStrategy(new FixedGuids()));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        var one = new Catch { Species = "Pike" };
        session.Store(one);
        await session.SaveChangesAsync(Token);

        one.Id.ShouldBe(FixedGuids.TheId);

        var stored = await ReadStringsAsync("select id from fi_doc_catch");
        stored.ShouldHaveSingleItem().ShouldBe(FixedGuids.TheId.ToString());

        // The round trip is the fact that matters: an uppercase write reads back as nothing at all.
        (await session.LoadAsync<Catch>(FixedGuids.TheId))!.Species.ShouldBe("Pike");
    }

    [Fact]
    public async Task an_identity_strategy_for_the_wrong_id_type_is_refused()
    {
        await using var store = StoreFor(_ => { });

        Should.Throw<ArgumentException>(() => store.Options.Schema.For<Boat>()
                .Identity(x => x.Registration)
                .IdStrategy(new FixedGuids2()))
            .Message.ShouldContain("IIdentification<Boat, String>");
    }

    /// <summary>
    ///     <c>HiloSettings(settings)</c> — the method form of the settable property, so a block of
    ///     configuration reads the same as Marten's.
    /// </summary>
    [Fact]
    public async Task hilo_settings_can_be_set_through_the_dsl()
    {
        await using var store = StoreFor(o => o.Schema.For<Reading>()
            .HiloSettings(new HiloSettings { MaxLo = 3, SequenceName = "shared" }));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Schema.For<Reading>().Mapping.HiloSettings!.MaxLo.ShouldBe(3);

        await using var session = store.LightweightSession();
        var first = new Reading { Value = 1 };
        session.Store(first);
        await session.SaveChangesAsync(Token);

        first.Id.ShouldBeGreaterThan(0);
        (await ReadStringsAsync("select entity_name from fi_hilo")).ShouldContain("shared");
    }

    // ---- index refinement ----

    /// <summary>
    ///     A partial index, and the fact that makes it worth having: the planner reaches it. That is
    ///     only true because the predicate is built from the same parser and member factory a query
    ///     goes through — SQLite uses a partial index only when the query's <c>WHERE</c> implies the
    ///     index's, over the terms as written.
    /// </summary>
    [Fact]
    public async Task a_partial_index_is_created_and_the_planner_reaches_it()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>()
            .Index(x => x.Weight, name: "idx_catch_pike_weight", predicate: x => x.Species == "Pike"));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var sql = await IndexSqlAsync("idx_catch_pike_weight");
        sql.ShouldContain("WHERE");
        sql.ShouldContain("'Pike'");

        await using var session = store.LightweightSession();
        for (var i = 1; i <= 5; i++)
        {
            session.Store(new Catch
            {
                Id = Guid.NewGuid(), Weight = i, Species = i % 2 == 0 ? "Pike" : "Trout"
            });
        }

        await session.SaveChangesAsync(Token);

        var plan = await session.Query<Catch>()
            .Where(x => x.Species == "Pike" && x.Weight > 1)
            .ExplainAsync(Token);

        plan.ToString().ShouldContain("idx_catch_pike_weight");
    }

    /// <summary>
    ///     ⚠️ The predicate's value is a literal in the DDL, because DDL cannot carry parameters — so
    ///     it is escaped rather than concatenated. The same defence-in-depth fisher#162 added to the
    ///     JSON-path locator, in the one other place Fisher writes a value into SQL text.
    /// </summary>
    [Fact]
    public async Task a_quoted_value_in_an_index_predicate_is_escaped()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>()
            .Index(x => x.Weight, name: "idx_catch_quoted", predicate: x => x.Species == "O'Brien"));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await IndexSqlAsync("idx_catch_quoted")).ShouldContain("'O''Brien'");

        await using var session = store.LightweightSession();
        session.Store(new Catch { Id = Guid.NewGuid(), Weight = 4, Species = "O'Brien" });
        await session.SaveChangesAsync(Token);

        (await session.Query<Catch>().Where(x => x.Species == "O'Brien").CountAsync(Token))
            .ShouldBe(1);
    }

    /// <summary>
    ///     A value Fisher cannot render unambiguously is refused by name rather than reached for with
    ///     <c>ToString()</c> — the marten#4954 class, one place over.
    /// </summary>
    [Fact]
    public async Task an_unrenderable_index_predicate_value_is_refused_by_name()
    {
        await using var store = StoreFor(_ => { });

        var refusal = Should.Throw<BadLinqExpressionException>(() =>
            store.Options.Schema.For<Catch>()
                .Index(x => x.Weight, name: "idx_bad", predicate: x => x.Tag == new Tag("x")));

        refusal.Message.ShouldContain("Tag");
    }

    [Fact]
    public async Task a_unique_index_can_be_partial()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>()
            .UniqueIndex(x => x.Weight, name: "idx_catch_unique_pike",
                predicate: x => x.Species == "Pike"));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var sql = await IndexSqlAsync("idx_catch_unique_pike");
        sql.ShouldContain("UNIQUE");
        sql.ShouldContain("WHERE");

        await using var session = store.LightweightSession();
        session.Store(new Catch { Id = Guid.NewGuid(), Weight = 4, Species = "Pike" });
        session.Store(new Catch { Id = Guid.NewGuid(), Weight = 4, Species = "Trout" });
        await session.SaveChangesAsync(Token);

        // Two rows share the weight; only one is in the index, so the constraint is not violated.
        (await session.Query<Catch>().CountAsync(Token)).ShouldBe(2);
    }

    /// <summary>
    ///     The metadata-column indexes. Real columns rather than JSON expressions, so there is nothing
    ///     to resolve through the member factory.
    /// </summary>
    [Fact]
    public async Task the_metadata_columns_can_be_indexed()
    {
        await using var store = StoreFor(o =>
        {
            o.Schema.For<Catch>().IndexLastModified().IndexCreatedAt();
            o.Schema.For<Reading>().MultiTenanted().IndexTenantId();
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await IndexNamesAsync("fi_doc_catch")).ShouldContain("idx_fi_doc_catch_last_modified");
        (await IndexSqlAsync("idx_fi_doc_catch_last_modified")).ShouldContain("last_modified");

        // IndexCreatedAt enables the column as well: an index over a column that does not exist is
        // not a weaker version of this, it fails the migration.
        (await ColumnNamesAsync("fi_doc_catch")).ShouldContain("created_at");
        (await IndexNamesAsync("fi_doc_catch")).ShouldContain("idx_fi_doc_catch_created_at");

        (await IndexNamesAsync("fi_doc_reading")).ShouldContain("idx_fi_doc_reading_tenant_id");
    }

    [Fact]
    public async Task indexing_tenant_id_on_a_single_tenant_type_is_refused()
    {
        await using var store = StoreFor(_ => { });

        Should.Throw<InvalidOperationException>(() => store.Options.Schema.For<Catch>().IndexTenantId())
            .Message.ShouldContain("MultiTenanted()");
    }

    /// <summary>
    ///     <c>SoftDeletedWithIndex</c>, whose index is partial by design: every ordinary read carries
    ///     <c>is_deleted = 0</c>, so an index holding only the live rows is the size of the live set
    ///     rather than of the table's whole history.
    /// </summary>
    [Fact]
    public async Task soft_deleted_with_index_creates_a_partial_index_over_the_live_rows()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>().SoftDeletedWithIndex());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var sql = await IndexSqlAsync("idx_fi_doc_catch_is_deleted");
        sql.ShouldContain("is_deleted");
        sql.ShouldContain("WHERE is_deleted = 0");

        await using var session = store.LightweightSession();
        var id = Guid.NewGuid();
        session.Store(new Catch { Id = id, Weight = 3, Species = "Pike" });
        await session.SaveChangesAsync(Token);

        session.Delete<Catch>(id);
        await session.SaveChangesAsync(Token);

        (await session.Query<Catch>().CountAsync(Token)).ShouldBe(0);
        (await session.Query<Catch>().MaybeDeleted().CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     <c>IgnoreIndex</c> — an index added out of band that the schema comparison must leave alone.
    ///     Without it, <c>db-assert</c> reports it as surplus and <c>db-apply</c> drops it.
    /// </summary>
    [Fact]
    public async Task an_ignored_index_survives_the_migration()
    {
        await using (var first = StoreFor(o => o.Schema.For<Catch>()))
        {
            await first.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        await ExecuteAsync("create index idx_by_hand on fi_doc_catch (last_modified)");

        await using var store = StoreFor(o => o.Schema.For<Catch>().IgnoreIndex("idx_by_hand"));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await IndexNamesAsync("fi_doc_catch")).ShouldContain("idx_by_hand");
        await store.Database.AssertDatabaseMatchesConfigurationAsync();
    }

    /// <summary>
    ///     Ignoring a name Fisher itself declares is a collision, not an exemption, and Weasel refuses
    ///     it rather than resolving silently in whichever direction the ordering happened to give.
    /// </summary>
    [Fact]
    public async Task ignoring_an_index_fisher_declares_is_refused()
    {
        await using var store = StoreFor(o => o.Schema.For<Catch>()
            .Index(x => x.Species, name: "idx_species")
            .IgnoreIndex("idx_species"));

        await Should.ThrowAsync<ArgumentException>(() =>
            store.ApplyAllConfiguredChangesToDatabaseAsync(Token));
    }

    // ---- the missing attributes ----

    [Fact]
    public async Task the_declarative_alias_names_the_table()
    {
        await using var store = StoreFor(o => o.Schema.For<Aliased>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Schema.For<Aliased>().Mapping.Alias.ShouldBe("gear");
        (await TableNamesAsync()).ShouldContain("fi_doc_gear");
    }

    /// <summary>
    ///     The DSL still wins, being the layer that names the type in this store's own configuration —
    ///     the four-layer order fisher#39 established.
    /// </summary>
    [Fact]
    public async Task the_dsl_alias_outranks_the_attribute()
    {
        await using var store = StoreFor(o => o.Schema.For<Aliased>().DocumentAlias("tackle"));

        store.Options.Schema.For<Aliased>().Mapping.Alias.ShouldBe("tackle");
    }

    [Fact]
    public async Task the_declarative_multi_tenancy_adds_the_column()
    {
        await using var store = StoreFor(o => o.Schema.For<Scoped>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Schema.For<Scoped>().Mapping.TenancyStyle.ShouldBe(TenancyStyle.Conjoined);
        (await ColumnNamesAsync("fi_doc_scoped")).ShouldContain("tenant_id");
    }

    [Fact]
    public async Task the_declarative_optimistic_concurrency_adds_the_column()
    {
        await using var store = StoreFor(o => o.Schema.For<Guarded>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Schema.For<Guarded>().Mapping.UseOptimisticConcurrency.ShouldBeTrue();
        (await ColumnNamesAsync("fi_doc_guarded")).ShouldContain("guid_version");
    }

    /// <summary>
    ///     <c>[ForeignKey]</c> is enforced, which is worth asserting rather than assuming: enforcement
    ///     is per-connection through <c>PRAGMA foreign_keys</c>, and Weasel's default profile is what
    ///     turns it on for every connection Fisher opens.
    /// </summary>
    [Fact]
    public async Task the_declarative_foreign_key_is_enforced()
    {
        await using var store = StoreFor(o =>
        {
            o.Schema.For<Catch>();
            o.Schema.For<Child>();
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // The member is duplicated as a side effect, because a constraint needs a real column.
        (await ColumnNamesAsync("fi_doc_child")).ShouldContain("catch_id");

        await using var session = store.LightweightSession();
        session.Store(new Child { Id = Guid.NewGuid(), CatchId = Guid.NewGuid() });

        await Should.ThrowAsync<Exception>(() => session.SaveChangesAsync(Token));
    }

    // ---- helpers ----

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private Task<List<string>> IndexNamesAsync(string table)
        => ReadStringsAsync(
            $"select name from sqlite_master where type = 'index' and tbl_name = '{table}'");

    private Task<List<string>> TableNamesAsync()
        => ReadStringsAsync("select name from sqlite_master where type = 'table'");

    private Task<List<string>> ColumnNamesAsync(string table)
        => ReadStringsAsync($"select name from pragma_table_xinfo('{table}')");

    private async Task<string> IndexSqlAsync(string indexName)
    {
        var rows = await ReadStringsAsync(
            $"select sql from sqlite_master where type = 'index' and name = '{indexName}'");

        rows.ShouldNotBeEmpty();
        return rows[0];
    }

    private async Task<List<string>> ReadStringsAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            if (!await reader.IsDBNullAsync(0, Token))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    // ---- fixture types ----

    public record struct Tag(string Value);

    public class Catch
    {
        public Guid Id { get; set; }
        public int Weight { get; set; }
        public string Species { get; set; } = "";
        public Tag Tag { get; set; }
    }

    public class Child
    {
        public Guid Id { get; set; }

        [ForeignKey(typeof(Catch))]
        public Guid CatchId { get; set; }
    }

    public class Boat
    {
        public string Registration { get; set; } = "";
        public string Name { get; set; } = "";
        public double Length { get; set; }
    }

    public class Reading
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    [DocumentAlias("gear")]
    public class Aliased
    {
        public Guid Id { get; set; }
    }

    [MultiTenanted]
    public class Scoped
    {
        public Guid Id { get; set; }
    }

    [UseOptimisticConcurrency]
    public class Guarded
    {
        public Guid Id { get; set; }
    }

    /// <summary>A string identity strategy of the caller's own — two members, and that is the seam.</summary>
    private sealed class PrefixedKeys : IIdentification<Boat, string>
    {
        public string Identity(Boat document) => document.Registration;

        public string AssignIfMissing(Boat document, ISequenceSource sequences)
        {
            if (string.IsNullOrEmpty(document.Registration))
            {
                document.Registration = "boat-" + Guid.NewGuid().ToString("N")[..8];
            }

            return document.Registration;
        }
    }

    private sealed class FixedGuids : IIdentification<Catch, Guid>
    {
        internal static readonly Guid TheId = new("A1B2C3D4-0000-0000-0000-00000000BEEF");

        public Guid Identity(Catch document) => document.Id;

        public Guid AssignIfMissing(Catch document, ISequenceSource sequences)
        {
            if (document.Id == Guid.Empty)
            {
                document.Id = TheId;
            }

            return document.Id;
        }
    }

    private sealed class FixedGuids2 : IIdentification<Boat, Guid>
    {
        public Guid Identity(Boat document) => Guid.Empty;

        public Guid AssignIfMissing(Boat document, ISequenceSource sequences) => Guid.Empty;
    }
}

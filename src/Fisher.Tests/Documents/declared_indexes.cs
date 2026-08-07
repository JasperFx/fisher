using System.Linq.Expressions;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#16 — <c>Schema.For&lt;T&gt;().Index(...)</c>, as a SQLite expression index over the
///     member's own locator.
/// </summary>
/// <remarks>
///     The assertions that matter are the planner ones. An index over a JSON expression is only used
///     when the query's expression matches the index's textually, so an index built from a hand-written
///     <c>json_extract</c> instead of from <c>MemberFactory</c>'s <c>TypedLocator</c> would be created
///     without error, never used, and never wrong enough to notice. <c>EXPLAIN QUERY PLAN</c> is SQLite's
///     own answer to that, and is what these ask.
/// </remarks>
public class declared_indexes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("declared-indexes");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(options =>
        {
            options.Schema.For<Angler>()
                .Index(x => x.Name)
                .Index(x => x.Club.Town)
                .Index(x => x.JoinedAt)
                .UniqueIndex(x => x.Licence)
                .Index([x => x.Country, x => x.Name], name: "idx_angler_country_name");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore StoreFor(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    // ---- what gets created ----

    [Fact]
    public async Task an_index_is_created_for_each_declaration()
    {
        var indexes = await IndexNamesAsync("fi_doc_angler");

        indexes.ShouldContain("idx_fi_doc_angler_name");
        indexes.ShouldContain("idx_fi_doc_angler_club_town");
        indexes.ShouldContain("idx_fi_doc_angler_joined_at");
        indexes.ShouldContain("idx_fi_doc_angler_licence");
        indexes.ShouldContain("idx_angler_country_name");
    }

    /// <summary>
    ///     The divergence from Marten and Polecat: no column is materialised, so the table's shape is
    ///     exactly what it would be without the index.
    /// </summary>
    [Fact]
    public async Task indexing_a_member_adds_no_column()
    {
        var columns = await ColumnNamesAsync("fi_doc_angler");

        columns.ShouldNotContain("name");
        columns.ShouldNotContain("club_town");
        columns.ShouldNotContain("licence");

        // Only the columns Fisher owns.
        columns.ShouldContain("id");
        columns.ShouldContain("data");
    }

    [Fact]
    public async Task the_migration_creates_them_rather_than_the_first_write()
    {
        // Nothing written yet, and the indexes already exist — they go through Weasel's migration like
        // every other schema object, so AutoCreate.None is honoured for free.
        (await IndexNamesAsync("fi_doc_angler")).ShouldContain("idx_fi_doc_angler_name");
    }

    [Fact]
    public async Task applying_the_configuration_again_is_a_no_op()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await _store.Database.AssertDatabaseMatchesConfigurationAsync();
    }

    // ---- what the planner does with them ----

    /// <summary>
    ///     The whole point of the feature.
    /// </summary>
    [Fact]
    public async Task the_planner_uses_a_declared_index()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");

        var plan = await QueryPlanAsync(x => x.Name == "Isaak");

        plan.ShouldContain("USING INDEX");
        plan.ShouldContain("idx_fi_doc_angler_name");
    }

    [Fact]
    public async Task the_planner_uses_a_declared_index_on_a_nested_member()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");

        var plan = await QueryPlanAsync(x => x.Club.Town == "Ware");

        plan.ShouldContain("idx_fi_doc_angler_club_town");
    }

    /// <summary>
    ///     The case that proves the expression has to come from <c>TypedLocator</c> rather than being
    ///     spelled out. A timestamp's locator is fisher#1's <c>strftime</c> wrapper, so an index over the
    ///     bare <c>json_extract</c> would not serve a range predicate — which is the only kind a
    ///     timestamp index is for.
    /// </summary>
    [Fact]
    public async Task the_planner_uses_a_declared_index_for_a_timestamp_range()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1", new DateTimeOffset(1653, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var cutoff = new DateTimeOffset(1650, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var plan = await QueryPlanAsync(x => x.JoinedAt > cutoff);

        plan.ShouldContain("idx_fi_doc_angler_joined_at");
    }

    [Fact]
    public async Task an_unindexed_member_still_scans()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");

        var plan = await QueryPlanAsync(x => x.Rod == "Split cane");

        plan.ShouldContain("SCAN");
        plan.ShouldNotContain("USING INDEX");
    }

    /// <summary>
    ///     A composite index serves a predicate on its leading member, and not one on its trailing
    ///     member alone — ordinary B-tree behaviour, asserted so the column order in the declaration is
    ///     known to reach SQLite in the order given.
    /// </summary>
    [Fact]
    public async Task a_composite_index_is_ordered_as_declared()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");

        var leading = await QueryPlanAsync(x => x.Country == "GB");
        leading.ShouldContain("idx_angler_country_name");
    }

    // ---- uniqueness ----

    [Fact]
    public async Task a_unique_index_rejects_a_duplicate_value()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
            await StoreAnglerAsync("Charles", "Winchester", "GB", "L-1"));

        ex.Message.ShouldContain("UNIQUE constraint failed");
    }

    [Fact]
    public async Task a_unique_index_allows_distinct_values()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", "L-1");
        await StoreAnglerAsync("Charles", "Winchester", "GB", "L-2");

        await using var session = _store.LightweightSession();
        var all = await session.Query<Angler>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(2);
    }

    /// <summary>
    ///     <c>json_extract</c> yields SQL NULL for an absent key and SQLite treats NULLs in a unique
    ///     index as distinct, so a unique index constrains only the documents that have the member.
    ///     That is the behaviour on both siblings; pinned here because it is the kind of thing a reader
    ///     would otherwise assume the opposite of.
    /// </summary>
    [Fact]
    public async Task a_unique_index_does_not_constrain_documents_missing_the_member()
    {
        await StoreAnglerAsync("Isaak", "Ware", "GB", null);
        await StoreAnglerAsync("Charles", "Winchester", "GB", null);

        await using var session = _store.LightweightSession();
        var all = await session.Query<Angler>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(2);
    }

    // ---- registration ----

    [Fact]
    public void declaring_the_same_index_twice_is_idempotent()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        options.Schema.For<Angler>().Index(x => x.Name).Index(x => x.Name);

        options.Schema.For<Angler>().Mapping.Indexes.Count.ShouldBe(1);
    }

    [Fact]
    public void two_different_indexes_cannot_share_one_name()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };
        options.Schema.For<Angler>().Index(x => x.Name, name: "idx_shared");

        var ex = Should.Throw<InvalidOperationException>(
            () => options.Schema.For<Angler>().Index(x => x.Country, name: "idx_shared"));

        ex.Message.ShouldContain("idx_shared");
        ex.Message.ShouldContain("Name");
    }

    /// <summary>
    ///     An index over a member that is also duplicated lands on the generated column, because that is
    ///     what a query against it emits. Not special-cased — it falls out of reading the locator.
    /// </summary>
    [Fact]
    public async Task indexing_a_duplicated_member_indexes_the_column()
    {
        await using var store = StoreFor(options =>
            options.Schema.For<Gaff>()
                .Duplicate(x => x.Maker, index: false)
                .Index(x => x.Maker, name: "idx_gaff_maker"));

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var sql = await IndexSqlAsync("idx_gaff_maker");

        // The column name, not a json_extract expression.
        sql.ShouldContain("maker");
        sql.ShouldNotContain("json_extract");
    }

    // ---- helpers ----

    private async Task StoreAnglerAsync(string name, string town, string country, string? licence,
        DateTimeOffset? joinedAt = null)
    {
        await using var session = _store.LightweightSession();

        session.Store(new Angler
        {
            Id = Guid.NewGuid(),
            Name = name,
            Country = country,
            Licence = licence,
            Rod = "Split cane",
            JoinedAt = joinedAt ?? DateTimeOffset.UtcNow,
            Club = new Club { Town = town }
        });

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> QueryPlanAsync(Expression<Func<Angler, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Angler>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();

        builder.Append("explain query plan select data from fi_doc_angler where ");
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);

        var command = builder.Compile();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        command.Connection = connection;

        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plan.Add(reader.GetString(3));
        }

        return string.Join(" | ", plan);
    }

    private async Task<List<string>> IndexNamesAsync(string table)
        => await ReadStringsAsync(
            $"select name from sqlite_master where type = 'index' and tbl_name = '{table}'");

    private async Task<List<string>> ColumnNamesAsync(string table)
        => await ReadStringsAsync($"select name from pragma_table_xinfo('{table}')");

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
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, TestContext.Current.CancellationToken))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }
}

public class Angler
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? Licence { get; set; }
    public string Rod { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
    public Club Club { get; set; } = new();
}

public class Club
{
    public string Town { get; set; } = string.Empty;
}

public class Gaff
{
    public Guid Id { get; set; }
    public string Maker { get; set; } = string.Empty;
}

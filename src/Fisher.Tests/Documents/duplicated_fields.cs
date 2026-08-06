using System.Linq.Expressions;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#2 — a duplicated field is a SQLite <c>VIRTUAL</c> generated column plus an index, so the
///     tests that matter are about what the query planner does with it and about the column staying in
///     step with <c>data</c> without anything writing it.
/// </summary>
public class duplicated_fields : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("duplicated-fields");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(options =>
        {
            options.Schema.For<Catch>()
                .Duplicate(x => x.Species)
                .Duplicate(x => x.Weight)
                .Duplicate(x => x.LandedAt)
                .Duplicate(x => x.Water.Name)
                .Duplicate(x => x.Boat, index: false);
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

    [Fact]
    public async Task the_column_is_generated_and_holds_the_extracted_value()
    {
        await StoreCatchAsync("Pike", 12.5, "Loch Ness", "Nessie II");

        // Read outside Fisher: the column is never written, so its value is entirely SQLite's doing.
        var row = await SingleRowAsync("select species, weight, water_name, boat from fi_doc_catch");

        row["species"].ShouldBe("Pike");
        Convert.ToDouble(row["weight"]).ShouldBe(12.5);
        row["water_name"].ShouldBe("Loch Ness");
        row["boat"].ShouldBe("Nessie II");
    }

    /// <summary>
    ///     The whole point. A predicate Fisher translates against a duplicated member has to produce
    ///     SQL the planner serves from the index rather than by computing <c>json_extract</c> per row.
    /// </summary>
    [Fact]
    public async Task the_planner_uses_the_index_for_a_duplicated_member()
    {
        await StoreCatchAsync("Pike", 12.5, "Loch Ness", "Nessie II");

        var plan = await QueryPlanAsync(x => x.Species == "Pike");

        plan.ShouldContain("USING INDEX");
        plan.ShouldContain("idx_fi_doc_catch_species");
    }

    [Fact]
    public async Task an_unduplicated_member_still_scans()
    {
        await StoreCatchAsync("Pike", 12.5, "Loch Ness", "Nessie II");

        // Nothing is wrong with this — it is what every query did before fisher#2, and is the contrast
        // that makes the test above mean something.
        (await QueryPlanAsync(x => x.Bait == "Spinner")).ShouldContain("SCAN");
    }

    [Fact]
    public async Task the_translated_predicate_names_the_column_rather_than_the_json_path()
    {
        SqlFor(x => x.Species == "Pike").ShouldContain("species =");
        SqlFor(x => x.Species == "Pike").ShouldNotContain("json_extract");

        // A null test asks whether the member is present, which is a question about the JSON and not
        // about the column — and no index would serve it anyway.
        SqlFor(x => x.Bait == null).ShouldContain("json_extract");
    }

    [Fact]
    public async Task queries_return_the_same_rows_as_before()
    {
        await StoreCatchAsync("Pike", 12.5, "Loch Ness", "Nessie II");
        await StoreCatchAsync("Trout", 2.0, "Test", "Nessie II");

        await using var session = _store.LightweightSession();

        (await session.Query<Catch>().Where(x => x.Species == "Pike")
            .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Species).ShouldBe(["Pike"]);

        (await session.Query<Catch>().Where(x => x.Weight > 5)
            .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Species).ShouldBe(["Pike"]);

        (await session.Query<Catch>().Where(x => x.Water.Name == "Test")
            .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Species).ShouldBe(["Trout"]);
    }

    /// <summary>
    ///     The fisher#1 payoff. A duplicated timestamp's generated expression is the member's own
    ///     <c>strftime</c> locator, so the column holds the normalised UTC form — which is both what
    ///     makes it sortable as text and what makes the index usable for a range query.
    /// </summary>
    [Fact]
    public async Task a_duplicated_timestamp_holds_the_normalised_form_and_orders_by_instant()
    {
        // Written with offsets whose text order is the opposite of their instant order.
        await StoreCatchAsync("Later", 1, "A", "B", DateTimeOffset.Parse("2024-03-01T12:00:00-05:00"));
        await StoreCatchAsync("Earlier", 1, "C", "D", DateTimeOffset.Parse("2024-03-01T12:00:00+00:00"));

        var row = await SingleRowAsync(
            "select landed_at from fi_doc_catch where json_extract(data, '$.species') = 'Later'");

        // 12:00-05:00 is 17:00 UTC, rendered fixed-width to the millisecond.
        row["landed_at"].ShouldBe("2024-03-01T17:00:00.000");

        await using var session = _store.LightweightSession();

        (await session.Query<Catch>().OrderBy(x => x.LandedAt)
            .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Species).ShouldBe(["Earlier", "Later"]);

        (await QueryPlanAsync(x => x.LandedAt > DateTimeOffset.Parse("2024-03-01T13:00:00Z")))
            .ShouldContain("idx_fi_doc_catch_landed_at");
    }

    [Fact]
    public async Task an_index_is_created_unless_the_registration_declined_one()
    {
        var indexes = await IndexNamesAsync("fi_doc_catch");

        indexes.ShouldContain("idx_fi_doc_catch_species");
        indexes.ShouldContain("idx_fi_doc_catch_water_name");
        indexes.ShouldNotContain("idx_fi_doc_catch_boat");
    }

    /// <summary>
    ///     Fisher runs a migration on the first write of each document type per process, so a delta
    ///     that keeps re-adding the generated column would fail the second time with
    ///     <c>duplicate column name</c>. This is what the <c>pragma_table_xinfo</c> override in
    ///     <c>DocumentTable</c> exists for.
    /// </summary>
    [Fact]
    public async Task applying_the_configuration_again_is_a_no_op()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        // Nothing left to do — which is the assertion, because the migration machinery cannot see a
        // generated column through pragma_table_info and would otherwise report one as missing forever.
        await _store.Database.AssertDatabaseMatchesConfigurationAsync();
    }

    /// <summary>
    ///     A generated column needs no backfill: the value is derived from <c>data</c>, so every row
    ///     that already exists is correct the moment the column is added. A written duplicated column —
    ///     which is what Marten and Polecat have — would leave these rows null until something rewrote
    ///     them.
    /// </summary>
    [Fact]
    public async Task duplicating_a_member_of_a_type_that_already_has_rows_needs_no_backfill()
    {
        await using (var plain = StoreFor(_ => { }))
        {
            await plain.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

            await using var session = plain.LightweightSession();
            session.Store(new Bait { Id = Guid.NewGuid(), Colour = "Chartreuse" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var upgraded = StoreFor(options => options.Schema.For<Bait>().Duplicate(x => x.Colour));
        await upgraded.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        (await SingleRowAsync("select colour from fi_doc_bait"))["colour"].ShouldBe("Chartreuse");
    }

    [Fact]
    public void duplicating_into_a_column_fisher_owns_is_refused()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        var ex = Should.Throw<InvalidOperationException>(
            () => options.Schema.For<Catch>().Duplicate(x => x.Species, columnName: "data"));

        ex.Message.ShouldContain("data");
        ex.Message.ShouldContain("Fisher owns");
    }

    [Fact]
    public void two_members_cannot_share_one_column()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };
        options.Schema.For<Catch>().Duplicate(x => x.Species, columnName: "label");

        var ex = Should.Throw<InvalidOperationException>(
            () => options.Schema.For<Catch>().Duplicate(x => x.Bait, columnName: "label"));

        ex.Message.ShouldContain("Species");
        ex.Message.ShouldContain("label");
    }

    [Fact]
    public void duplicating_the_same_member_twice_is_idempotent()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        options.Schema.For<Catch>().Duplicate(x => x.Species).Duplicate(x => x.Species);

        options.Schema.For<Catch>().Mapping.DuplicatedFields.Count.ShouldBe(1);
    }

    /// <summary>
    ///     Duplicating changes where a value is read from, never what it means — so a member whose
    ///     stored form does not order still refuses to be ordered by.
    /// </summary>
    [Fact]
    public void a_duplicated_string_stored_enum_still_refuses_to_be_ordered()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };
        options.ConfigureSerialization(EnumStorage.AsString);
        options.Schema.For<Catch>().Duplicate(x => x.Method);

        var member = new MemberFactory(options, options.Schema.For<Catch>().Mapping)
            .ResolveMember((MemberExpression)((Expression<Func<Catch, Method>>)(x => x.Method)).Body);

        member.TypedLocator.ShouldBe("method");
        member.AllowsRangeComparison.ShouldBeFalse();
    }

    // ---- helpers ----

    private async Task StoreCatchAsync(string species, double weight, string water, string boat,
        DateTimeOffset? landedAt = null)
    {
        await using var session = _store.LightweightSession();

        session.Store(new Catch
        {
            Id = Guid.NewGuid(),
            Species = species,
            Weight = weight,
            Boat = boat,
            Bait = "Spinner",
            LandedAt = landedAt ?? DateTimeOffset.UtcNow,
            Water = new Water { Name = water }
        });

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The SQL Fisher's own LINQ translation produces for a predicate, which is what the planner
    ///     tests then hand to SQLite.
    /// </summary>
    private string SqlFor(Expression<Func<Catch, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Catch>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();

        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);

        return builder.Compile().CommandText;
    }

    /// <summary>
    ///     <c>EXPLAIN QUERY PLAN</c> over Fisher's translation of the predicate — SQLite's own answer
    ///     about whether the index is reachable, rather than an assertion about SQL text.
    /// </summary>
    private async Task<string> QueryPlanAsync(Expression<Func<Catch, bool>> predicate)
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Catch>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();

        builder.Append("explain query plan select data from fi_doc_catch where ");
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

    private async Task<Dictionary<string, object?>> SingleRowAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = await reader.IsDBNullAsync(i, TestContext.Current.CancellationToken)
                ? null
                : reader.GetValue(i);
        }

        return row;
    }

    private async Task<List<string>> IndexNamesAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select name from sqlite_master where type = 'index' and tbl_name = '{table}'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

public class Catch
{
    public Guid Id { get; set; }
    public string Species { get; set; } = string.Empty;
    public double Weight { get; set; }
    public string Boat { get; set; } = string.Empty;
    public string? Bait { get; set; }
    public DateTimeOffset LandedAt { get; set; }
    public Method Method { get; set; }
    public Water Water { get; set; } = new();
}

public class Water
{
    public string Name { get; set; } = string.Empty;
}

public enum Method
{
    Fly,
    Spinning,
    Trolling
}

public class Bait
{
    public Guid Id { get; set; }
    public string Colour { get; set; } = string.Empty;
}

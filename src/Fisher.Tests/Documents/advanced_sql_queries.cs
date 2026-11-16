using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     <c>session.AdvancedSql</c> — fisher#34, the read half.
/// </summary>
/// <remarks>
///     The port from Polecat is mostly mechanical. What is asserted hardest here is the two places
///     Fisher had to diverge: scalars go through the provider's typed accessors because Fisher stores
///     Guids, timestamps and bools as text or INTEGER, and a document is materialized by its own
///     storage selector rather than by deserializing a column — which is what makes a hierarchy come
///     back as its real sub-class.
/// </remarks>
public class advanced_sql_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("advanced-sql");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Vessel>().AddSubClass<Trawler>().AddSubClass<Dinghy>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Angler { Id = KnownId, Name = "Frodo", Fee = 12.34m, Active = true });
        session.Store(new Angler { Id = Guid.NewGuid(), Name = "Sam", Fee = 5m, Active = false });
        session.Store(new Trawler { Id = Guid.NewGuid(), Name = "Sea Fox", NetLength = 40 });
        session.Store(new Dinghy { Id = Guid.NewGuid(), Name = "Puddle", Oars = 2 });
        session.QueueSqlCommand("create table port (code text primary key, name text)");
        session.QueueSqlCommand("insert into port values ('AB', 'Aberdeen')");
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static readonly Guid KnownId = Guid.NewGuid();

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- scalars ----

    [Fact]
    public async Task a_string_scalar()
    {
        await using var session = _store.LightweightSession();

        var names = await session.AdvancedSql.QueryAsync<string>(
            "select name from port order by code", TestContext.Current.CancellationToken);

        names.ShouldBe(["Aberdeen"]);
    }

    /// <summary>
    ///     The reason Fisher cannot use Polecat's <c>GetValue</c> + <c>Convert.ChangeType</c>: Fisher
    ///     stores a Guid as text and a timestamp as text, and <c>Convert.ChangeType</c> to
    ///     <see cref="Guid" /> throws outright because <see cref="Guid" /> is not
    ///     <see cref="IConvertible" />.
    /// </summary>
    [Fact]
    public async Task scalars_fisher_stores_in_a_non_clr_shape()
    {
        await using var session = _store.LightweightSession();
        var token = TestContext.Current.CancellationToken;

        var ids = await session.AdvancedSql.QueryAsync<Guid>(
            "select id from fi_doc_angler where json_extract(data,'$.name') = ?", token, "Frodo");
        ids.ShouldBe([KnownId]);

        var flags = await session.AdvancedSql.QueryAsync<bool>(
            "select json_extract(data,'$.active') from fi_doc_angler "
            + "where json_extract(data,'$.name') = ?", token, "Frodo");
        flags.ShouldBe([true]);

        var stamps = await session.AdvancedSql.QueryAsync<DateTimeOffset>(
            "select last_modified from fi_doc_angler where id = ?", token, KnownId);
        stamps.ShouldHaveSingleItem().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task a_null_scalar_comes_back_as_the_default()
    {
        await using var session = _store.LightweightSession();

        var values = await session.AdvancedSql.QueryAsync<string>(
            "select null", TestContext.Current.CancellationToken);

        values.ShouldHaveSingleItem().ShouldBeNull();
    }

    // ---- documents ----

    [Fact]
    public async Task a_document_result_type()
    {
        await using var session = _store.LightweightSession();

        var anglers = await session.AdvancedSql.QueryAsync<Angler>(
            "select data from fi_doc_angler order by json_extract(data,'$.name')",
            TestContext.Current.CancellationToken);

        anglers.Select(x => x.Name).ShouldBe(["Frodo", "Sam"]);
        anglers[0].Fee.ShouldBe(12.34m);
    }

    /// <summary>
    ///     The payoff for going through the storage selector rather than deserializing <c>data</c> to
    ///     the declared type: each row comes back as the sub-class its <c>doc_type</c> names. Polecat's
    ///     reader would return three <c>Vessel</c>s here, quietly missing whatever each sub-class added.
    /// </summary>
    [Fact]
    public async Task a_hierarchy_comes_back_as_its_real_subclasses()
    {
        await using var session = _store.LightweightSession();

        var vessels = await session.AdvancedSql.QueryAsync<Vessel>(
            "select data, doc_type from fi_doc_vessel order by json_extract(data,'$.name')",
            TestContext.Current.CancellationToken);

        vessels.Select(x => x.GetType()).ShouldBe([typeof(Dinghy), typeof(Trawler)]);
        vessels.OfType<Trawler>().Single().NetLength.ShouldBe(40);
        vessels.OfType<Dinghy>().Single().Oars.ShouldBe(2);
    }

    /// <summary>
    ///     Which columns a document needs is knowable rather than guessable, and the SQL above is
    ///     written from it. If the storage layout changes, this fails rather than the queries silently
    ///     mis-reading.
    /// </summary>
    [Fact]
    public void the_columns_a_document_needs_are_reported()
    {
        using var session = _store.LightweightSession();

        session.AdvancedSql.SelectFieldsFor<Angler>().ShouldBe(["data"]);
        session.AdvancedSql.SelectFieldsFor<Vessel>().ShouldBe(["data", "doc_type"]);
    }

    // ---- tuples ----

    [Fact]
    public async Task two_and_three_result_types()
    {
        await using var session = _store.LightweightSession();
        var token = TestContext.Current.CancellationToken;

        var pairs = await session.AdvancedSql.QueryAsync<string, decimal>(
            "select json_extract(data,'$.name'), json_extract(data,'$.fee') from fi_doc_angler "
            + "order by json_extract(data,'$.name')", token);
        pairs.ShouldBe([("Frodo", 12.34m), ("Sam", 5m)]);

        var triples = await session.AdvancedSql.QueryAsync<string, decimal, bool>(
            "select json_extract(data,'$.name'), json_extract(data,'$.fee'), "
            + "json_extract(data,'$.active') from fi_doc_angler "
            + "order by json_extract(data,'$.name')", token);
        triples[0].ShouldBe(("Frodo", 12.34m, true));
    }

    /// <summary>
    ///     A document leading the row is fine, because the selector resolves from column 0.
    /// </summary>
    [Fact]
    public async Task a_document_may_lead_a_tuple()
    {
        await using var session = _store.LightweightSession();

        var rows = await session.AdvancedSql.QueryAsync<Angler, long>(
            "select data, 1 from fi_doc_angler where json_extract(data,'$.name') = ?",
            TestContext.Current.CancellationToken, "Frodo");

        rows.ShouldHaveSingleItem().Item1.Name.ShouldBe("Frodo");
        rows[0].Item2.ShouldBe(1L);
    }

    /// <summary>
    ///     And anywhere else it is refused by name, rather than producing the cast error a misaligned
    ///     read would.
    /// </summary>
    [Fact]
    public async Task a_document_anywhere_but_first_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.AdvancedSql.QueryAsync<long, Angler>(
                "select 1, data from fi_doc_angler", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Angler");
        exception.Message.ShouldContain("position 2");
    }

    // ---- JSON result types ----

    [Fact]
    public async Task an_unregistered_type_is_deserialized_from_one_json_column()
    {
        await using var session = _store.LightweightSession();

        var summaries = await session.AdvancedSql.QueryAsync<Summary>(
            "select json_object('name', json_extract(data,'$.name')) from fi_doc_angler "
            + "order by json_extract(data,'$.name')", TestContext.Current.CancellationToken);

        summaries.Select(x => x.Name).ShouldBe(["Frodo", "Sam"]);
    }

    // ---- object, and a JSON column beside another computed one (fisher#613) ----

    /// <summary>
    ///     <c>object</c> is the provider's own value, in any position. The first call is the issue's
    ///     exact one, which used to take the process down with SIGSEGV: <c>object</c> fell through to
    ///     the JSON reader, whose stream read crashes natively when another computed column shares the
    ///     row.
    /// </summary>
    [Fact]
    public async Task object_in_any_result_position_is_the_providers_value()
    {
        await using var session = _store.LightweightSession();
        var token = TestContext.Current.CancellationToken;

        (await session.AdvancedSql.QueryAsync<object, long>("select 1, 2", token))
            .ShouldBe([((object)1L, 2L)]);

        (await session.AdvancedSql.QueryAsync<long, object>("select 1, 'two'", token))
            .ShouldBe([(1L, (object)"two")]);

        var triple = (await session.AdvancedSql.QueryAsync<object, object, object>(
            "select 2.5, x'0102', null", token)).ShouldHaveSingleItem();
        triple.Item1.ShouldBe(2.5);
        triple.Item2.ShouldBe(new byte[] { 1, 2 });
        triple.Item3.ShouldBeNull();

        // Plain text is a value, not JSON: deserializing 'Aberdeen' as JSON would throw.
        (await session.AdvancedSql.QueryAsync<object>("select name from port", token))
            .ShouldBe(["Aberdeen"]);
    }

    [Fact]
    public async Task streaming_object_is_the_providers_value_too()
    {
        await using var session = _store.LightweightSession();
        var token = TestContext.Current.CancellationToken;

        var singles = new List<object>();
        await foreach (var value in session.AdvancedSql.StreamAsync<object>("select 'Aberdeen'", token))
        {
            singles.Add(value);
        }

        singles.ShouldBe(["Aberdeen"]);

        var pairs = new List<(object, long)>();
        await foreach (var pair in session.AdvancedSql.StreamAsync<object, long>("select 1, 2", token))
        {
            pairs.Add(pair);
        }

        pairs.ShouldBe([((object)1L, 2L)]);
    }

    /// <summary>
    ///     The root cause of fisher#613, reached without <c>object</c>: a JSON result that is a computed
    ///     column, beside another computed column. Microsoft.Data.Sqlite's <c>GetStream</c> scans the
    ///     row for a rowid, matches the other column on its null table name and passes that null to
    ///     <c>sqlite3_table_column_metadata</c>, which segfaults. If this regresses, the runner dies
    ///     with exit 139 rather than failing this test.
    /// </summary>
    [Fact]
    public async Task a_json_column_beside_another_computed_column_is_read()
    {
        await using var session = _store.LightweightSession();
        var token = TestContext.Current.CancellationToken;

        var leading = await session.AdvancedSql.QueryAsync<Summary, long>(
            "select json_object('name', 'Frodo'), 2", token);
        leading.ShouldBe([(new Summary("Frodo"), 2L)]);

        var trailing = await session.AdvancedSql.QueryAsync<long, Summary>(
            "select 1, json_object('name', 'Sam')", token);
        trailing.ShouldBe([(1L, new Summary("Sam"))]);
    }

    // ---- streaming, parameters, transaction scope ----

    [Fact]
    public async Task streaming_yields_every_row()
    {
        await using var session = _store.LightweightSession();

        var names = new List<string>();
        await foreach (var name in session.AdvancedSql.StreamAsync<string>(
                           "select json_extract(data,'$.name') from fi_doc_angler order by 1",
                           TestContext.Current.CancellationToken))
        {
            names.Add(name);
        }

        names.ShouldBe(["Frodo", "Sam"]);
    }

    [Fact]
    public async Task a_wrong_parameter_count_says_so()
    {
        await using var session = _store.LightweightSession();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.AdvancedSql.QueryAsync<string>(
                "select name from port where code = ? and name = ?",
                TestContext.Current.CancellationToken, "AB"));

        exception.Message.ShouldContain("2 '?' placeholders");
    }

    [Fact]
    public async Task a_different_placeholder_leaves_a_literal_question_mark_alone()
    {
        await using var session = _store.LightweightSession();

        // The literal '?' must be inside a string literal. A bare one would be SQLite's *own*
        // anonymous parameter marker, which Fisher never binds — so it fails with "must add values
        // for the following parameters" rather than being passed through as text.
        var values = await session.AdvancedSql.QueryAsync<string>(
            '$', "select 'why?' where 1 = $", TestContext.Current.CancellationToken, 1);

        values.ShouldBe(["why?"]);
    }

    /// <summary>
    ///     It runs on the session's own connection, so it reads the session's uncommitted writes —
    ///     which is the property that makes it usable for "check what I just queued" inside a unit of
    ///     work, and which a second connection could not offer.
    /// </summary>
    [Fact]
    public async Task it_sees_the_sessions_own_uncommitted_writes()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into port values ('CD', 'Cardiff')");

        (await session.AdvancedSql.QueryAsync<long>(
            "select count(*) from port", TestContext.Current.CancellationToken)).ShouldBe([1L]);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await session.AdvancedSql.QueryAsync<long>(
            "select count(*) from port", TestContext.Current.CancellationToken)).ShouldBe([2L]);
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Fee { get; set; }
        public bool Active { get; set; }
    }

    public class Vessel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Trawler : Vessel
    {
        public int NetLength { get; set; }
    }

    public class Dinghy : Vessel
    {
        public int Oars { get; set; }
    }

    public record Summary(string Name);
}

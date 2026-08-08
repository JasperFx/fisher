using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     <c>QueueSqlCommand</c> — fisher#34.
/// </summary>
/// <remarks>
///     Two things are being pinned. The first is atomicity, which is the point of the feature on
///     SQLite specifically: an application's own tables live in the same file, one writer per file, so
///     "my rows and Fisher's, or neither" is otherwise unreachable. The second is parameter binding,
///     which is where the Fisher-only work is — see <c>raw_sql_parameter_binding</c> below and
///     <c>SqliteParameterValue</c> for why three CLR types need converting and the rest do not.
/// </remarks>
public class queued_sql_commands : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("queued-sql");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        // The application's own table, in the same file. This is the shape the feature exists for.
        await ExecuteAsync(
            "create table ledger (id integer primary key, note text, amount real, at text, ref text)");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    // ---- the unit of work ----

    [Fact]
    public async Task queued_sql_commits_with_the_documents_and_events()
    {
        var id = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Store(new Angler { Id = id, Name = "Frodo" });
        session.Events.StartStream<Angler>(id, new AnglerLanded("Trout"));
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 1, "landed");

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select count(*) from ledger")).ShouldBe(1L);
        (await ScalarAsync("select count(*) from fi_events")).ShouldBe(1L);
        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(1L);
    }

    /// <summary>
    ///     The half that cannot be had any other way. Without this the application's insert and Fisher's
    ///     write are two transactions on one file, so a failure after the first leaves the ledger row
    ///     behind with nothing that caused it.
    /// </summary>
    [Fact]
    public async Task a_failure_rolls_the_queued_sql_back_with_everything_else()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Angler { Id = Guid.NewGuid(), Name = "Sam" });
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 1, "first");
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 1, "duplicate key");

        await Should.ThrowAsync<SqliteException>(
            () => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        (await ScalarAsync("select count(*) from ledger")).ShouldBe(0L);
        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(0L);
    }

    [Fact]
    public async Task statements_run_in_the_order_they_were_queued()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into ledger (id, note) values (1, 'a')");
        session.QueueSqlCommand("update ledger set note = note || 'b' where id = 1");
        session.QueueSqlCommand("update ledger set note = note || 'c' where id = 1");

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select note from ledger where id = 1")).ShouldBe("abc");
    }

    [Fact]
    public async Task a_wrong_parameter_count_says_so_and_names_both_counts()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 1);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("2 '?' placeholders");
        exception.Message.ShouldContain("1 values");
    }

    /// <summary>
    ///     A <c>?</c> inside a JSON path would otherwise be split as a placeholder, which is why the
    ///     escape exists — and why Fisher offers it here rather than only on the raw-query surface, as
    ///     Polecat does. A JSON path is a likely thing to write against <c>fi_doc_*</c>.
    /// </summary>
    [Fact]
    public async Task a_different_placeholder_leaves_a_literal_question_mark_alone()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand('$', "insert into ledger (id, note) values ($, 'why?')", 1);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select note from ledger where id = 1")).ShouldBe("why?");
    }

    [Fact]
    public async Task a_session_with_only_queued_sql_still_commits()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 9, "alone");

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select count(*) from ledger")).ShouldBe(1L);
    }

    [Fact]
    public async Task a_null_parameter_binds_as_null()
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into ledger (id, note) values (?, ?)", 1, null);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select note is null from ledger where id = 1")).ShouldBe(1L);
    }

    // ---- parameter binding: the Fisher-only half ----

    /// <summary>
    ///     The three conversions <c>SqliteParameterValue</c> exists for, each asserted against a value
    ///     Fisher itself wrote rather than against a hand-typed literal — so the test fails if either
    ///     side of the encoding changes.
    /// </summary>
    /// <remarks>
    ///     Every one of these returns zero matches without the conversion, silently. Verified by
    ///     removing each arm of <c>SqliteParameterValue.ToDatabaseValue</c> in turn.
    /// </remarks>
    [Fact]
    public async Task a_guid_parameter_matches_what_fisher_stored()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Id = id, Name = "Merry" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.QueueSqlCommand("insert into ledger (id, ref) select 1, id from fi_doc_angler where id = ?", id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync("select ref from ledger where id = 1")).ShouldBe(id.ToString());
    }

    /// <summary>
    ///     A raw <see cref="DateTimeOffset" /> binds as <c>2026-08-08 18:45:30.123+00:00</c> — space
    ///     separated, original offset — against Fisher's <c>2026-08-08T18:45:30.123Z</c>.
    /// </summary>
    /// <remarks>
    ///     <b>A one-sided range test does not pin this, and the first version of this test did not.</b>
    ///     At index 10 the stored form has <c>T</c> (0x54) and the raw binding has a space (0x20), so
    ///     the stored value sorts after <em>any</em> same-date raw bound regardless of the time it
    ///     names. A <c>&gt;</c> against an earlier bound therefore returns the right answer for the
    ///     wrong reason and passes with the conversion removed. The two cases below are chosen because
    ///     each fails without it: equality never matches, and a bound an hour <em>after</em> the event
    ///     wrongly includes it. The second is the shape worth remembering — not an empty result, which
    ///     someone would investigate, but a plausible wrong one.
    /// </remarks>
    [Fact]
    public async Task a_timestamp_parameter_matches_fishers_stored_form()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Angler>(Guid.NewGuid(), new AnglerLanded("Pike"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stored = SqliteTimestampOfTheOnlyEvent(await ScalarAsync("select timestamp from fi_events"));

        await using (var session = _store.LightweightSession())
        {
            session.QueueSqlCommand(
                "insert into ledger (id, note) select 1, count(*) from fi_events where timestamp = ?",
                stored);
            session.QueueSqlCommand(
                "insert into ledger (id, note) select 2, count(*) from fi_events where timestamp > ?",
                stored.AddHours(1));

            // A DateTime carries no offset, so one is assumed — Utc at face value, anything else as
            // local. This is the Utc case; getting the assumption wrong shifts the value by the
            // machine's offset, which is why the rule is stated rather than left to be inferred.
            session.QueueSqlCommand(
                "insert into ledger (id, note) select 3, count(*) from fi_events where timestamp = ?",
                stored.UtcDateTime);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync("select note from ledger where id = 1")).ShouldBe("1");
        (await ScalarAsync("select note from ledger where id = 2")).ShouldBe("0");
        (await ScalarAsync("select note from ledger where id = 3")).ShouldBe("1");
    }

    /// <summary>
    ///     Parses the stored column back the way <c>FisherEventsRowReader</c> does, so the value fed
    ///     back in is one Fisher itself produced rather than one the test invented.
    /// </summary>
    private static DateTimeOffset SqliteTimestampOfTheOnlyEvent(object? raw)
        => DateTimeOffset.Parse((string)raw!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>
    ///     Against a declared column SQLite's affinity rules rescue a text-bound decimal; against
    ///     <c>json_extract</c> there is no affinity to apply. Since <c>json_extract</c> is how every
    ///     undeclared document member is read, the JSON case is the one that matters, and it is the one
    ///     asserted here.
    /// </summary>
    /// <remarks>
    ///     Note the path is <c>$.fee</c>, not <c>$.Fee</c>: Fisher's serializer defaults to camelCase,
    ///     so raw SQL reaching into <c>data</c> has to spell members the way the serializer wrote them.
    ///     Worth a comment because getting it wrong yields zero rows rather than an error — this test
    ///     was written with the wrong casing first and failed exactly like a missing conversion would.
    /// </remarks>
    [Fact]
    public async Task a_decimal_parameter_matches_a_json_extracted_number()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Pippin", Fee = 12.34m });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.QueueSqlCommand(
                "insert into ledger (id, note) select 1, count(*) from fi_doc_angler "
                + "where json_extract(data, '$.fee') = ?", 12.34m);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync("select note from ledger where id = 1")).ShouldBe("1");
    }

    /// <summary>
    ///     The types that need no conversion, pinned so a provider upgrade that changes one fails here
    ///     and names it rather than presenting as a predicate that quietly stopped matching. Same
    ///     discipline as <c>metadata_column_coercions</c>, and the same reason: this is
    ///     Microsoft.Data.Sqlite's behaviour, not Fisher's.
    /// </summary>
    [Theory]
    [InlineData(42, "integer", "42")]
    [InlineData(9_000_000_000L, "integer", "9000000000")]
    [InlineData(true, "integer", "1")]
    [InlineData(false, "integer", "0")]
    [InlineData(12.5d, "real", "12.5")]
    [InlineData("hello", "text", "hello")]
    [InlineData(DayOfWeek.Friday, "integer", "5")]
    public async Task raw_sql_parameter_binding(object value, string expectedStorageClass, string expectedText)
    {
        await using var session = _store.LightweightSession();
        session.QueueSqlCommand("insert into ledger (id, note, ref) values (1, typeof(?), cast(? as text))",
            value, value);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select note from ledger where id = 1")).ShouldBe(expectedStorageClass);
        (await ScalarAsync("select ref from ledger where id = 1")).ShouldBe(expectedText);
    }

    public record AnglerLanded(string Species);

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Fee { get; set; }
    }
}

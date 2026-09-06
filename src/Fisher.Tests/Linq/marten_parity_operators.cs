using System.Globalization;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     The four small operators Marten had and Fisher did not — <c>MatchesSql</c>,
///     <c>Stats(out QueryStatistics)</c>, <c>ToAsyncEnumerable()</c> and <c>ExplainAsync</c>
///     (fisher#202).
/// </summary>
/// <remarks>
///     Each was named in the migration guide's gap table and on the LINQ operators page, so closing a
///     gap means removing its entry there as well as adding the member here. <c>ExplainAsync</c> is the
///     one that was in neither: it has no Marten-portable shape, since PostgreSQL's <c>EXPLAIN</c>
///     returns a costed JSON tree and SQLite's returns four columns of prose.
/// </remarks>
public class marten_parity_operators : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("parity-ops");
    private DocumentStore _store = null!;
    private readonly Guid _pike = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Catch>().Index(x => x.Species);
            o.Schema.For<Tagged>().SoftDeleted();
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Catch { Id = _pike, Weight = 8, Species = "Pike", Landed = Landed(1) });
        for (var i = 1; i <= 6; i++)
        {
            session.Store(new Catch
            {
                Id = Guid.NewGuid(), Weight = i, Species = i % 2 == 0 ? "Pike" : "Trout",
                Landed = Landed(i + 1)
            });
        }

        await session.SaveChangesAsync(Token);
    }

    private static DateTimeOffset Landed(int day)
        => new(2026, 3, day, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IQuerySession Session() => _store.LightweightSession();

    // ---- MatchesSql ----

    [Fact]
    public async Task a_raw_fragment_composes_into_a_where()
    {
        await using var session = Session();

        var heavy = await session.Query<Catch>()
            .Where(x => x.MatchesSql("json_extract(data, '$.weight') > 5"))
            .OrderBy(x => x.Weight)
            .ToListAsync(Token);

        heavy.Select(x => x.Weight).ShouldBe([6, 8]);
    }

    /// <summary>
    ///     The fragment is one term among the others, which is the whole difference from
    ///     <c>AdvancedSql</c> — that replaces the query, this composes into it.
    /// </summary>
    [Fact]
    public async Task a_raw_fragment_composes_with_translated_terms_and_ordering()
    {
        await using var session = Session();

        var pike = await session.Query<Catch>()
            .Where(x => x.Species == "Pike" && x.MatchesSql("json_extract(data, '$.weight') > ?", 5))
            .OrderByDescending(x => x.Weight)
            .ToListAsync(Token);

        pike.Select(x => x.Weight).ShouldBe([8, 6]);
    }

    /// <summary>
    ///     ⚠️ The security property. Values are bound, never interpolated — so the SQL text carries a
    ///     parameter marker and the value reaches the command's parameter collection.
    /// </summary>
    /// <remarks>
    ///     Asserted on the rendered command rather than only on the result, because a query that
    ///     inlined its value would return exactly the same rows. This is the fisher#161/#162 discipline:
    ///     the audit's whole subject is that no runtime value reaches the SQL text.
    /// </remarks>
    [Fact]
    public async Task a_matches_sql_value_is_bound_rather_than_interpolated()
    {
        await using var session = Session();

        var sql = session.ToSql(session.Query<Catch>()
            .Where(x => x.MatchesSql("json_extract(data, '$.species') = ?", "Pike")));

        sql.ShouldNotContain("Pike");
        sql.ShouldContain("@p0");

        var pike = await session.Query<Catch>()
            .Where(x => x.MatchesSql("json_extract(data, '$.species') = ?", "Pike"))
            .CountAsync(Token);

        pike.ShouldBe(4);
    }

    /// <summary>
    ///     ⚠️ A Guid, a timestamp and a decimal go through the same conversions <c>IAdvancedSql</c>
    ///     applies. Without them each binds to something Fisher never wrote and matches nothing —
    ///     silently, because "no rows" is a perfectly ordinary answer.
    /// </summary>
    [Fact]
    public async Task a_matches_sql_value_is_converted_to_fishers_storage_encoding()
    {
        await using var session = Session();

        // A raw Guid binds UPPERCASE against lowercase canonical text; the conversion is what makes
        // this match at all.
        var byId = await session.Query<Catch>()
            .Where(x => x.MatchesSql("id = ?", _pike))
            .ToListAsync(Token);

        byId.ShouldHaveSingleItem().Weight.ShouldBe(8);

        // last_modified holds SqliteTimestamp's fixed-width UTC form; a raw DateTimeOffset binds
        // space-separated with its original offset, which sorts after any same-date stored value.
        var recent = await session.Query<Catch>()
            .Where(x => x.MatchesSql("last_modified > ?", DateTimeOffset.UtcNow.AddMinutes(-5)))
            .CountAsync(Token);

        recent.ShouldBe(7);
    }

    /// <summary>
    ///     ⚠️ The fragment is bracketed, so an <c>or</c> inside it cannot swallow the terms beside it —
    ///     including the implicit soft-delete, tenant and hierarchy filters, which is the one way this
    ///     operator could turn into a read of rows the caller never asked for.
    /// </summary>
    /// <remarks>
    ///     Uses a soft-deleted type, because the filter it must not swallow is one Fisher adds rather
    ///     than one the caller wrote. Without the brackets the rendered predicate is
    ///     <c>… and a or b and is_deleted = 0</c>, which <c>and</c> binding tighter than <c>or</c>
    ///     turns into "everything matching a, deleted or not".
    /// </remarks>
    [Fact]
    public async Task a_raw_fragment_cannot_swallow_the_filters_beside_it()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Tagged { Id = "live", Label = "a" });
        session.Store(new Tagged { Id = "gone", Label = "b" });
        await session.SaveChangesAsync(Token);

        session.Delete<Tagged>("gone");
        await session.SaveChangesAsync(Token);

        await using var reader = Session();

        var sql = reader.ToSql(reader.Query<Tagged>()
            .Where(x => x.MatchesSql("json_extract(data, '$.label') = 'a' "
                                     + "or json_extract(data, '$.label') = 'b'")));

        sql.ShouldContain("(json_extract(data, '$.label') = 'a' "
                          + "or json_extract(data, '$.label') = 'b')");

        var found = await reader.Query<Tagged>()
            .Where(x => x.MatchesSql("json_extract(data, '$.label') = 'a' "
                                     + "or json_extract(data, '$.label') = 'b'"))
            .ToListAsync(Token);

        found.ShouldHaveSingleItem().Id.ShouldBe("live");
    }

    /// <summary>
    ///     Too few values is otherwise an <c>IndexOutOfRangeException</c> from inside the provider;
    ///     too many is <em>silence</em>, with the surplus never reaching the query. Both are refused by
    ///     name, which is the shape marten#5289's follow-up had to add after the fact.
    /// </summary>
    [Fact]
    public async Task a_placeholder_count_mismatch_is_refused_by_name()
    {
        await using var session = Session();

        var tooFew = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>()
                .Where(x => x.MatchesSql("json_extract(data, '$.weight') > ? and "
                                         + "json_extract(data, '$.weight') < ?", 2))
                .ToListAsync(Token));

        tooFew.Message.ShouldContain("MatchesSql");
        tooFew.Message.ShouldContain("2 '?' placeholder");

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>()
                .Where(x => x.MatchesSql("json_extract(data, '$.weight') > ?", 2, 9))
                .ToListAsync(Token));
    }

    /// <summary>
    ///     The placeholder overload, for SQL carrying a literal <c>?</c>. A bare <c>?</c> Fisher does
    ///     not consume is still SQLite's own anonymous parameter marker, so it does not pass through as
    ///     text — the trap <c>IAdvancedSql</c>'s twins exist for.
    /// </summary>
    [Fact]
    public async Task a_custom_placeholder_leaves_a_literal_question_mark_alone()
    {
        await using var session = Session();

        var found = await session.Query<Catch>()
            .Where(x => x.MatchesSql('^', "json_extract(data, '$.species') = ^ "
                                          + "and instr(json_extract(data, '$.species'), '?') = 0",
                "Pike"))
            .CountAsync(Token);

        found.ShouldBe(4);
    }

    [Fact]
    public void matches_sql_outside_a_query_says_so()
    {
        Action call = () => _ = new Catch().MatchesSql("1=1");

        call.ShouldThrow<NotSupportedException>().Message.ShouldContain("MatchesSql");
    }

    // ---- Stats ----

    [Fact]
    public async Task stats_reports_the_total_beside_a_paged_result()
    {
        await using var session = Session();

        var page = await session.Query<Catch>()
            .Where(x => x.Weight > 1)
            .Stats(out var stats)
            .OrderBy(x => x.Weight)
            .Skip(1).Take(2)
            .ToListAsync(Token);

        page.Select(x => x.Weight).ShouldBe([3, 4]);
        stats.TotalResults.ShouldBe(6);
    }

    /// <summary>
    ///     The total is the query's, not the page's — which is the entire point, and the distinction
    ///     <c>CountIgnoringPagingAsync</c> already draws against <c>CountAsync</c>.
    /// </summary>
    [Fact]
    public async Task the_total_ignores_paging_and_survives_a_page_past_the_end()
    {
        await using var session = Session();

        var page = await session.Query<Catch>()
            .Stats(out var stats)
            .OrderBy(x => x.Weight)
            .Skip(50).Take(5)
            .ToListAsync(Token);

        page.ShouldBeEmpty();
        stats.TotalResults.ShouldBe(7);
    }

    /// <summary>
    ///     Where the operator sits in the chain does not matter — it is a marker on the expression
    ///     tree, not a wrapper that has to be outermost.
    /// </summary>
    [Fact]
    public async Task stats_reads_the_predicates_wherever_it_sits_in_the_chain()
    {
        await using var session = Session();

        await session.Query<Catch>().OrderBy(x => x.Weight).Take(2)
            .Stats(out var trailing).Where(x => x.Species == "Pike").ToListAsync(Token);

        trailing.TotalResults.ShouldBe(4);
    }

    [Fact]
    public async Task stats_works_over_a_projection()
    {
        await using var session = Session();

        var names = await session.Query<Catch>()
            .Stats(out var stats)
            .OrderBy(x => x.Weight).Take(2)
            .Select(x => x.Species)
            .ToListAsync(Token);

        names.Count.ShouldBe(2);
        stats.TotalResults.ShouldBe(7);
    }

    /// <summary>
    ///     A scalar terminal is refused by name rather than leaving <c>TotalResults</c> at zero, which
    ///     would be a wrong answer the caller cannot see.
    /// </summary>
    [Fact]
    public async Task stats_on_a_scalar_terminal_is_refused_by_name()
    {
        await using var session = Session();

        var refusal = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Stats(out _).CountAsync(Token));

        refusal.Message.ShouldContain("Stats(out QueryStatistics)");
        refusal.Message.ShouldContain("ToListAsync");
    }

    /// <summary>
    ///     <c>ToPagedListAsync</c> computes its own total through the same count, so a redundant
    ///     <c>Stats</c> beside it must not collide with it.
    /// </summary>
    [Fact]
    public async Task stats_alongside_to_paged_list_is_redundant_rather_than_an_error()
    {
        await using var session = Session();

        var page = await session.Query<Catch>()
            .Stats(out var stats)
            .OrderBy(x => x.Weight)
            .ToPagedListAsync(1, 3, Token);

        page.TotalItemCount.ShouldBe(7);
        stats.TotalResults.ShouldBe(7);
    }

    // ---- ToAsyncEnumerable ----

    [Fact]
    public async Task documents_stream_one_at_a_time()
    {
        await using var session = Session();

        var weights = new List<int>();

        await foreach (var one in session.Query<Catch>().OrderBy(x => x.Weight)
                           .ToAsyncEnumerable(Token))
        {
            weights.Add(one.Weight);
        }

        weights.ShouldBe([1, 2, 3, 4, 5, 6, 8]);
    }

    /// <summary>
    ///     The implicit filters and the ordinary translation apply, because this is the same statement
    ///     <c>ToListAsync</c> builds — only the materialization differs.
    /// </summary>
    [Fact]
    public async Task streaming_carries_the_predicates_and_the_paging()
    {
        await using var session = Session();

        var weights = new List<int>();

        await foreach (var one in session.Query<Catch>()
                           .Where(x => x.Species == "Pike")
                           .OrderByDescending(x => x.Weight).Take(2)
                           .ToAsyncEnumerable(Token))
        {
            weights.Add(one.Weight);
        }

        weights.ShouldBe([8, 6]);
    }

    /// <summary>
    ///     Breaking out part way disposes the reader through the <c>await foreach</c>, so the session
    ///     is usable afterwards rather than holding an open reader on its one connection.
    /// </summary>
    [Fact]
    public async Task a_partial_enumeration_releases_the_reader()
    {
        await using var session = Session();

        await foreach (var one in session.Query<Catch>().OrderBy(x => x.Weight)
                           .ToAsyncEnumerable(Token))
        {
            one.Weight.ShouldBe(1);
            break;
        }

        (await session.Query<Catch>().CountAsync(Token)).ShouldBe(7);
    }

    [Fact]
    public async Task a_projection_streams_too()
    {
        await using var session = Session();

        var species = new List<string>();

        await foreach (var name in session.Query<Catch>().OrderBy(x => x.Weight)
                           .Select(x => x.Species).ToAsyncEnumerable(Token))
        {
            species.Add(name);
        }

        species.Count.ShouldBe(7);
        species[0].ShouldBe("Trout");
    }

    // ---- ExplainAsync ----

    /// <summary>
    ///     The question a declared index otherwise leaves unanswerable. SQLite's planner uses an
    ///     expression index only when the query's expression matches the index's, so an index that is
    ///     never used is created without error and reports nothing anywhere.
    /// </summary>
    [Fact]
    public async Task the_plan_shows_a_declared_index_being_used()
    {
        await using var session = Session();

        var plan = await session.Query<Catch>().Where(x => x.Species == "Pike").ExplainAsync(Token);

        plan.Steps.ShouldNotBeEmpty();
        plan.UsesIndex.ShouldBeTrue();
        plan.ToString().ShouldContain("json_extract");
    }

    [Fact]
    public async Task an_unindexed_predicate_reports_a_scan()
    {
        await using var session = Session();

        var plan = await session.Query<Catch>().Where(x => x.Weight > 3).ExplainAsync(Token);

        plan.ScansTable.ShouldBeTrue();
        plan.UsesIndex.ShouldBeFalse();
    }

    /// <summary>
    ///     The plan is taken over the exact statement the query would run, so the SQL it reports is
    ///     what <c>ToSql</c> reports — implicit filters, parameters and all. Explaining a
    ///     re-derivation would answer about a query nobody was going to execute.
    /// </summary>
    [Fact]
    public async Task the_plan_carries_the_sql_it_explained()
    {
        await using var session = Session();

        var queryable = session.Query<Catch>().Where(x => x.Species == "Pike").OrderBy(x => x.Weight);

        var plan = await queryable.ExplainAsync(Token);

        plan.Sql.ShouldBe(session.ToSql(queryable));
        plan.Sql.ShouldNotContain("explain");
    }

    /// <summary>
    ///     Explaining plans; it does not run. A query whose execution would be refused still explains,
    ///     and nothing is read.
    /// </summary>
    [Fact]
    public async Task explaining_does_not_execute_the_query()
    {
        await using var session = Session();

        var plan = await session.Query<Catch>()
            .Where(x => x.Weight > 3)
            .Stats(out var stats)
            .ExplainAsync(Token);

        plan.Steps.ShouldNotBeEmpty();
        stats.TotalResults.ShouldBe(0);
    }

    /// <summary>
    ///     A culture whose decimal separator is a comma must not change the SQL — the same guard
    ///     <c>sql_injection_hardening</c> keeps over the modulo operands.
    /// </summary>
    [Fact]
    public async Task a_matches_sql_query_survives_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

        try
        {
            await using var session = Session();

            var found = await session.Query<Catch>()
                .Where(x => x.MatchesSql("json_extract(data, '$.weight') > ?", 1.5m))
                .CountAsync(Token);

            found.ShouldBe(6);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public int Weight { get; set; }
        public string Species { get; set; } = "";
        public DateTimeOffset Landed { get; set; }
    }

    public class Tagged
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }
}

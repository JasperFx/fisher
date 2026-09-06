using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Relevance ordering over the FTS5 index — fisher#220.
/// </summary>
/// <remarks>
///     <para>
///         #215 landed the index and the six search operators, all of which only ever FILTER on a
///         match. <c>bm25()</c> reads a value the match computes, which the #215 sub-select predicate
///         cannot hand back — so ordering by relevance is what turns that predicate into a join. These
///         tests are about the two halves of that: the ranking is genuinely by relevance rather than by
///         insertion order, and everything the sub-select composed with still composes.
///     </para>
///     <para>
///         The corpus is deliberately built so relevance and insertion order DISAGREE. A test whose
///         expected order matches the order the rows were stored in cannot tell a working bm25 from a
///         rank that was silently dropped, which is the failure mode worth guarding — nothing here
///         throws when ranking does not happen.
///     </para>
/// </remarks>
public class full_text_relevance : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fulltext-rank");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Paper>().FullTextIndex(x => x.Title, x => x.Abstract);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        // Stored WEAKEST first, so a result in insertion order is visibly not a ranked one.
        session.Store(new Paper
        {
            Slug = "passing-mention",
            Title = "Bridges of the lowlands",
            Abstract = "A survey of river crossings. Corrosion is mentioned once.",
            Year = 2001
        });
        session.Store(new Paper
        {
            Slug = "body-heavy",
            Title = "Structural fatigue",
            Abstract = "Corrosion drives fatigue. Corrosion in joints, corrosion at welds, "
                       + "and corrosion under insulation are each treated in turn.",
            Year = 2010
        });
        session.Store(new Paper
        {
            Slug = "title-hit",
            Title = "Corrosion",
            Abstract = "A short note.",
            Year = 2020
        });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    private async Task<string[]> SlugsAsync(Func<IQuerySession, Task<IReadOnlyList<Paper>>> query)
    {
        await using var session = _store.QuerySession();
        var results = await query(session);
        return results.Select(x => x.Slug).ToArray();
    }

    [Fact]
    public async Task ranks_by_relevance_rather_than_by_insertion_order()
    {
        var slugs = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion"))
            .OrderByRelevance()
            .ToListAsync(Token));

        slugs.Length.ShouldBe(3);

        // The exact middle ordering is bm25's to decide and depends on its length normalisation, so
        // what is pinned is the part that is the point: the passing mention, stored FIRST, ranks LAST.
        slugs.Last().ShouldBe("passing-mention");
        slugs.ShouldNotBe(new[] { "passing-mention", "body-heavy", "title-hit" });
    }

    [Fact]
    public async Task descending_is_the_exact_reverse()
    {
        var best = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion")).OrderByRelevance().ToListAsync(Token));

        var worst = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion")).OrderByRelevanceDescending().ToListAsync(Token));

        worst.ShouldBe(Enumerable.Reverse(best).ToArray());
    }

    /// <summary>
    ///     The composition ruling: relevance is an ordering term, not a replacement for one.
    /// </summary>
    [Fact]
    public async Task relevance_composes_with_a_following_order_by()
    {
        // Every paper carries the same term once in the title, so bm25 ties them and the tiebreak
        // decides the whole order -- which is what makes this a test of composition rather than of
        // ranking.
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Paper { Slug = "tie-a", Title = "Ballast", Abstract = "x", Year = 1999 });
            session.Store(new Paper { Slug = "tie-b", Title = "Ballast", Abstract = "x", Year = 2015 });
            await session.SaveChangesAsync(Token);
        }

        var ascending = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("ballast"))
            .OrderByRelevance()
            .ThenBy(x => x.Year)
            .ToListAsync(Token));

        ascending.ShouldBe(new[] { "tie-a", "tie-b" });

        var descending = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("ballast"))
            .OrderByRelevance()
            .ThenByDescending(x => x.Year)
            .ToListAsync(Token));

        descending.ShouldBe(new[] { "tie-b", "tie-a" });
    }

    [Fact]
    public async Task relevance_composes_after_an_ordinary_order_by()
    {
        var slugs = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion"))
            .OrderBy(x => x.Year)
            .ThenByRelevance()
            .ToListAsync(Token));

        slugs.ShouldBe(new[] { "passing-mention", "body-heavy", "title-hit" });
    }

    /// <summary>
    ///     Column weights, which had nowhere to live before there was anything to weight.
    /// </summary>
    /// <remarks>
    ///     Weighting the title to nothing has to change the answer, and the direction is checkable
    ///     without depending on bm25's exact arithmetic: with the title discounted, the paper whose
    ///     only hit is its title must fall behind the one that says the word repeatedly in its body.
    /// </remarks>
    [Fact]
    public async Task column_weights_change_the_ranking()
    {
        var weighted = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion"))
            .OrderByRelevance(0.0, 1.0)
            .ToListAsync(Token));

        Array.IndexOf(weighted, "body-heavy").ShouldBeLessThan(Array.IndexOf(weighted, "title-hit"));

        var titleHeavy = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion"))
            .OrderByRelevance(10.0, 0.1)
            .ToListAsync(Token));

        titleHeavy.First().ShouldBe("title-hit");
    }

    [Fact]
    public async Task relevance_composes_with_paging_and_an_ordinary_predicate()
    {
        var slugs = await SlugsAsync(session => session.Query<Paper>()
            .Where(x => x.Search("corrosion") && x.Year > 2005)
            .OrderByRelevance()
            .Take(1)
            .ToListAsync(Token));

        slugs.Length.ShouldBe(1);
        slugs.ShouldNotContain("passing-mention");
    }

    [Fact]
    public async Task counting_a_ranked_query_still_works()
    {
        await using var session = _store.QuerySession();

        var count = await session.Query<Paper>()
            .Where(x => x.Search("corrosion"))
            .OrderByRelevance()
            .CountAsync(Token);

        count.ShouldBe(3);
    }

    // ---- refusals ----

    /// <summary>
    ///     bm25() is only legal where its table is the subject of a MATCH, so ranking without a
    ///     full-text predicate is refused by name rather than left to fail as SQLite syntax.
    /// </summary>
    [Fact]
    public async Task ranking_without_a_full_text_predicate_is_refused()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Paper>().OrderByRelevance().ToListAsync(Token));

        ex.Message.ShouldContain("needs a full-text predicate");
    }

    [Fact]
    public async Task ranking_a_negated_match_is_refused()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Paper>()
                .Where(x => !x.Search("corrosion"))
                .OrderByRelevance()
                .ToListAsync(Token));

        ex.Message.ShouldContain("negated");
    }

    [Fact]
    public async Task ranking_two_matches_is_refused_rather_than_picking_one()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Paper>()
                .Where(x => x.Search("corrosion") && x.Search("fatigue"))
                .OrderByRelevance()
                .ToListAsync(Token));

        ex.Message.ShouldContain("cannot tell which one");
    }
}

public class Paper
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Abstract { get; set; } = string.Empty;
    public int Year { get; set; }
}

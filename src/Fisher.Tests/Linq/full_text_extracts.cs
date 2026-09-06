using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Projected snippets and highlights over the FTS5 index — the second half of fisher#220.
/// </summary>
/// <remarks>
///     <para>
///         These read a value the match COMPUTES, so they are columns without being document members.
///         <c>SelectProjection</c> already rewrites arbitrary sub-expressions into reads from the row's
///         value array, so a snippet becomes a locator there exactly the way a member does and the rest
///         of the projection machinery cannot tell the difference.
///     </para>
///     <para>
///         <b>They work only because Fisher's FTS5 table uses external content.</b> A contentless FTS5
///         table stores no text, and <c>snippet()</c> over one returns an empty string rather than an
///         error — so this capability would have been silently useless on a different index design.
///         Verified against SQLite directly before the code was written.
///     </para>
/// </remarks>
public class full_text_extracts : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fulltext-extract");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Report>().FullTextIndex(x => x.Title, x => x.Body);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Report
        {
            Title = "Corrosion basics",
            // Deliberately well past the 32-token default budget, so a snippet that comes back
            // un-elided means the budget was ignored rather than that the text simply fitted.
            Body = "An extended treatment of corrosion in structural steel, covering the "
                   + "electrochemistry of the process, the role of chlorides and standing water, "
                   + "the prevention of corrosion by coating and by cathodic protection, the "
                   + "inspection regimes that catch it early, the repair techniques available "
                   + "once it has taken hold, and the long service life of a structure that "
                   + "nobody inspects often enough to notice any of it happening at all."
        });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task a_snippet_marks_the_matched_term_and_elides_the_rest()
    {
        await using var session = _store.QuerySession();

        var extract = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => x.Snippet())
            .FirstAsync(Token);

        extract.ShouldContain("<b>corrosion</b>");

        // The body is far longer than the token budget, so an un-elided answer would mean the
        // budget was ignored -- which is how a snippet silently becomes "the whole column". The
        // comparison is against the stored text rather than a magic number, so it keeps meaning the
        // same thing if the corpus is ever edited.
        extract.ShouldContain("…");

        await using var reading = _store.QuerySession();
        var stored = await reading.Query<Report>().FirstAsync(Token);
        extract.Length.ShouldBeLessThan(stored.Body.Length);
    }

    [Fact]
    public async Task snippet_markers_and_budget_are_the_callers()
    {
        await using var session = _store.QuerySession();

        var extract = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => x.Snippet("[", "]", "...", 6))
            .FirstAsync(Token);

        extract.ShouldContain("[corrosion]");
        extract.ShouldContain("...");
        extract.ShouldNotContain("<b>");
    }

    [Fact]
    public async Task a_highlight_returns_the_whole_named_column()
    {
        await using var session = _store.QuerySession();

        var highlighted = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => x.Highlight(nameof(Report.Title)))
            .FirstAsync(Token);

        highlighted.ShouldBe("<b>Corrosion</b> basics");
    }

    [Fact]
    public async Task a_highlight_takes_the_callers_markers()
    {
        await using var session = _store.QuerySession();

        var highlighted = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => x.Highlight(nameof(Report.Title), "<em>", "</em>"))
            .FirstAsync(Token);

        highlighted.ShouldBe("<em>Corrosion</em> basics");
    }

    /// <summary>
    ///     The shape the ruling asked for: an extract sitting beside ordinary members in one anonymous
    ///     type, which is what "a snippet is a projection member" has to mean to be worth anything.
    /// </summary>
    [Fact]
    public async Task an_extract_projects_alongside_document_members()
    {
        await using var session = _store.QuerySession();

        var row = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => new { x.Title, Extract = x.Snippet(), Whole = x.Highlight(nameof(Report.Body)) })
            .FirstAsync(Token);

        row.Title.ShouldBe("Corrosion basics");
        row.Extract.ShouldContain("<b>corrosion</b>");
        row.Whole.ShouldContain("<b>corrosion</b>");
        row.Whole.ShouldContain("nobody inspects often enough");
    }

    [Fact]
    public async Task an_extract_composes_with_relevance_ordering()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Report { Title = "Bridges", Body = "corrosion is mentioned once" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();

        var rows = await query.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .OrderByRelevance()
            .Select(x => new { x.Title, Extract = x.Snippet() })
            .ToListAsync(Token);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Extract.Contains("<b>corrosion</b>"));
    }

    /// <summary>
    ///     A marker carrying a quote must not be able to close the literal it is rendered into. These
    ///     go into the SELECT list as SQL literals rather than as parameters, so the escaping is the
    ///     only thing between a caller's string and the statement.
    /// </summary>
    [Fact]
    public async Task a_marker_containing_a_quote_is_escaped_rather_than_injected()
    {
        await using var session = _store.QuerySession();

        var extract = await session.Query<Report>()
            .Where(x => x.Search("corrosion"))
            .Select(x => x.Snippet("o'brien'", "'", "...", 6))
            .FirstAsync(Token);

        extract.ShouldContain("o'brien'corrosion'");
    }

    // ---- refusals ----

    [Fact]
    public async Task an_unknown_highlight_column_is_refused_with_the_indexed_members_named()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Report>()
                .Where(x => x.Search("corrosion"))
                .Select(x => x.Highlight("Nonexistent"))
                .ToListAsync(Token));

        ex.Message.ShouldContain("Nonexistent");
        ex.Message.ShouldContain("Title");
        ex.Message.ShouldContain("Body");
    }

    [Fact]
    public async Task projecting_a_snippet_without_a_full_text_predicate_is_refused()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Report>().Select(x => x.Snippet()).ToListAsync(Token));

        ex.Message.ShouldContain("full-text predicate");
    }

    [Fact]
    public void calling_an_extract_outside_a_query_throws_rather_than_returning_a_plausible_value()
    {
        var report = new Report { Title = "x", Body = "y" };

        Should.Throw<NotSupportedException>(() => report.Snippet());
        Should.Throw<NotSupportedException>(() => report.Highlight("Title"));
    }
}

public class Report
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

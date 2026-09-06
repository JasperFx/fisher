using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using Fisher.Storage.FullText;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Full-text search over SQLite's FTS5 — fisher#215.
/// </summary>
/// <remarks>
///     <para>
///         The tests that matter are the ones about staying in <em>step</em>. A full-text index that
///         has drifted does not error — it returns fewer rows than it should, which is
///         indistinguishable from a search that legitimately matched nothing. So updating a document,
///         deleting one, and writing straight past Fisher with raw SQL are all pinned, and so is every
///         refusal, because each refusal replaces exactly that silence.
///     </para>
///     <para>
///         <c>Article</c> is soft-deleted on purpose, so the implicit filters are exercised by the
///         ordinary searches rather than only by the test named for them.
///     </para>
/// </remarks>
public class full_text_search : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fulltext");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Article>().SoftDeleted()
                .FullTextIndex(x => x.Title, x => x.Body);
            options.Schema.For<Note>().FullTextIndex();
            options.Schema.For<Part>().FullTextIndex(FullTextTokenizer.Trigram, x => x.Code);
            options.Schema.For<Plain>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        session.Store(new Article
        {
            Title = "The quick brown fox", Body = "It jumps over the lazy dog", Author = "Aesop"
        });
        session.Store(new Article
        {
            // "the" is in all three bodies or titles, so it is the term the exclusion tests narrow.
            Title = "Running with scissors", Body = "A cautionary tale about the perils of haste",
            Author = "Nobody"
        });
        session.Store(new Article
        {
            Title = "Wombats of the southern hemisphere", Body = "They dig, and they are cubic",
            Author = "Aesop"
        });

        session.Store(new Note { Heading = "Groceries", Text = "milk and marmalade" });
        session.Store(new Part { Code = "WIDGET-4471-WOMBAT" });
        session.Store(new Part { Code = "SPROCKET-0099-BADGER" });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    private async Task<string[]> TitlesAsync(Func<IQuerySession, Task<IReadOnlyList<Article>>> query)
    {
        await using var session = _store.QuerySession();
        var results = await query(session);
        return results.Select(x => x.Title).OrderBy(x => x).ToArray();
    }

    // ---- the operators ----

    [Fact]
    public async Task search_passes_fts5_syntax_through()
    {
        var titles = await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.Search("quick AND fox")).ToListAsync(Token));

        titles.ShouldBe(["The quick brown fox"]);
    }

    [Fact]
    public async Task search_can_scope_to_one_indexed_column()
    {
        await using var session = _store.QuerySession();

        // "dog" is in the body of the fox article and in no title.
        (await session.Query<Article>().Where(x => x.Search("body: dog")).CountAsync(Token))
            .ShouldBe(1);

        (await session.Query<Article>().Where(x => x.Search("title: dog")).CountAsync(Token))
            .ShouldBe(0);
    }

    [Fact]
    public async Task plain_text_search_ands_the_words_in_any_order()
    {
        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PlainTextSearch("fox brown")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox"]);

        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PlainTextSearch("fox wombats")).ToListAsync(Token)))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     The safety property that makes it the operator for user input.
    /// </summary>
    [Fact]
    public async Task plain_text_search_treats_operators_as_words()
    {
        await using var session = _store.QuerySession();

        // Unquoted, `fox OR wombats` is a disjunction and would match two articles. Quoted, OR is a
        // word nothing contains — so the honest answer is none, not an FTS5 syntax error either.
        (await session.Query<Article>().Where(x => x.PlainTextSearch("fox OR wombats"))
            .CountAsync(Token)).ShouldBe(0);

        // And a term full of FTS5 punctuation is a search rather than a parse failure.
        (await session.Query<Article>().Where(x => x.PlainTextSearch("\"unbalanced (")).CountAsync(Token))
            .ShouldBe(0);
    }

    [Fact]
    public async Task phrase_search_requires_the_words_adjacent_and_in_order()
    {
        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PhraseSearch("quick brown")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox"]);

        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PhraseSearch("brown quick")).ToListAsync(Token)))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task prefix_search_matches_from_the_start_of_a_word()
    {
        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PrefixSearch("womb")).ToListAsync(Token)))
            .ShouldBe(["Wombats of the southern hemisphere"]);

        // ... and not from the middle, which is what NgramSearch is for.
        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.PrefixSearch("ombat")).ToListAsync(Token)))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task web_style_search_handles_phrases_alternatives_and_exclusions()
    {
        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.WebStyleSearch("\"quick brown\"")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox"]);

        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.WebStyleSearch("fox or wombats")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox", "Wombats of the southern hemisphere"]);

        (await TitlesAsync(s =>
            s.Query<Article>().Where(x => x.WebStyleSearch("the -fox -wombats")).ToListAsync(Token)))
            .ShouldBe(["Running with scissors"]);
    }

    [Fact]
    public async Task web_style_search_cannot_be_malformed()
    {
        await using var session = _store.QuerySession();

        // Every one of these is a syntax error to raw FTS5 and a plain search here.
        foreach (var term in new[] { "\"unterminated", "AND", "*", "((" })
        {
            await session.Query<Article>().Where(x => x.WebStyleSearch(term)).CountAsync(Token);
        }
    }

    [Fact]
    public async Task ngram_search_matches_inside_a_word()
    {
        await using var session = _store.QuerySession();

        var parts = await session.Query<Part>().Where(x => x.NgramSearch("4471")).ToListAsync(Token);
        parts.Select(x => x.Code).ShouldBe(["WIDGET-4471-WOMBAT"]);

        var middle = await session.Query<Part>().Where(x => x.NgramSearch("OMBA")).ToListAsync(Token);
        middle.Select(x => x.Code).ShouldBe(["WIDGET-4471-WOMBAT"]);
    }

    [Fact]
    public async Task an_index_over_the_whole_document_searches_every_value()
    {
        await using var session = _store.QuerySession();

        (await session.Query<Note>().Where(x => x.PlainTextSearch("marmalade")).CountAsync(Token))
            .ShouldBe(1);

        (await session.Query<Note>().Where(x => x.PlainTextSearch("Groceries")).CountAsync(Token))
            .ShouldBe(1);
    }

    // ---- staying in step ----

    [Fact]
    public async Task an_updated_document_is_reindexed()
    {
        await using (var writing = _store.LightweightSession())
        {
            var article = await writing.Query<Article>().Where(x => x.Search("fox")).FirstAsync(Token);
            article!.Title = "The sluggish grey badger";
            writing.Store(article);
            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        (await session.Query<Article>().Where(x => x.PlainTextSearch("fox")).CountAsync(Token))
            .ShouldBe(0);
        (await session.Query<Article>().Where(x => x.PlainTextSearch("badger")).CountAsync(Token))
            .ShouldBe(1);
    }

    [Fact]
    public async Task a_hard_deleted_document_leaves_the_index()
    {
        await using (var writing = _store.LightweightSession())
        {
            var note = await writing.Query<Note>().Where(x => x.Search("marmalade")).FirstAsync(Token);
            writing.Delete(note!);
            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        (await session.Query<Note>().Where(x => x.PlainTextSearch("marmalade")).CountAsync(Token))
            .ShouldBe(0);
    }

    /// <summary>
    ///     A soft delete is an UPDATE, so the row stays indexed — and the ordinary soft-delete filter
    ///     is what removes it from the answer.
    /// </summary>
    /// <remarks>
    ///     Free rather than implemented, and worth pinning for exactly that reason: the full-text
    ///     predicate is one more <c>where</c> fragment on the ordinary statement, so the tenant,
    ///     soft-delete and hierarchy filters apply to it the way they apply to any other query. An
    ///     index that answered on its own would be the fourth place all three have to be remembered.
    /// </remarks>
    [Fact]
    public async Task a_soft_deleted_document_is_filtered_out_of_a_search()
    {
        await using (var writing = _store.LightweightSession())
        {
            var article = await writing.Query<Article>().Where(x => x.Search("fox")).FirstAsync(Token);
            writing.Delete(article!);
            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        (await session.Query<Article>().Where(x => x.Search("fox")).CountAsync(Token)).ShouldBe(0);

        (await session.Query<Article>().Where(x => x.Search("fox")).MaybeDeleted().CountAsync(Token))
            .ShouldBe(1);
    }

    /// <summary>
    ///     The reason the index is maintained by triggers rather than on Fisher's write path.
    /// </summary>
    /// <remarks>
    ///     A trigger is a database-level object, so a writer that never went near Fisher — a
    ///     <c>QueueSqlCommand</c>, a transaction participant, another process holding the file — still
    ///     keeps the index in step. Verified by writing the row with raw SQL, which is as far outside
    ///     Fisher's write path as it is possible to get while still being the same database.
    /// </remarks>
    [Fact]
    public async Task a_write_that_bypasses_fisher_still_updates_the_index()
    {
        await using (var writing = _store.LightweightSession())
        {
            writing.QueueSqlCommand(
                "insert into fi_doc_note(id, data) values (?, ?)",
                Guid.NewGuid().ToString(),
                """{"heading":"Smuggled","text":"pangolin sightings"}""");

            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        (await session.Query<Note>().Where(x => x.PlainTextSearch("pangolin")).CountAsync(Token))
            .ShouldBe(1);
    }

    [Fact]
    public async Task the_index_can_be_rebuilt_and_checked()
    {
        await _store.Advanced.CheckFullTextIndexAsync<Article>(Token);
        await _store.Advanced.RebuildFullTextIndexAsync<Article>(Token);
        await _store.Advanced.CheckFullTextIndexAsync<Article>(Token);

        (await TitlesAsync(s => s.Query<Article>().Where(x => x.Search("fox")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox"]);
    }

    /// <summary>
    ///     An index declared on a store that already holds documents is populated when it is created.
    /// </summary>
    /// <remarks>
    ///     The triggers only fire on writes made after they exist, so without the <c>rebuild</c> in
    ///     the create statement this store would search an empty index and report no matches — an
    ///     answer with nothing wrong with it except that it is false.
    /// </remarks>
    [Fact]
    public async Task declaring_an_index_over_existing_rows_populates_it()
    {
        await using var database = TemporaryDatabase.Create("fulltext-existing");

        using (var before = DocumentStore.For(options =>
               {
                   options.ConnectionString = database.ConnectionString;
                   options.AutoCreateSchemaObjects = AutoCreate.All;
                   options.Schema.For<Article>();
               }))
        {
            await before.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = before.LightweightSession();
            session.Store(new Article { Title = "Historic pelicans", Body = "long before the index" });
            await session.SaveChangesAsync(Token);
        }

        using var after = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Article>().FullTextIndex(x => x.Title, x => x.Body);
        });

        await after.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var reading = after.QuerySession();

        (await reading.Query<Article>().Where(x => x.PlainTextSearch("pelicans")).CountAsync(Token))
            .ShouldBe(1);
    }

    [Fact]
    public async Task applying_the_configuration_again_is_a_no_op()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // The rebuild in the create statement is idempotent too, so nothing is doubled.
        (await TitlesAsync(s => s.Query<Article>().Where(x => x.Search("fox")).ToListAsync(Token)))
            .ShouldBe(["The quick brown fox"]);
    }

    [Fact]
    public async Task the_index_participates_in_schema_assertion()
    {
        // Nothing pending after a clean apply.
        await _store.Database.AssertDatabaseMatchesConfigurationAsync();
    }

    /// <summary>
    ///     Cleaning the documents empties the index with them, and does not touch the index directly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is why the FTS objects are named <c>fi_fts_*</c> rather than
    ///         <c>fi_doc_*_fts</c>.</b> <c>DeleteAllDocumentsAsync</c> discovers its targets by the
    ///         <c>fi_doc_</c> prefix, and an FTS5 virtual table brings four shadow tables — so under
    ///         the obvious name the sweep would issue <c>delete from</c> against the index and every
    ///         shadow of it. Verified against SQLite 3.50.4 that such a delete does <em>not</em> error,
    ///         which is what makes the naming a correctness rule rather than a preference.
    ///     </para>
    ///     <para>
    ///         What does clear the index is the row delete on the document table, through the trigger —
    ///         which is the same mechanism every other write uses, rather than a second one the cleaner
    ///         would have to remember.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task cleaning_the_documents_clears_the_index_through_the_triggers()
    {
        await _store.Advanced.Clean.DeleteAllDocumentsAsync(Token);

        await using var session = _store.QuerySession();

        (await session.Query<Article>().Where(x => x.Search("fox")).MaybeDeleted().CountAsync(Token))
            .ShouldBe(0);

        // Still a healthy index rather than one left in a state nothing checked.
        await _store.Advanced.CheckFullTextIndexAsync<Article>(Token);
    }

    [Fact]
    public async Task completely_removing_everything_leaves_no_full_text_objects_behind()
    {
        await _store.Advanced.Clean.CompletelyRemoveAllAsync(Token);

        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name from sqlite_master where name like 'fi\\_%' escape '\\' order by name";

        var left = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            left.Add(reader.GetString(0));
        }

        // The view is the one a table-only sweep would leave.
        left.ShouldBeEmpty();
    }

    // ---- composition ----

    [Fact]
    public async Task a_search_composes_with_other_predicates_and_with_paging()
    {
        await using var session = _store.QuerySession();

        var page = await session.Query<Article>()
            .Where(x => x.Search("the") && x.Author == "Aesop")
            .OrderBy(x => x.Title)
            .Take(1)
            .ToListAsync(Token);

        page.Single().Title.ShouldBe("The quick brown fox");

        (await session.Query<Article>().Where(x => x.Search("the") && x.Author == "Aesop")
            .CountAsync(Token)).ShouldBe(2);
    }

    [Fact]
    public async Task a_search_can_be_negated()
    {
        await using var session = _store.QuerySession();

        var titles = await session.Query<Article>()
            .Where(x => !x.Search("fox"))
            .ToListAsync(Token);

        titles.Select(x => x.Title).OrderBy(x => x)
            .ShouldBe(["Running with scissors", "Wombats of the southern hemisphere"]);
    }

    [Fact]
    public async Task an_empty_search_term_matches_nothing_rather_than_failing()
    {
        await using var session = _store.QuerySession();

        (await session.Query<Article>().Where(x => x.PlainTextSearch("   ")).CountAsync(Token))
            .ShouldBe(0);
    }

    // ---- refusals ----

    [Fact]
    public async Task searching_a_type_with_no_index_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Plain>().Where(x => x.Search("anything")).ToListAsync(Token));

        exception.Message.ShouldContain("declares no full-text index");
        exception.Message.ShouldContain("FullTextIndex");
    }

    [Fact]
    public async Task ngram_search_against_a_word_index_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Article>().Where(x => x.NgramSearch("ombat")).ToListAsync(Token));

        exception.Message.ShouldContain("Trigram tokenizer");
        exception.Message.ShouldContain("come back empty");
    }

    [Fact]
    public async Task a_word_operator_against_a_trigram_index_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Part>().Where(x => x.PrefixSearch("WID")).ToListAsync(Token));

        exception.Message.ShouldContain("Trigram");
        exception.Message.ShouldContain("NgramSearch");
    }

    [Fact]
    public async Task an_ngram_term_too_short_to_match_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Part>().Where(x => x.NgramSearch("ab")).ToListAsync(Token));

        exception.Message.ShouldContain("at least three characters");
    }

    [Fact]
    public async Task a_web_style_search_of_only_exclusions_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Article>().Where(x => x.WebStyleSearch("-fox")).ToListAsync(Token));

        exception.Message.ShouldContain("only exclusions");
    }

    [Fact]
    public async Task a_malformed_raw_search_reports_rather_than_matching_nothing()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<Exception>(() =>
            session.Query<Article>().Where(x => x.Search("\"unbalanced")).ToListAsync(Token));
    }

    [Fact]
    public void calling_a_search_operator_outside_a_query_throws()
    {
        var article = new Article();

        Should.Throw<NotSupportedException>(() => article.Search("fox"))
            .Message.ShouldContain("only meaningful inside a Fisher LINQ query");
    }

    [Fact]
    public void declaring_two_different_full_text_indexes_is_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Article>()
                .FullTextIndex(x => x.Title)
                .FullTextIndex(x => x.Body);
        }));

        exception.Message.ShouldContain("one per document type");
    }

    [Fact]
    public void declaring_the_same_index_twice_is_idempotent()
    {
        using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Article>()
                .FullTextIndex(x => x.Title, x => x.Body)
                .FullTextIndex(x => x.Title, x => x.Body);
        });
    }

    // ---- the attribute ----

    [Fact]
    public async Task the_attribute_declares_the_index()
    {
        await using var database = TemporaryDatabase.Create("fulltext-attribute");

        using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Recipe>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var writing = store.LightweightSession())
        {
            writing.Store(new Recipe
            {
                Name = "Cassoulet", Method = "Slowly, with beans", Notes = "not indexed"
            });
            await writing.SaveChangesAsync(Token);
        }

        await using var session = store.QuerySession();

        (await session.Query<Recipe>().Where(x => x.PlainTextSearch("beans")).CountAsync(Token))
            .ShouldBe(1);

        // Notes carries no attribute, so it is outside the index.
        (await session.Query<Recipe>().Where(x => x.PlainTextSearch("indexed")).CountAsync(Token))
            .ShouldBe(0);
    }

    [Fact]
    public void the_attribute_on_the_type_and_on_a_member_is_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Contradiction>();
        }));

        exception.Message.ShouldContain("use one or the other");
    }

    [Fact]
    public void two_attributes_disagreeing_about_the_tokenizer_are_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<MixedTokenizers>();
        }));

        exception.Message.ShouldContain("more than one tokenizer");
    }

    public class Article
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Author { get; set; } = "";
    }

    public class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Heading { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class Part
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
    }

    public class Plain
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
    }

    public class Recipe
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [FullTextIndex] public string Name { get; set; } = "";
        [FullTextIndex] public string Method { get; set; } = "";

        public string Notes { get; set; } = "";
    }

    [FullTextIndex]
    public class Contradiction
    {
        public Guid Id { get; set; }

        [FullTextIndex] public string Name { get; set; } = "";
    }

    public class MixedTokenizers
    {
        public Guid Id { get; set; }

        [FullTextIndex] public string Name { get; set; } = "";

        [FullTextIndex(Tokenizer = FullTextTokenizer.Trigram)]
        public string Code { get; set; } = "";
    }
}

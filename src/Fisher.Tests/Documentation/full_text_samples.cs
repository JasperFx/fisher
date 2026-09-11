using Fisher.Linq;
using Fisher.Storage.FullText;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/documents/querying/linq/full-text.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

public class SearchableArticle
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Author { get; set; } = "";
}

public static class full_text_samples
{
    public static void declare_index(StoreOptions opts)
    {
        #region sample_full_text_declare_index
        opts.Schema.For<SearchableArticle>().FullTextIndex(x => x.Title, x => x.Body);
        #endregion
    }

    public static void declare_trigram_index(StoreOptions opts)
    {
        #region sample_full_text_trigram_index
        opts.Schema.For<SearchableArticle>()
            .FullTextIndex(FullTextTokenizer.Trigram, x => x.Title);
        #endregion
    }

    public static async Task search(IQuerySession session)
    {
        #region sample_full_text_search
        var articles = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("quick brown fox"))
            .ToListAsync();
        #endregion

        _ = articles;
    }

    public static async Task search_composes(IQuerySession session)
    {
        #region sample_full_text_composes
        var page = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("wombat") && x.Author == "Aesop")
            .OrderBy(x => x.Title)
            .ToPagedListAsync(1, 20);
        #endregion

        _ = page;
    }

    public static async Task maintenance(DocumentStore store)
    {
        #region sample_full_text_maintenance
        await store.Advanced.CheckFullTextIndexAsync<SearchableArticle>();
        await store.Advanced.RebuildFullTextIndexAsync<SearchableArticle>();
        #endregion
    }

    public static async Task ranked(IQuerySession session)
    {
        #region sample_full_text_relevance
        var best = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("quick brown fox"))
            .OrderByRelevance()
            .Take(10)
            .ToListAsync();
        #endregion

        _ = best;
    }

    public static async Task ranked_with_weights(IQuerySession session)
    {
        #region sample_full_text_relevance_weights
        // The index declared Title then Body, so a Title hit counts for ten Body hits
        var best = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("wombat"))
            .OrderByRelevance(10.0, 1.0)
            .ThenByDescending(x => x.Author)
            .ToListAsync();
        #endregion

        _ = best;
    }

    public static async Task snippet(IQuerySession session)
    {
        #region sample_full_text_snippet
        var hits = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("corrosion"))
            .OrderByRelevance()
            .Select(x => new { x.Id, x.Title, Extract = x.Snippet() })
            .ToListAsync();

        // Extract: "…the <b>corrosion</b> on the lower hull was…"
        #endregion

        _ = hits;
    }

    public static async Task snippet_with_markers(IQuerySession session)
    {
        #region sample_full_text_snippet_markers
        var hits = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("corrosion"))
            .Select(x => new { x.Id, Extract = x.Snippet("[", "]", " … ", 16) })
            .ToListAsync();
        #endregion

        _ = hits;
    }

    public static async Task highlight(IQuerySession session)
    {
        #region sample_full_text_highlight
        var hits = await session.Query<SearchableArticle>()
            .Where(x => x.PlainTextSearch("corrosion"))
            .Select(x => new { x.Id, Title = x.Highlight("Title", "<mark>", "</mark>") })
            .ToListAsync();
        #endregion

        _ = hits;
    }
}

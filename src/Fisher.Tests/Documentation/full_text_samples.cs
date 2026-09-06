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
}

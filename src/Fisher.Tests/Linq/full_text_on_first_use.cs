using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#422 — a full-text index on a store that never ran the full migration.
/// </summary>
/// <remarks>
///     <para>
///         <b>fisher#74's asymmetry again, for one feature.</b> A document table is created on first use,
///         from the write path and the read path alike. The full-text index's content view, FTS5 table and
///         triggers came only from the full migration (<c>DocumentFeatureSchema</c>), because the
///         on-demand path built its own list of schema objects and the two had drifted. So
///         <c>Search(...)</c> worked on a migrated database and failed with
///         <c>no such table: fi_fts_&lt;alias&gt;</c> on a fresh one, which is the direction that passes
///         in development and fails on first deploy.
///     </para>
///     <para>
///         Nothing here calls <c>ApplyAllConfiguredChangesToDatabaseAsync</c>, which is the whole point:
///         every other full-text test class applies the schema in <c>InitializeAsync</c>, so none of them
///         could see this.
///     </para>
/// </remarks>
public class full_text_on_first_use : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fulltext-first-use");
    private readonly List<DocumentStore> _stores = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private DocumentStore StoreFor(AutoCreate autoCreate = AutoCreate.All, bool indexed = true)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = autoCreate;

            var mapping = options.Schema.For<FirstUseMemo>();
            if (indexed)
            {
                mapping.FullTextIndex(x => x.Title);
            }
        });

        _stores.Add(store);
        return store;
    }

    private static async Task StoreAsync(DocumentStore store, params string[] titles)
    {
        await using var session = store.LightweightSession();
        foreach (var title in titles)
        {
            session.Store(new FirstUseMemo { Title = title });
        }

        await session.SaveChangesAsync(Token);
    }

    private static async Task<int> SearchAsync(DocumentStore store, string terms)
    {
        await using var session = store.QuerySession();
        return await session.Query<FirstUseMemo>().Where(x => x.Search(terms)).CountAsync(Token);
    }

    /// <remarks>
    ///     The discriminating fact: the first write provisions the table, and has to provision the index
    ///     and its triggers with it, or the row is never indexed and the search fails outright.
    /// </remarks>
    [Fact]
    public async Task a_write_then_a_search_finds_the_document_with_no_migration()
    {
        var store = StoreFor();

        await StoreAsync(store, "Herring season opens", "Mackerel are running");

        (await SearchAsync(store, "herring")).ShouldBe(1);
    }

    /// <remarks>
    ///     The read path provisions too (fisher#74), so a search on a fresh store answers "nothing"
    ///     rather than failing — and a write after it is indexed by the triggers that read created.
    /// </remarks>
    [Fact]
    public async Task a_search_before_any_write_answers_empty_and_later_writes_are_indexed()
    {
        var store = StoreFor();

        (await SearchAsync(store, "herring")).ShouldBe(0);

        await StoreAsync(store, "Herring season opens");

        (await SearchAsync(store, "herring")).ShouldBe(1);
    }

    /// <remarks>
    ///     A table written before the index was declared: the first use under the new configuration adds
    ///     the index, and FTS5's <c>'rebuild'</c> on creation is what indexes the rows already there.
    ///     Without it they would be silently missing from every search.
    /// </remarks>
    [Fact]
    public async Task rows_written_before_the_index_existed_are_found_once_it_is_added()
    {
        await StoreAsync(StoreFor(indexed: false), "Herring season opens", "Mackerel are running");

        var indexed = StoreFor();

        (await SearchAsync(indexed, "mackerel")).ShouldBe(1);
    }

    /// <remarks>
    ///     <c>AutoCreate.None</c> still checks and declines (fisher#81), and now checks the index as well
    ///     as the table: a table applied without its index would otherwise pass the check and fail the
    ///     search with a raw <c>no such table</c>.
    /// </remarks>
    [Fact]
    public async Task auto_create_none_names_a_missing_index_rather_than_creating_it()
    {
        await StoreAsync(StoreFor(indexed: false), "Herring season opens");

        var store = StoreFor(AutoCreate.None);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => SearchAsync(store, "herring"));

        refusal.Message.ShouldContain("fi_fts_");
        refusal.Message.ShouldContain(nameof(FirstUseMemo));
        refusal.Message.ShouldContain("AutoCreate.None");
    }

    [Fact]
    public async Task auto_create_none_is_happy_once_the_index_has_been_applied()
    {
        await StoreFor().ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var store = StoreFor(AutoCreate.None);
        await StoreAsync(store, "Herring season opens");

        (await SearchAsync(store, "herring")).ShouldBe(1);
    }
}

public class FirstUseMemo
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

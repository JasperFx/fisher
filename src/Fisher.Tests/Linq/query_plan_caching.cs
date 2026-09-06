using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     The configuration-only halves of query construction are built once and reused — the select
///     list per storage, the <c>MemberFactory</c> per mapping and table alias (fisher#179).
/// </summary>
/// <remarks>
///     <para>
///         What these pin is not that the caching happened — a cache is invisible when it is right —
///         but that it is keyed on the things that actually vary. Both keys have a plausible wrong
///         answer that would look correct in a single-session test: caching the select list per
///         document type (it varies by tracking flavor, not by type) and caching one member factory
///         per type (the locators are qualified with the table alias, which a join changes).
///     </para>
/// </remarks>
public class query_plan_caching : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("query-plan-cache");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new CachedAngler { Id = "ana", Name = "Ana", Catches = 3 });
        session.Store(new CachedAngler { Id = "bo", Name = "Bo", Catches = 7 });
        session.Store(new CachedCatch { Id = "one", AnglerId = "ana", Species = "Trout" });
        session.Store(new CachedCatch { Id = "two", AnglerId = "bo", Species = "Pike" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <remarks>
    ///     <b>The select list belongs to the storage, not to the document type.</b> The query-only
    ///     flavor omits <c>id</c> where the writeable ones read it at column 0 — a contract with the
    ///     query-only selectors rather than an optimization — so a cache keyed on the mapping would
    ///     hand one of the two flavors the other's layout, and the rows would then be read from the
    ///     wrong ordinals rather than failing outright.
    /// </remarks>
    [Fact]
    public async Task the_select_list_is_per_storage_flavor_not_per_document_type()
    {
        await using var session = _store.QuerySession();

        // AdvancedSql resolves the query-only flavor directly; the LINQ path resolves the session's
        // own. Same document type, two select lists.
        var queryOnlyFields = session.AdvancedSql.SelectFieldsFor<CachedAngler>();
        var linqSql = session.ToSql(session.Query<CachedAngler>().Where(x => x.Catches > 1));

        queryOnlyFields.ShouldNotContain("id");
        linqSql.ShouldStartWith("select id, data from");

        // And the flavor the query resolved reads what it selected.
        var anglers = await session.Query<CachedAngler>()
            .Where(x => x.Catches > 1).OrderBy(x => x.Id).ToListAsync(TestContext.Current.CancellationToken);

        anglers.Select(x => x.Name).ShouldBe(["Ana", "Bo"]);
    }

    /// <remarks>
    ///     <b>The table alias is inside the locator, so it is part of the member factory's key.</b>
    ///     A cache holding one factory per document type would qualify an unjoined query's locators
    ///     with <c>outer_t</c> (or leave a joined query's unqualified) depending on which query ran
    ///     first — an ordering-dependent failure, which is the worst kind.
    /// </remarks>
    [Fact]
    public async Task an_unjoined_query_and_a_joined_one_get_their_own_locators()
    {
        await using var session = _store.QuerySession();

        var unjoined = session.ToSql(session.Query<CachedAngler>().Where(x => x.Catches > 1));

        var joined = session.ToSql(session.Query<CachedAngler>()
            .Join(session.Query<CachedCatch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Where(x => x.a.Catches > 1));

        unjoined.ShouldContain("json_extract(data,");
        unjoined.ShouldNotContain("outer_t.data");

        joined.ShouldContain("json_extract(outer_t.data,");

        // Run in the other order too: whichever query built the factory first, both are right.
        await using var reversed = _store.QuerySession();

        var joinedFirst = reversed.ToSql(reversed.Query<CachedAngler>()
            .Join(reversed.Query<CachedCatch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Where(x => x.a.Catches > 1));

        var unjoinedSecond = reversed.ToSql(reversed.Query<CachedAngler>().Where(x => x.Catches > 1));

        joinedFirst.ShouldBe(joined);
        unjoinedSecond.ShouldBe(unjoined);
    }

    /// <remarks>
    ///     A shared member factory is read from every session and every thread, so it has to be
    ///     immutable in fact and not merely in intent. Parallel queries of both shapes over one store,
    ///     asserting the answers rather than waiting for an exception — a test that watches for a
    ///     crash proves nothing about a data race.
    /// </remarks>
    [Fact]
    public async Task parallel_queries_over_one_store_agree()
    {
        var queries = Enumerable.Range(0, 16).Select(async i =>
        {
            await using var session = _store.QuerySession();

            if (i % 2 == 0)
            {
                var anglers = await session.Query<CachedAngler>()
                    .Where(x => x.Catches > 1)
                    .OrderBy(x => x.Id)
                    .ToListAsync(TestContext.Current.CancellationToken);

                anglers.Select(x => x.Name).ShouldBe(["Ana", "Bo"]);
            }
            else
            {
                var joined = await session.Query<CachedAngler>()
                    .Join(session.Query<CachedCatch>(), a => a.Id, c => c.AnglerId,
                        (a, c) => new { Angler = a.Name, c.Species })
                    .OrderBy(x => x.Species)
                    .ToListAsync(TestContext.Current.CancellationToken);

                joined.Select(x => x.Species).ShouldBe(["Pike", "Trout"]);
            }
        });

        await Task.WhenAll(queries);
    }
}

public class CachedAngler
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Catches { get; set; }
}

public class CachedCatch
{
    public string Id { get; set; } = string.Empty;
    public string AnglerId { get; set; } = string.Empty;
    public string Species { get; set; } = string.Empty;
}

using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     More than one join in a query — fisher#55.
/// </summary>
/// <remarks>
///     <para>
///         fisher#25 supported one join and refused a second by name. What made a second hard was never
///         the SQL — <c>Statement.Joins</c> was already a list and rendered in order — but that a second
///         join is written against the <em>shape</em> the first produced: <c>x =&gt; x.catch.WaterId</c>
///         names no document at all until that shape is resolved back to one. See <c>JoinShape</c>.
///     </para>
///     <para>
///         So the tests that matter here are the ones a two-table join could not have had: which table a
///         locator reads when <b>three</b> documents share a member name, a key that reaches through the
///         previous shape, and a chain whose joins are of different kinds. <c>Water</c> carries a
///         <c>Name</c> and a <c>Region</c> deliberately, so both traps are live in every test rather
///         than only in the ones named for them.
///     </para>
/// </remarks>
public class chained_joins : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("chained-joins");
    private DocumentStore _store = null!;

    private static readonly Guid FrodoId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid MerryId = Guid.NewGuid();

    private static readonly Guid BrandywineId = Guid.NewGuid();
    private static readonly Guid WithywindleId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Anglr>();
            options.Schema.For<Ctch>();
            options.Schema.For<Water>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        session.Store(new Water { Id = BrandywineId, Name = "Brandywine", Region = "Shire" });
        session.Store(new Water { Id = WithywindleId, Name = "Withywindle", Region = "Oldforest" });

        session.Store(new Anglr { Id = FrodoId, Name = "Frodo", Region = "Shire" });
        session.Store(new Anglr { Id = SamId, Name = "Sam", Region = "Shire" });
        session.Store(new Anglr { Id = MerryId, Name = "Merry", Region = "Buckland" });

        session.Store(new Ctch { AnglerId = FrodoId, WaterId = BrandywineId, Name = "Trout", Weight = 3 });
        session.Store(new Ctch { AnglerId = FrodoId, WaterId = WithywindleId, Name = "Pike", Weight = 11 });
        session.Store(new Ctch { AnglerId = SamId, WaterId = BrandywineId, Name = "Chub", Weight = 2 });

        // Merry's catch names a water that does not exist, so the second join has an unmatched row of
        // its own — which is how the two join kinds are told apart at the *second* rung rather than the
        // first.
        session.Store(new Ctch { AnglerId = MerryId, WaterId = Guid.NewGuid(), Name = "Roach", Weight = 1 });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IDocumentSession Session() => _store.LightweightSession();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task three_tables_joined_in_a_chain()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id,
                (x, w) => new { Angler = x.a.Name, Species = x.c.Name, Water = w.Name })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Species}/{x.Water}").OrderBy(x => x)
            .ShouldBe(["Frodo/Pike/Withywindle", "Frodo/Trout/Brandywine", "Sam/Chub/Brandywine"]);
    }

    /// <summary>
    ///     The same three tables through <c>GroupJoin</c>/<c>SelectMany</c>, which is what query syntax
    ///     emits and is the spelling whose intermediate shapes nest.
    /// </summary>
    /// <remarks>
    ///     <b>This is the shape that needed <c>JoinShape.Fold</c>.</b> The second join's intermediate
    ///     holds the first join's <em>result</em> rather than a document, so <c>y.y.a.Name</c> resolves
    ///     through two shapes — and without folding it lands on a member access over an anonymous-type
    ///     construction, which evaluates correctly in memory and is not something any member factory can
    ///     translate.
    /// </remarks>
    [Fact]
    public async Task three_tables_through_group_join_and_select_many()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .GroupJoin(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { x.a, c })
            .GroupJoin(session.Query<Water>(), y => y.c.WaterId, w => w.Id, (y, waters) => new { y, waters })
            .SelectMany(z => z.waters, (z, w) => new { Angler = z.y.a.Name, Species = z.y.c.Name, Water = w.Name })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Species}/{x.Water}").OrderBy(x => x)
            .ShouldBe(["Frodo/Pike/Withywindle", "Frodo/Trout/Brandywine", "Sam/Chub/Brandywine"]);
    }

    /// <summary>
    ///     An inner join, then a left one — so the second rung keeps a row the first rung matched and the
    ///     second could not.
    /// </summary>
    [Fact]
    public async Task a_left_join_after_an_inner_one()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .GroupJoin(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { x.a, c })
            .GroupJoin(session.Query<Water>(), y => y.c.WaterId, w => w.Id, (y, waters) => new { y, waters })
            .SelectMany(z => z.waters.DefaultIfEmpty(),
                (z, w) => new { Angler = z.y.a.Name, Species = z.y.c.Name, Water = w == null ? "none" : w.Name })
            .ToListAsync(Token);

        // Merry's Roach survives the first join and matches no water, which is exactly what the left
        // join on the second rung is for. An inner join there drops it — see the test above.
        rows.Select(x => $"{x.Angler}/{x.Species}/{x.Water}").OrderBy(x => x)
            .ShouldBe([
                "Frodo/Pike/Withywindle", "Frodo/Trout/Brandywine", "Merry/Roach/none", "Sam/Chub/Brandywine"
            ]);
    }

    /// <summary>
    ///     The aliasing trap, one level deeper than fisher#25 could reach it.
    /// </summary>
    /// <remarks>
    ///     All three documents have a <c>Name</c> and two of them a <c>Region</c>, so an unqualified —
    ///     or wrongly qualified — locator produces valid SQL that reads somebody else's column. The
    ///     answer here is only right if every one of the three locators carries its own alias, which is
    ///     what building them from a per-side <c>MemberFactory</c> guarantees rather than hopes for.
    /// </remarks>
    [Fact]
    public async Task a_member_name_all_three_share_reads_its_own_table()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id,
                (x, w) => new { AnglerName = x.a.Name, CatchName = x.c.Name, WaterName = w.Name })
            .ToListAsync(Token);

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => x.AnglerName != x.CatchName && x.CatchName != x.WaterName);

        rows.Select(x => x.AnglerName).OrderBy(x => x).Distinct().ShouldBe(["Frodo", "Sam"]);
        rows.Select(x => x.CatchName).OrderBy(x => x).ShouldBe(["Chub", "Pike", "Trout"]);
        rows.Select(x => x.WaterName).OrderBy(x => x).ShouldBe(["Brandywine", "Brandywine", "Withywindle"]);
    }

    /// <summary>
    ///     One predicate naming all three documents, which is the point of joining three of them.
    /// </summary>
    [Fact]
    public async Task a_predicate_after_the_join_can_name_all_three()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id, (x, w) => new { x.a, x.c, w })
            .Where(r => r.a.Region == "Shire" && r.c.Weight > 2 && r.w.Region == "Oldforest")
            .Select(r => r.c.Name)
            .ToListAsync(Token);

        // Only Frodo's Pike: Shire angler, over two pounds, caught outside the Shire.
        rows.ShouldBe(["Pike"]);
    }

    [Fact]
    public async Task ordering_by_a_member_of_the_third_table()
    {
        await using var session = Session();

        var rows = await session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id,
                (x, w) => new { Species = x.c.Name, Water = w.Name })
            .OrderByDescending(r => r.Water)
            .ThenBy(r => r.Species)
            .ToListAsync(Token);

        rows.Select(x => $"{x.Water}/{x.Species}")
            .ShouldBe(["Withywindle/Pike", "Brandywine/Chub", "Brandywine/Trout"]);
    }

    /// <summary>
    ///     The terminals serve a chain without knowing it is one, which is the dividend of the join
    ///     living on the ordinary statement rather than on a parallel one.
    /// </summary>
    [Fact]
    public async Task the_terminals_work_over_a_chain()
    {
        await using var session = Session();

        var joined = () => session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id, (x, w) => new { x.a, x.c, w });

        (await joined().CountAsync(Token)).ShouldBe(3);
        (await joined().AnyAsync(Token)).ShouldBeTrue();
        (await joined().Where(r => r.w.Name == "Withywindle").CountAsync(Token)).ShouldBe(1);
        (await joined().MaxAsync(r => r.c.Weight, Token)).ShouldBe(11);
        (await joined().OrderBy(r => r.c.Weight).Select(r => r.c.Name).ToListAsync(Token))
            .ShouldBe(["Chub", "Trout", "Pike"]);
    }

    /// <summary>
    ///     Three aliases in the rendered SQL, and the third numbered rather than named.
    /// </summary>
    /// <remarks>
    ///     <c>outer_t</c> and <c>inner_t</c> are kept for the first two sides rather than renumbered to
    ///     <c>t0</c>/<c>t1</c>: <c>ToSql</c> exists to be read, one join is overwhelmingly the common
    ///     case, and the two names say which side is which where a number does not.
    /// </remarks>
    [Fact]
    public void the_sql_carries_one_alias_per_side()
    {
        using var session = Session();

        var sql = session.ToSql(session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId, (a, c) => new { a, c })
            .Join(session.Query<Water>(), x => x.c.WaterId, w => w.Id, (x, w) => new { x.a, x.c, w }));

        sql.ShouldContain("from fi_doc_anglr outer_t");
        sql.ShouldContain("join fi_doc_ctch inner_t on outer_t.id = json_extract(inner_t.data, '$.anglerId')");
        sql.ShouldContain("join fi_doc_water inner_t2 on json_extract(inner_t.data, '$.waterId') = inner_t2.id");
        sql.ShouldContain("inner_t2.data");
    }

    /// <summary>
    ///     A key that reaches through the previous shape to something that is not a document member is
    ///     refused by name, rather than producing a locator against the wrong table.
    /// </summary>
    [Fact]
    public async Task a_second_join_key_that_names_no_document_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Anglr>()
            .Join(session.Query<Ctch>(), a => a.Id, c => c.AnglerId,
                (a, c) => new { a, c, Computed = a.Name + c.Name })
            .Join(session.Query<Water>(), x => x.Computed, w => w.Name, (x, w) => new { x.a, w });

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));

        exception.Message.ShouldContain("single document member");
    }

    public class Anglr
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
    }

    public class Ctch
    {
        public Guid Id { get; set; }
        public Guid AnglerId { get; set; }
        public Guid WaterId { get; set; }
        public string Name { get; set; } = "";
        public int Weight { get; set; }
    }

    public class Water
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
    }
}

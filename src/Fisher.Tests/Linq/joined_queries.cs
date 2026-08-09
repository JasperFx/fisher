using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     <c>GroupJoin(...).SelectMany(...)</c> across two document tables — fisher#25.
/// </summary>
/// <remarks>
///     <para>
///         The tests that matter are the ones about <em>which table</em> a locator reads and
///         <em>where</em> a condition sits. Both failure modes are silent: an unqualified locator
///         produces valid SQL that reads the wrong side when the two documents share a member name, and
///         an inner-side filter in the <c>WHERE</c> turns a left join back into an inner one, dropping
///         exactly the rows the left join exists to keep.
///     </para>
///     <para>
///         <c>Catch</c> is soft-deleted and <c>Vessel</c> is a hierarchy on purpose, so the implicit
///         filters and the sub-class resolution are exercised by the ordinary joins rather than only by
///         the tests named for them.
///     </para>
/// </remarks>
public class joined_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("joins");
    private DocumentStore _store = null!;

    private static readonly Guid FrodoId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid MerryId = Guid.NewGuid();
    private static readonly Guid PippinId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Angler>();
            options.Schema.For<Catch>().SoftDeleted();
            options.Schema.For<Vessel>().AddSubClass<Trawler>().AddSubClass<Dinghy>();
            options.Schema.For<TenantedAngler>().MultiTenanted();
            options.Schema.For<TenantedCatch>().MultiTenanted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Id = FrodoId, Name = "Frodo", Region = "Shire", Licence = 10 });
            session.Store(new Angler { Id = SamId, Name = "Sam", Region = "Shire", Licence = 20 });
            session.Store(new Angler { Id = MerryId, Name = "Merry", Region = "Buckland", Licence = 30 });
            session.Store(new Angler { Id = PippinId, Name = "Pippin", Region = "Buckland", Licence = 40 });

            session.Store(new Catch { AnglerId = FrodoId, Name = "Trout", Weight = 3, Rating = Rating.Poor });
            session.Store(new Catch { AnglerId = FrodoId, Name = "Pike", Weight = 11, Rating = Rating.Good });
            session.Store(new Catch { AnglerId = SamId, Name = "Chub", Weight = 2, Rating = Rating.Good });

            session.Store(new Trawler { AnglerId = FrodoId, Name = "Brandywine Belle", Nets = 4 });
            session.Store(new Dinghy { AnglerId = SamId, Name = "Gaffer", HasOars = true });

            await session.SaveChangesAsync(Token);
        }

        // Pippin's only catch is soft-deleted, so an inner join must not find him and a left join must
        // still return him with nothing attached.
        await using (var session = _store.LightweightSession())
        {
            var perch = new Catch { AnglerId = PippinId, Name = "Perch", Weight = 5 };
            session.Store(perch);
            await session.SaveChangesAsync(Token);

            session.Delete(perch);
            await session.SaveChangesAsync(Token);
        }

        foreach (var tenant in new[] { "shire", "bree" })
        {
            await using var session = _store.LightweightSession(tenant);
            session.Store(new TenantedAngler { Id = FrodoId, Name = $"{tenant}-angler" });
            session.Store(new TenantedCatch { AnglerId = FrodoId, Name = $"{tenant}-catch" });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IDocumentSession Session() => _store.LightweightSession();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the two join kinds ----

    [Fact]
    public async Task an_inner_join_returns_one_row_per_match()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name, c.Weight })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Species}/{x.Weight}").OrderBy(x => x)
            .ShouldBe(["Frodo/Pike/11", "Frodo/Trout/3", "Sam/Chub/2"]);
    }

    /// <summary>
    ///     An outer row with no match is dropped by an inner join and kept by a left one — which is what
    ///     <c>DefaultIfEmpty()</c> means and the only thing it changes.
    /// </summary>
    [Fact]
    public async Task a_left_join_keeps_an_outer_row_with_no_match()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches.DefaultIfEmpty(),
                (x, c) => new { Angler = x.a.Name, Species = c == null ? "none" : c.Name })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Species}").OrderBy(x => x)
            .ShouldBe(["Frodo/Pike", "Frodo/Trout", "Merry/none", "Pippin/none", "Sam/Chub"]);
    }

    /// <summary>
    ///     Query syntax is how a join is usually written, and it produces the same two calls the method
    ///     form does — a transparent identifier in place of the explicit anonymous type.
    /// </summary>
    [Fact]
    public async Task the_query_syntax_form_is_the_same_join()
    {
        await using var session = Session();

        var rows = await (from angler in session.Query<Angler>()
            join landed in session.Query<Catch>() on angler.Id equals landed.AnglerId into catches
            from landed in catches.DefaultIfEmpty()
            select new { angler.Name, Species = landed == null ? "none" : landed.Name }).ToListAsync(Token);

        rows.Count.ShouldBe(5);
        rows.Single(x => x.Name == "Merry").Species.ShouldBe("none");
    }

    /// <summary>
    ///     A plain <c>Join</c> — what query syntax emits when the <c>join</c> clause has no
    ///     <c>into</c>, and the way an inner join is usually written.
    /// </summary>
    /// <remarks>
    ///     The same join with no grouping step: its one result selector is already over the two
    ///     documents, which is the shape the <c>GroupJoin</c> pair has to be rewritten into.
    /// </remarks>
    [Fact]
    public async Task a_plain_join_is_the_inner_join_without_the_grouping_step()
    {
        await using var session = Session();

        var rows = await (from angler in session.Query<Angler>()
            join landed in session.Query<Catch>() on angler.Id equals landed.AnglerId
            orderby landed.Weight descending
            select new { angler.Name, Species = landed.Name, landed.Weight }).ToListAsync(Token);

        rows.Select(x => $"{x.Name}/{x.Species}").ShouldBe(["Frodo/Pike", "Frodo/Trout", "Sam/Chub"]);

        var method = await session.Query<Angler>()
            .Join(session.Query<Catch>(), a => a.Id, c => c.AnglerId,
                (a, c) => new JoinedRow(a.Name, c.Name, c.Weight))
            .CountAsync(Token);

        method.ShouldBe(3);
    }

    // ---- which table a locator reads ----

    /// <summary>
    ///     Both documents have a <c>Name</c>, so an unqualified <c>json_extract(data, '$.name')</c>
    ///     would be valid SQL reading whichever table SQLite resolved it against. The aliases are what
    ///     make each side mean itself.
    /// </summary>
    [Fact]
    public async Task a_member_both_sides_have_reads_its_own_table()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>().Where(x => x.Name == "Frodo")
            .GroupJoin(session.Query<Catch>().Where(c => c.Name == "Pike"),
                a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name })
            .ToListAsync(Token);

        rows.Count.ShouldBe(1);
        rows[0].Angler.ShouldBe("Frodo");
        rows[0].Species.ShouldBe("Pike");
    }

    /// <summary>
    ///     A predicate on the inner query is part of the join, not decoration. Polecat drops these
    ///     silently, which returns more rows than the caller asked for and looks correct.
    /// </summary>
    [Fact]
    public async Task the_inner_querys_predicate_is_applied()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>().Where(c => c.Weight > 5),
                a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, c.Weight })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Weight}").ShouldBe(["Frodo/11"]);
    }

    /// <summary>
    ///     And it still leaves a left join a left join: the predicate is in the <c>ON</c> clause, so an
    ///     outer row whose only inner rows were filtered out is kept rather than dropped.
    /// </summary>
    [Fact]
    public async Task an_inner_predicate_does_not_turn_a_left_join_into_an_inner_one()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>().Where(c => c.Weight > 5),
                a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches.DefaultIfEmpty(),
                (x, c) => new { Angler = x.a.Name, Weight = c == null ? 0 : c.Weight })
            .ToListAsync(Token);

        rows.Select(x => $"{x.Angler}/{x.Weight}").OrderBy(x => x)
            .ShouldBe(["Frodo/11", "Merry/0", "Pippin/0", "Sam/0"]);
    }

    /// <summary>
    ///     A <c>where</c> after the join clause, which is where query syntax puts one and is the shape
    ///     most joins are actually written in.
    /// </summary>
    /// <remarks>
    ///     It filters joined rows, so it may name either side — including both in one expression, which
    ///     is what needs a resolver that answers for two tables.
    /// </remarks>
    [Fact]
    public async Task a_predicate_after_the_join_can_name_either_side()
    {
        await using var session = Session();

        var rows = await (from angler in session.Query<Angler>()
            join landed in session.Query<Catch>() on angler.Id equals landed.AnglerId
            where angler.Region == "Shire" && landed.Weight > 2
            select new { angler.Name, Species = landed.Name }).ToListAsync(Token);

        rows.Select(x => $"{x.Name}/{x.Species}").OrderBy(x => x).ShouldBe(["Frodo/Pike", "Frodo/Trout"]);
    }

    /// <summary>
    ///     The method-syntax spelling names the projected result instead, and each member of it is
    ///     traced back through the result selector to the document member it came from.
    /// </summary>
    [Fact]
    public async Task a_predicate_over_the_projected_result_is_traced_back_to_its_document()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight))
            .Where(x => x.Weight > 2)
            .ToListAsync(Token);

        rows.Select(x => x.Species).OrderBy(x => x).ShouldBe(["Pike", "Trout"]);
    }

    /// <summary>
    ///     A post-join predicate is not the same as one on the inner query, and a left join is where
    ///     the difference shows: this one runs after the match, so it removes the unmatched outer rows
    ///     that <c>an_inner_predicate_does_not_turn_a_left_join_into_an_inner_one</c> keeps. Both are
    ///     what the caller wrote, and in memory both would behave the same way.
    /// </summary>
    [Fact]
    public async Task a_predicate_after_a_left_join_filters_the_joined_rows()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches.DefaultIfEmpty(),
                (x, c) => new JoinedRow(x.a.Name, c == null ? "none" : c.Name, c == null ? 0 : c.Weight))
            .Where(x => x.Angler != "Frodo")
            .ToListAsync(Token);

        rows.Select(x => x.Angler).OrderBy(x => x).ShouldBe(["Merry", "Pippin", "Sam"]);
    }

    // ---- the implicit filters, on both sides ----

    /// <summary>
    ///     A soft-deleted inner row is invisible to the join, and the outer row it belonged to survives
    ///     a left join. The second half is what pins the filter's placement: in the <c>WHERE</c> it
    ///     would take Pippin with it.
    /// </summary>
    [Fact]
    public async Task a_soft_deleted_inner_row_is_filtered_without_dropping_its_outer_row()
    {
        await using var session = Session();

        var inner = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name })
            .ToListAsync(Token);

        inner.ShouldNotContain(x => x.Angler == "Pippin");
        inner.ShouldNotContain(x => x.Species == "Perch");

        var left = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches.DefaultIfEmpty(),
                (x, c) => new { Angler = x.a.Name, Species = c == null ? "none" : c.Name })
            .ToListAsync(Token);

        left.Single(x => x.Angler == "Pippin").Species.ShouldBe("none");
    }

    /// <summary>
    ///     <c>MaybeDeleted()</c> on the inner query reaches the deleted row, which is what says the
    ///     inner side really is parsed rather than only filtered.
    /// </summary>
    [Fact]
    public async Task the_inner_query_can_ask_for_deleted_rows()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>().Where(a => a.Name == "Pippin")
            .GroupJoin(session.Query<Catch>().MaybeDeleted(),
                a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { c.Name })
            .ToListAsync(Token);

        rows.Select(x => x.Name).ShouldBe(["Perch"]);
    }

    /// <summary>
    ///     Both sides are tenant-scoped, and the leak this checks for is the one fisher#51 was: an inner
    ///     row of another tenant whose key matches an outer row of this one.
    /// </summary>
    [Fact]
    public async Task both_sides_of_a_join_are_scoped_to_the_tenant()
    {
        foreach (var tenant in new[] { "shire", "bree" })
        {
            await using var session = _store.LightweightSession(tenant);

            var rows = await session.Query<TenantedAngler>()
                .GroupJoin(session.Query<TenantedCatch>(), a => a.Id, c => c.AnglerId,
                    (a, catches) => new { a, catches })
                .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name })
                .ToListAsync(Token);

            rows.Select(x => $"{x.Angler}/{x.Species}").ShouldBe([$"{tenant}-angler/{tenant}-catch"]);
        }
    }

    /// <summary>
    ///     The inner document comes back as its real sub-class.
    /// </summary>
    /// <remarks>
    ///     This is what the offsetting reader buys: the inner side is materialized by its own storage's
    ///     selector, which resolves <c>doc_type</c>. Deserializing the <c>data</c> column directly —
    ///     which is what Polecat's join handler does — returns the base type for every row, quietly
    ///     missing whatever the sub-class added.
    /// </remarks>
    [Fact]
    public async Task a_joined_hierarchy_comes_back_as_its_sub_classes()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Vessel>(), a => a.Id, v => v.AnglerId, (a, vessels) => new { a, vessels })
            .SelectMany(x => x.vessels, (x, v) => new { Angler = x.a.Name, Vessel = v })
            .ToListAsync(Token);

        rows.Single(x => x.Angler == "Frodo").Vessel.ShouldBeOfType<Trawler>().Nets.ShouldBe(4);
        rows.Single(x => x.Angler == "Sam").Vessel.ShouldBeOfType<Dinghy>().HasOars.ShouldBeTrue();
    }

    /// <summary>
    ///     Narrowing the inner query to one sub-class adds its discriminator to the <c>ON</c> clause.
    /// </summary>
    [Fact]
    public async Task the_inner_query_can_be_one_sub_class_of_a_hierarchy()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>()
            .GroupJoin(session.Query<Trawler>(), a => a.Id, v => v.AnglerId, (a, vessels) => new { a, vessels })
            .SelectMany(x => x.vessels, (x, v) => new { Angler = x.a.Name, v.Name })
            .ToListAsync(Token);

        rows.Select(x => x.Angler).ShouldBe(["Frodo"]);
    }

    // ---- what else composes ----

    [Fact]
    public async Task a_whole_outer_document_can_be_projected()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>().Where(a => a.Name == "Sam")
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a, Species = c.Name })
            .ToListAsync(Token);

        rows.Count.ShouldBe(1);
        rows[0].Angler.Region.ShouldBe("Shire");
        rows[0].Species.ShouldBe("Chub");
    }

    [Fact]
    public async Task ordering_after_the_join_by_either_side()
    {
        await using var session = Session();

        var byInner = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name, c.Weight })
            .OrderByDescending(x => x.Weight)
            .ToListAsync(Token);

        byInner.Select(x => x.Species).ShouldBe(["Pike", "Trout", "Chub"]);

        var byOuter = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name, Species = c.Name })
            .OrderBy(x => x.Angler).ThenByDescending(x => x.Species)
            .ToListAsync(Token);

        byOuter.Select(x => $"{x.Angler}/{x.Species}")
            .ShouldBe(["Frodo/Trout", "Frodo/Pike", "Sam/Chub"]);
    }

    /// <summary>
    ///     Ordering the outer query before the join is the other spelling, and it survives the join.
    /// </summary>
    [Fact]
    public async Task ordering_before_the_join_still_orders_the_result()
    {
        await using var session = Session();

        var rows = await session.Query<Angler>().OrderBy(a => a.Name)
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Angler = x.a.Name })
            .ToListAsync(Token);

        rows.Select(x => x.Angler).ShouldBe(["Frodo", "Frodo", "Sam"]);
    }

    [Fact]
    public async Task counting_paging_and_any_over_a_join()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        (await query().CountAsync(Token)).ShouldBe(3);
        (await query().AnyAsync(Token)).ShouldBeTrue();

        (await query().OrderBy(x => x.Weight).Take(2).ToListAsync(Token))
            .Select(x => x.Species).ShouldBe(["Chub", "Trout"]);

        // A count over a paged join counts joined rows, not outer ones — the join has to survive being
        // wrapped in a subquery for that to be true. The page is deliberately larger than the three
        // joined rows and smaller than the four anglers, so dropping the join answers 4.
        (await query().Take(4).CountAsync(Token)).ShouldBe(3);

        var page = await query().OrderBy(x => x.Weight).ToPagedListAsync(2, 2, Token);
        page.TotalItemCount.ShouldBe(3);
        page.Select(x => x.Species).ShouldBe(["Pike"]);
    }

    [Fact]
    public async Task first_and_single_over_a_join()
    {
        await using var session = Session();

        var first = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight))
            .OrderBy(x => x.Weight).FirstAsync(Token);

        first!.Species.ShouldBe("Chub");

        var single = await session.Query<Angler>().Where(a => a.Name == "Sam")
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight))
            .SingleAsync(Token);

        single!.Species.ShouldBe("Chub");
    }

    /// <summary>
    ///     The rendered SQL, which is the cheapest way to see that both aliases and both sides' implicit
    ///     filters are where they should be.
    /// </summary>
    [Fact]
    public async Task the_sql_aliases_both_tables_and_filters_the_inner_side_in_the_on_clause()
    {
        await using var session = Session();

        var sql = session.ToSql(session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight)));

        sql.ShouldContain("from fi_doc_angler outer_t");
        sql.ShouldContain("join fi_doc_catch inner_t on outer_t.id = json_extract(inner_t.data, '$.anglerId')");
        sql.ShouldContain("and inner_t.is_deleted = 0");
        sql.ShouldContain("outer_t.data");
        sql.ShouldContain("inner_t.data");

        await Task.CompletedTask;
    }

    // ---- the scalar aggregates and Last (fisher#54) ----

    /// <summary>
    ///     An aggregate over a joined member is the same rewrite the post-join <c>Where</c> and
    ///     <c>OrderBy</c> go through — the selector names a member of the caller's result shape, which
    ///     is traced back to the document it came from and resolved against that side's table.
    /// </summary>
    [Fact]
    public async Task the_scalar_aggregates_over_an_inner_side_member()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        (await query().SumAsync(x => x.Weight, Token)).ShouldBe(16);
        (await query().MinAsync(x => x.Weight, Token)).ShouldBe(2);
        (await query().MaxAsync(x => x.Weight, Token)).ShouldBe(11);
        (await query().AverageAsync(x => x.Weight, Token)).ShouldBe(16 / 3d, 0.0001);
    }

    /// <summary>
    ///     An outer-side member is counted <em>once per matched row</em>, not once per outer document —
    ///     Frodo's licence is in the total twice because he has two catches. That is what a join means
    ///     rather than a Fisher quirk, and it is the number a caller aggregating a join has to expect.
    /// </summary>
    [Fact]
    public async Task an_aggregate_over_the_outer_side_counts_a_row_per_match()
    {
        await using var session = Session();

        var total = await session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { x.a.Licence, c.Weight })
            .SumAsync(x => x.Licence, Token);

        total.ShouldBe(40);
    }

    /// <summary>
    ///     Min and max only need the member to order, so a string member of either side is a real
    ///     answer — the same rule the unjoined aggregates follow.
    /// </summary>
    [Fact]
    public async Task min_and_max_over_a_string_member_of_either_side()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        (await query().MinAsync(x => x.Angler, Token)).ShouldBe("Frodo");
        (await query().MaxAsync(x => x.Angler, Token)).ShouldBe("Sam");
        (await query().MinAsync(x => x.Species, Token)).ShouldBe("Chub");
    }

    /// <summary>
    ///     A left join's unmatched rows contribute SQL NULL, which <c>sum</c> and <c>avg</c> skip and
    ///     <c>count</c> does not — so the two disagree about how many rows there are, correctly.
    /// </summary>
    [Fact]
    public async Task an_aggregate_over_a_left_join_skips_the_unmatched_rows()
    {
        await using var session = Session();

        // The selector names c.Weight directly, which a materializing read could not do on a left join
        // whose unmatched rows have no inner document — an aggregate reads the column instead of
        // invoking the selector, so the null-guard the other left-join tests carry is not needed here.
        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches.DefaultIfEmpty(), (x, c) => new { x.a.Name, c.Weight });

        (await query.CountAsync(Token)).ShouldBe(5);
        (await query.SumAsync(x => x.Weight, Token)).ShouldBe(16);
        (await query.AverageAsync(x => x.Weight, Token)).ShouldBe(16 / 3d, 0.0001);
    }

    /// <summary>
    ///     An aggregate over a paged join applies to the page, which needs the join carried into the
    ///     subquery — the same thing <c>CountAsync</c> needs and for the same reason.
    /// </summary>
    [Fact]
    public async Task an_aggregate_over_a_paged_join_aggregates_the_page()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        // The two lightest of the three catches — 2 and 3, not the 16 the whole join sums to.
        (await query().OrderBy(x => x.Weight).Take(2).SumAsync(x => x.Weight, Token)).ShouldBe(5);

        // The same page over an outer-side member: Sam's licence and Frodo's. Dropping either the join
        // or the alias from the subquery makes this "no such column" rather than a wrong number, which
        // is the one mercy of a qualified locator.
        var licences = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { x.a.Licence, c.Weight })
            .OrderBy(x => x.Weight).Take(2);

        (await licences.SumAsync(x => x.Licence, Token)).ShouldBe(30);
    }

    [Fact]
    public async Task last_over_a_join()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        (await query().OrderBy(x => x.Weight).LastAsync(Token))!.Species.ShouldBe("Pike");
        (await query().OrderByDescending(x => x.Weight).LastAsync(Token))!.Species.ShouldBe("Chub");

        (await session.Query<Angler>().Where(a => a.Name == "Merry")
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight))
            .OrderBy(x => x.Weight).LastOrDefaultAsync(Token)).ShouldBeNull();
    }

    /// <summary>
    ///     <c>Last</c> over a page is the last of <em>that page</em>, so the reversal goes on a statement
    ///     wrapping it rather than in place. A join cannot reuse the unjoined wrapper: its ordering
    ///     locators are qualified with a table alias that does not survive into the enclosing scope, so
    ///     the keys are carried out of the page as named columns instead.
    /// </summary>
    [Fact]
    public async Task last_over_a_paged_join_is_the_last_of_the_page()
    {
        await using var session = Session();

        Func<IQueryable<JoinedRow>> query = () => session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        // Chub 2, Trout 3, Pike 11. The last of the first two is Trout; reversing in place would say
        // Pike, which is the last of all three.
        (await query().OrderBy(x => x.Weight).Take(2).LastAsync(Token))!.Species.ShouldBe("Trout");

        // Ordering on the outer side through the same wrapper, and descending so the reversal is
        // exercised in both directions.
        (await query().OrderByDescending(x => x.Angler).ThenBy(x => x.Weight).Take(2).LastAsync(Token))!
            .Species.ShouldBe("Trout");
    }

    [Fact]
    public async Task last_over_a_join_without_an_ordering_is_refused_by_name()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.LastAsync(Token));

        exception.Message.ShouldContain("OrderBy");
    }

    // ---- what is refused, and by name ----

    [Fact]
    public async Task a_group_join_without_a_select_many_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId,
                (a, catches) => new { a.Name, Count = catches.Count() });

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));

        exception.Message.ShouldContain("SelectMany");
    }

    /// <summary>
    ///     A predicate over a member the join computed rather than read is refused, because its value
    ///     exists only after the row has been materialized.
    /// </summary>
    [Fact]
    public async Task filtering_by_a_computed_member_of_the_result_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Label = x.a.Name + "/" + c.Name })
            .Where(x => x.Label == "Frodo/Pike");

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));

        exception.Message.ShouldContain("Cannot translate the comparison");
    }

    /// <summary>
    ///     A result that asks about the group rather than about the matched row has nothing a join can
    ///     answer with — the flattening already happened.
    /// </summary>
    [Fact]
    public async Task projecting_the_group_itself_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { x.a.Name, Landed = x.catches.Count() });

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));

        exception.Message.ShouldContain("one row per match");
    }

    [Fact]
    public async Task ordering_the_inner_query_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>().OrderBy(c => c.Weight), a => a.Id, c => c.AnglerId,
                (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new JoinedRow(x.a.Name, c.Name, c.Weight));

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));

        exception.Message.ShouldContain("only filter");
    }

    [Fact]
    public async Task ordering_by_a_computed_member_of_the_result_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Label = x.a.Name + "/" + c.Name })
            .OrderBy(x => x.Label);

        await Should.ThrowAsync<BadLinqExpressionException>(() => query.ToListAsync(Token));
    }

    [Fact]
    public async Task an_aggregate_over_a_computed_member_of_the_result_is_refused()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { Doubled = c.Weight * 2 });

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            query.SumAsync(x => x.Doubled, Token));

        exception.Message.ShouldContain("member of one of the joined documents");
    }

    /// <summary>
    ///     The two guards are the resolved member's, not the path's, so they apply to a joined selector
    ///     exactly as they do to an unjoined one — see <c>aggregating_queries</c> for what each one is
    ///     protecting against.
    /// </summary>
    [Fact]
    public async Task the_aggregate_guards_still_apply_over_a_join()
    {
        await using var session = Session();

        var query = session.Query<Angler>()
            .GroupJoin(session.Query<Catch>(), a => a.Id, c => c.AnglerId, (a, catches) => new { a, catches })
            .SelectMany(x => x.catches, (x, c) => new { c.Rating });

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            query.SumAsync(x => (int)x.Rating, Token));

        exception.Message.ShouldContain("not a number");
    }

    public record JoinedRow(string Angler, string Species, int Weight);

    public enum Rating
    {
        Poor,
        Good
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public int Licence { get; set; }
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public Guid AnglerId { get; set; }
        public string Name { get; set; } = "";
        public int Weight { get; set; }
        public Rating Rating { get; set; }
    }

    public class Vessel
    {
        public Guid Id { get; set; }
        public Guid AnglerId { get; set; }
        public string Name { get; set; } = "";
    }

    public class Trawler : Vessel
    {
        public int Nets { get; set; }
    }

    public class Dinghy : Vessel
    {
        public bool HasOars { get; set; }
    }

    public class TenantedAngler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class TenantedCatch
    {
        public Guid Id { get; set; }
        public Guid AnglerId { get; set; }
        public string Name { get; set; } = "";
    }
}

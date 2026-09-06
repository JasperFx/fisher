using Fisher.Linq;
using Fisher.Linq.Includes;
using Fisher.Linq.SoftDeletes;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     <c>Include()</c> — fetching the documents a query's rows point at, fisher#204.
/// </summary>
/// <remarks>
///     <para>
///         The tests that matter are the ones about what happens when an include finds
///         <em>nothing</em>, because Fisher resolves an include with a second statement and every
///         failure mode of that shape looks identical from the caller's side: a list that stays empty.
///         So the mistyped identity, the projected query, the aggregate terminal and the join are all
///         pinned as refusals rather than as silence.
///     </para>
///     <para>
///         <c>Catch</c> is soft-deleted so the implicit filter is exercised by the ordinary includes
///         rather than only by the test named for it — a related document that has been deleted must
///         not come back, which is the same rule the primary query follows.
///     </para>
/// </remarks>
public class included_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("includes");
    private DocumentStore _store = null!;

    private static readonly Guid FrodoId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid MerryId = Guid.NewGuid();

    private static readonly Guid BelleId = Guid.NewGuid();
    private static readonly Guid GafferId = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Angler>();
            options.Schema.For<Boat>();
            options.Schema.For<Crew>();
            options.Schema.For<Catch>().SoftDeleted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Boat { Id = BelleId, Name = "Brandywine Belle", Berth = "Bucklebury" });
            session.Store(new Boat { Id = GafferId, Name = "Gaffer", Berth = "Hobbiton" });

            session.Store(new Crew { Name = "Bill", BoatId = BelleId });
            session.Store(new Crew { Name = "Ted", BoatId = BelleId });
            session.Store(new Crew { Name = "Hal", BoatId = GafferId });

            // Frodo and Sam share the Belle, so it must be included exactly once for the pair.
            session.Store(new Angler { Id = FrodoId, Name = "Frodo", BoatId = BelleId, Region = "Shire" });
            session.Store(new Angler { Id = SamId, Name = "Sam", BoatId = BelleId, Region = "Shire" });
            session.Store(new Angler { Id = MerryId, Name = "Merry", BoatId = GafferId, Region = "Buckland" });

            session.Store(new Catch { AnglerId = FrodoId, Name = "Trout", Weight = 3 });
            session.Store(new Catch { AnglerId = FrodoId, Name = "Pike", Weight = 11 });
            session.Store(new Catch { AnglerId = SamId, Name = "Chub", Weight = 2 });

            await session.SaveChangesAsync(Token);
        }

        // Merry's only catch is soft-deleted, so an include over it must not hand it back.
        await using (var deleting = _store.LightweightSession())
        {
            var perch = new Catch { AnglerId = MerryId, Name = "Perch", Weight = 5 };
            deleting.Store(perch);
            await deleting.SaveChangesAsync(Token);

            deleting.Delete(perch);
            await deleting.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    // ---- the single-value plan ----

    [Fact]
    public async Task an_include_with_a_callback_hands_over_each_related_document()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var anglers = await session.Query<Angler>()
            .Include<Angler, Boat>(x => x.BoatId, boats.Add)
            .OrderBy(x => x.Name)
            .ToListAsync(Token);

        anglers.Select(x => x.Name).ShouldBe(["Frodo", "Merry", "Sam"]);
        boats.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Brandywine Belle", "Gaffer"]);
    }

    /// <summary>
    ///     Two anglers share the Belle; it must arrive once.
    /// </summary>
    /// <remarks>
    ///     The destination takes one call per included <em>document</em>, not one per parent reference
    ///     — the contract Marten's temp-table join has by construction and Fisher has to get right by
    ///     deduplicating the identities it collected.
    /// </remarks>
    [Fact]
    public async Task a_related_document_two_rows_share_arrives_once()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        await session.Query<Angler>()
            .Where(x => x.Region == "Shire")
            .Include(x => x.BoatId, boats)
            .ToListAsync(Token);

        boats.Count.ShouldBe(1);
        boats[0].Name.ShouldBe("Brandywine Belle");
    }

    [Fact]
    public async Task an_include_can_sit_anywhere_in_the_chain()
    {
        await using var session = _store.QuerySession();

        var before = new List<Boat>();
        var after = new List<Boat>();

        await session.Query<Angler>()
            .Include(x => x.BoatId, before)
            .Where(x => x.Name == "Merry")
            .ToListAsync(Token);

        await session.Query<Angler>()
            .Where(x => x.Name == "Merry")
            .Include(x => x.BoatId, after)
            .ToListAsync(Token);

        before.Select(x => x.Name).ShouldBe(["Gaffer"]);
        after.Select(x => x.Name).ShouldBe(["Gaffer"]);
    }

    /// <summary>
    ///     The include follows the rows the query actually returned, not the rows it matched.
    /// </summary>
    [Fact]
    public async Task an_include_covers_the_page_rather_than_the_table()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var page = await session.Query<Angler>()
            .OrderBy(x => x.Name)
            .Take(1)
            .Include(x => x.BoatId, boats)
            .ToListAsync(Token);

        page.Single().Name.ShouldBe("Frodo");
        boats.Select(x => x.Name).ShouldBe(["Brandywine Belle"]);
    }

    [Fact]
    public async Task includes_resolve_for_the_first_single_and_last_families()
    {
        await using var session = _store.QuerySession();

        var first = new List<Boat>();
        var single = new List<Boat>();
        var last = new List<Boat>();

        await session.Query<Angler>().OrderBy(x => x.Name)
            .Include(x => x.BoatId, first).FirstAsync(Token);

        await session.Query<Angler>().Where(x => x.Name == "Merry")
            .Include(x => x.BoatId, single).SingleAsync(Token);

        await session.Query<Angler>().OrderBy(x => x.Name)
            .Include(x => x.BoatId, last).LastAsync(Token);

        first.Select(x => x.Name).ShouldBe(["Brandywine Belle"]);
        single.Select(x => x.Name).ShouldBe(["Gaffer"]);
        last.Select(x => x.Name).ShouldBe(["Brandywine Belle"]);
    }

    [Fact]
    public async Task a_query_matching_nothing_includes_nothing_without_failing()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var anglers = await session.Query<Angler>()
            .Where(x => x.Name == "Bilbo")
            .Include(x => x.BoatId, boats)
            .ToListAsync(Token);

        anglers.ShouldBeEmpty();
        boats.ShouldBeEmpty();
    }

    /// <summary>
    ///     An id source over a collection member fans out.
    /// </summary>
    [Fact]
    public async Task an_include_on_a_collection_member_fans_out()
    {
        await using var writing = _store.LightweightSession();
        writing.Store(new Angler
        {
            Name = "Fatty", BoatId = GafferId, Region = "Buckland",
            FavouriteBoatIds = [BelleId, GafferId]
        });
        await writing.SaveChangesAsync(Token);

        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        await session.Query<Angler>()
            .Where(x => x.Name == "Fatty")
            .Include(x => x.FavouriteBoatIds, boats)
            .ToListAsync(Token);

        boats.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Brandywine Belle", "Gaffer"]);
    }

    [Fact]
    public async Task an_include_can_be_narrowed_by_a_filter()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        await session.Query<Angler>()
            .Include(x => x.BoatId, boats, b => b.Berth == "Hobbiton")
            .ToListAsync(Token);

        boats.Select(x => x.Name).ShouldBe(["Gaffer"]);
    }

    [Fact]
    public async Task several_includes_on_one_query_all_resolve()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();
        var catches = new List<Catch>();

        await session.Query<Angler>()
            .Where(x => x.Region == "Shire")
            .Include(x => x.BoatId, boats)
            .Include<Angler, Guid, Catch>(x => x.Id, c => c.AnglerId, catches)
            .ToListAsync(Token);

        boats.Select(x => x.Name).ShouldBe(["Brandywine Belle"]);
        catches.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Chub", "Pike", "Trout"]);
    }

    // ---- the dictionary plan ----

    [Fact]
    public async Task a_dictionary_include_keys_by_the_related_document_identity()
    {
        await using var session = _store.QuerySession();

        var boats = new Dictionary<Guid, Boat>();

        await session.Query<Angler>()
            .Include(x => x.BoatId, boats)
            .ToListAsync(Token);

        boats.Count.ShouldBe(2);
        boats[BelleId].Name.ShouldBe("Brandywine Belle");
        boats[GafferId].Name.ShouldBe("Gaffer");
    }

    [Fact]
    public async Task a_dictionary_keyed_by_the_wrong_type_is_refused_at_the_call()
    {
        await using var session = _store.QuerySession();

        var boats = new Dictionary<string, Boat>();

        var exception = Should.Throw<NotSupportedException>(() =>
            session.Query<Angler>().Include(x => x.BoatId, boats));

        exception.Message.ShouldContain("identity is a 'Guid'");
    }

    // ---- the dictionary-of-list plan ----

    [Fact]
    public async Task a_dictionary_of_list_include_groups_by_the_mapping_member()
    {
        await using var session = _store.QuerySession();

        var catches = new Dictionary<Guid, List<Catch>>();

        var anglers = await session.Query<Angler>()
            .Where(x => x.Region == "Shire")
            .Include(x => x.Id, (Catch c) => c.AnglerId, catches)
            .OrderBy(x => x.Name)
            .ToListAsync(Token);

        anglers.Count.ShouldBe(2);
        catches[FrodoId].Select(x => x.Name).OrderBy(x => x).ShouldBe(["Pike", "Trout"]);
        catches[SamId].Select(x => x.Name).ShouldBe(["Chub"]);
    }

    /// <summary>
    ///     A key with no related documents is absent rather than present-and-empty.
    /// </summary>
    [Fact]
    public async Task a_dictionary_of_list_include_omits_a_key_with_no_matches()
    {
        await using var session = _store.QuerySession();

        var catches = new Dictionary<Guid, List<Catch>>();

        await session.Query<Angler>()
            .Include(x => x.Id, (Catch c) => c.AnglerId, catches)
            .ToListAsync(Token);

        catches.ShouldNotContainKey(MerryId);
    }

    // ---- the mapping direction ----

    [Fact]
    public async Task a_mapped_include_fetches_the_documents_pointing_back()
    {
        await using var session = _store.QuerySession();

        var crew = new List<Crew>();

        await session.Query<Boat>()
            .Where(x => x.Name == "Brandywine Belle")
            .Include(x => x.Id, (Crew c) => c.BoatId, crew)
            .ToListAsync(Token);

        crew.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Bill", "Ted"]);
    }

    [Fact]
    public async Task a_mapped_include_honours_its_filter()
    {
        await using var session = _store.QuerySession();

        var crew = new List<Crew>();

        await session.Query<Boat>()
            .Include(x => x.Id, (Crew c) => c.BoatId, crew, c => c.Name == "Hal")
            .ToListAsync(Token);

        crew.Select(x => x.Name).ShouldBe(["Hal"]);
    }

    // ---- the implicit filters the include shares with an ordinary query ----

    /// <summary>
    ///     A soft-deleted related document is not included.
    /// </summary>
    /// <remarks>
    ///     Free rather than implemented, and worth pinning for exactly that reason: the include runs
    ///     through <c>session.Query&lt;T&gt;()</c>, so the soft-delete, tenancy and hierarchy filters
    ///     apply to it the way they apply to any other read. An include that composed its own SQL would
    ///     be the third place all three have to be remembered.
    /// </remarks>
    [Fact]
    public async Task a_soft_deleted_related_document_is_not_included()
    {
        await using var session = _store.QuerySession();

        var catches = new List<Catch>();

        await session.Query<Angler>()
            .Where(x => x.Name == "Merry")
            .Include(x => x.Id, (Catch c) => c.AnglerId, catches)
            .ToListAsync(Token);

        catches.ShouldBeEmpty();

        // ... and it is still there when the query asks for it.
        var deleted = new List<Catch>();

        await session.Query<Angler>()
            .Where(x => x.Name == "Merry")
            .Include(x => x.Id, (Catch c) => c.AnglerId, deleted)
            .ToListAsync(Token);

        deleted.ShouldBeEmpty();

        (await session.Query<Catch>().IsDeleted().ToListAsync(Token))
            .Select(x => x.Name).ShouldBe(["Perch"]);
    }

    // ---- refusals ----

    [Fact]
    public async Task include_after_a_select_is_refused()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>()
                .Include(x => x.BoatId, boats)
                .Select(x => x.Name)
                .ToListAsync(Token));

        exception.Message.ShouldContain("Include() cannot follow a Select or a GroupBy");
    }

    [Fact]
    public async Task include_over_a_group_by_is_refused()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>()
                .Include(x => x.BoatId, boats)
                .GroupBy(x => x.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(Token));

        exception.Message.ShouldContain("Include() cannot follow a Select or a GroupBy");
    }

    [Fact]
    public async Task include_over_a_join_is_refused()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>()
                .Include(x => x.BoatId, boats)
                .Join(session.Query<Catch>(), a => a.Id, c => c.AnglerId,
                    (a, c) => new { a.Name, Species = c.Name })
                .ToListAsync(Token));

        exception.Message.ShouldContain("Include() cannot be combined with a join");
    }

    [Fact]
    public async Task include_on_a_count_is_refused()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>().Include(x => x.BoatId, boats).CountAsync(Token));

        exception.Message.ShouldContain("CountAsync");
        exception.Message.ShouldContain("come back empty");
    }

    [Fact]
    public async Task include_on_any_and_on_an_aggregate_is_refused()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        (await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>().Include(x => x.BoatId, boats).AnyAsync(Token)))
            .Message.ShouldContain("AnyAsync");

        (await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>().Include(x => x.BoatId, boats).SumAsync(x => x.Licence, Token)))
            .Message.ShouldContain("SumAsync");
    }

    /// <summary>
    ///     An identity of the wrong CLR type is refused rather than binding a parameter that matches
    ///     nothing.
    /// </summary>
    [Fact]
    public async Task an_id_source_of_the_wrong_type_is_refused_rather_than_matching_nothing()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        var exception = await Should.ThrowAsync<Fisher.Linq.BadLinqExpressionException>(() =>
            session.Query<Angler>()
                .Include(x => x.Region, boats)
                .ToListAsync(Token));

        exception.Message.ShouldContain("cannot match a 'String'");
        exception.Message.ShouldContain("'Guid' member of Boat");
    }

    [Fact]
    public void include_on_a_queryable_that_is_not_fishers_is_refused()
    {
        var boats = new List<Boat>();

        Should.Throw<NotSupportedException>(() =>
            new[] { new Angler() }.AsQueryable().Include(x => x.BoatId, boats));
    }

    /// <summary>
    ///     The provider is cached per session, so a second query must not inherit the first's includes.
    /// </summary>
    /// <remarks>
    ///     This is the reason the plans ride in the expression tree rather than on the provider, and it
    ///     is the bug a port of Marten's design would have produced here — Marten builds a provider per
    ///     <c>Query&lt;T&gt;()</c> call, Fisher builds one per session.
    /// </remarks>
    [Fact]
    public async Task includes_do_not_leak_into_the_next_query_on_the_same_session()
    {
        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        await session.Query<Angler>().Include(x => x.BoatId, boats).ToListAsync(Token);
        boats.Count.ShouldBe(2);

        await session.Query<Angler>().ToListAsync(Token);
        boats.Count.ShouldBe(2);
    }

    /// <summary>
    ///     More identities than fit one <c>in (...)</c>, so the chunking is exercised end to end.
    /// </summary>
    [Fact]
    public async Task an_include_over_more_identities_than_one_chunk_holds()
    {
        var boatIds = new List<Guid>();

        await using (var writing = _store.LightweightSession())
        {
            for (var i = 0; i < 1200; i++)
            {
                var boat = new Boat { Name = $"Hull {i}", Berth = "Grey Havens" };
                boatIds.Add(boat.Id);
                writing.Store(boat);
                writing.Store(new Angler
                {
                    Name = $"Sailor {i}", BoatId = boat.Id, Region = "Havens"
                });
            }

            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        var boats = new List<Boat>();

        await session.Query<Angler>()
            .Where(x => x.Region == "Havens")
            .Include(x => x.BoatId, boats)
            .ToListAsync(Token);

        boats.Count.ShouldBe(1200);
        boats.Select(x => x.Id).OrderBy(x => x).ShouldBe(boatIds.OrderBy(x => x));
    }

    public class Angler
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public int Licence { get; set; }
        public Guid BoatId { get; set; }
        public List<Guid> FavouriteBoatIds { get; set; } = [];
    }

    public class Boat
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Berth { get; set; } = "";
    }

    public class Crew
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public Guid BoatId { get; set; }
    }

    public class Catch
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AnglerId { get; set; }
        public string Name { get; set; } = "";
        public int Weight { get; set; }
    }
}

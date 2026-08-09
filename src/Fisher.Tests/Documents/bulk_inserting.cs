using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     <c>Advanced.BulkInsertAsync</c> — fisher#36.
/// </summary>
/// <remarks>
///     There is no <c>SqlBulkCopy</c> to reach for and none is needed: on SQLite the transaction
///     dominates the cost, so the statements are the ordinary ones. What is worth testing is therefore
///     the batching semantics rather than the writes — including the one that is a real limitation,
///     that a failure part way leaves earlier batches committed.
/// </remarks>
public class bulk_inserting : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("bulk");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Angler>();
            o.Schema.For<Numbered>();
            o.Schema.For<Perishable>().SoftDeleted();
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static List<Angler> Anglers(int count)
        => Enumerable.Range(1, count)
            .Select(i => new Angler { Id = Guid.NewGuid(), Name = $"angler-{i}", Catches = i })
            .ToList();

    [Fact]
    public async Task inserts_everything()
    {
        await _store.Advanced.BulkInsertAsync(Anglers(250), token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(250);
        (await session.Query<Angler>().SumAsync(x => x.Catches, Token)).ShouldBe(250 * 251 / 2);
    }

    [Fact]
    public async Task spans_batches()
    {
        await _store.Advanced.BulkInsertAsync(Anglers(25), batchSize: 4, token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(25);
    }

    [Fact]
    public async Task an_empty_set_does_nothing()
    {
        await Should.NotThrowAsync(() => _store.Advanced.BulkInsertAsync(new List<Angler>(), token: Token));
    }

    [Fact]
    public async Task inserts_only_fails_on_a_duplicate()
    {
        var anglers = Anglers(3);
        await _store.Advanced.BulkInsertAsync(anglers, token: Token);

        await Should.ThrowAsync<Exception>(() =>
            _store.Advanced.BulkInsertAsync(anglers, BulkInsertMode.InsertsOnly, token: Token));
    }

    [Fact]
    public async Task overwrite_existing_updates_in_place()
    {
        var anglers = Anglers(3);
        await _store.Advanced.BulkInsertAsync(anglers, token: Token);

        foreach (var angler in anglers)
        {
            angler.Name = "renamed";
        }

        await _store.Advanced.BulkInsertAsync(anglers, BulkInsertMode.OverwriteExisting, token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(3);
        (await session.Query<Angler>().ToListAsync(Token)).ShouldAllBe(x => x.Name == "renamed");
    }

    /// <summary>
    ///     Not atomic across batches, and that is the trade for not holding SQLite's single write lock
    ///     for the whole run. Pinned so it reads as a decision rather than being discovered.
    /// </summary>
    [Fact]
    public async Task a_failure_part_way_leaves_earlier_batches_committed()
    {
        var anglers = Anglers(6);
        await _store.Advanced.BulkInsertAsync(anglers.Take(1).ToList(), token: Token);

        // The duplicate is in the second batch of two, so the first batch commits and the second does not.
        var second = new List<Angler> { anglers[1], anglers[2], anglers[3], anglers[0] };

        await Should.ThrowAsync<Exception>(() =>
            _store.Advanced.BulkInsertAsync(second, batchSize: 2, token: Token));

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(3);
    }

    /// <summary>
    ///     Hi-Lo assigns ids inside the write, so a bulk insert of numeric-identified documents must
    ///     still produce unique, contiguous ids across every batch.
    /// </summary>
    [Fact]
    public async Task numeric_identities_stay_unique_across_batches()
    {
        var numbered = Enumerable.Range(1, 60).Select(_ => new Numbered()).ToList();

        await _store.Advanced.BulkInsertAsync(numbered, batchSize: 7, token: Token);

        await using var session = _store.LightweightSession();
        var ids = (await session.Query<Numbered>().ToListAsync(Token)).Select(x => x.Id).ToList();

        ids.Count.ShouldBe(60);
        ids.Distinct().Count().ShouldBe(60);
        ids.ShouldAllBe(x => x > 0);
    }

    // ---- IgnoreDuplicates (fisher#53) ----

    /// <summary>
    ///     The property that distinguishes this mode from <c>OverwriteExisting</c>: what was already
    ///     there is <em>unchanged</em>, not rewritten with the incoming version.
    /// </summary>
    [Fact]
    public async Task ignore_duplicates_inserts_the_new_and_leaves_the_stored_alone()
    {
        var stored = Anglers(3);
        await _store.Advanced.BulkInsertAsync(stored, token: Token);

        foreach (var angler in stored)
        {
            angler.Name = "renamed";
        }

        var feed = stored.Concat(Anglers(2)).ToList();

        await _store.Advanced.BulkInsertAsync(feed, BulkInsertMode.IgnoreDuplicates, token: Token);

        await using var session = _store.LightweightSession();
        var names = (await session.Query<Angler>().ToListAsync(Token)).Select(x => x.Name).ToList();

        names.Count.ShouldBe(5);
        names.Count(x => x == "renamed").ShouldBe(0);
        names.Count(x => x.StartsWith("angler-")).ShouldBe(5);
    }

    [Fact]
    public async Task ignore_duplicates_over_a_batch_that_is_entirely_new_or_entirely_existing()
    {
        var anglers = Anglers(4);

        await _store.Advanced.BulkInsertAsync(anglers, BulkInsertMode.IgnoreDuplicates, token: Token);
        await _store.Advanced.BulkInsertAsync(anglers, BulkInsertMode.IgnoreDuplicates, token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(4);
    }

    [Fact]
    public async Task ignore_duplicates_spans_batches()
    {
        var anglers = Anglers(20);
        await _store.Advanced.BulkInsertAsync(anglers.Take(8).ToList(), token: Token);

        await _store.Advanced.BulkInsertAsync(anglers, BulkInsertMode.IgnoreDuplicates, batchSize: 3,
            token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Angler>().CountAsync(Token)).ShouldBe(20);
    }

    /// <summary>
    ///     An integer identity is where the comparison has to normalise: the reader hands an INTEGER
    ///     column back as <c>long</c> while the identity is an <c>int</c>, and boxed those never compare
    ///     equal — so every document would look new and the second pass would fail on the primary key
    ///     rather than skipping.
    /// </summary>
    [Fact]
    public async Task ignore_duplicates_over_an_integer_identity()
    {
        var numbered = Enumerable.Range(1, 5).Select(_ => new Numbered()).ToList();
        await _store.Advanced.BulkInsertAsync(numbered, token: Token);

        numbered.ShouldAllBe(x => x.Id > 0);

        await _store.Advanced.BulkInsertAsync(numbered, BulkInsertMode.IgnoreDuplicates, token: Token);

        await using var session = _store.LightweightSession();
        (await session.Query<Numbered>().CountAsync(Token)).ShouldBe(5);
    }

    /// <summary>
    ///     A soft-deleted row still holds its primary key, so it is a duplicate even though no ordinary
    ///     load can see it. Probing through a filtered read would call it new and then collide.
    /// </summary>
    [Fact]
    public async Task a_soft_deleted_row_is_still_a_duplicate()
    {
        var perishable = new Perishable { Id = Guid.NewGuid(), Name = "milk" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(perishable);
            await session.SaveChangesAsync(Token);

            session.Delete(perishable);
            await session.SaveChangesAsync(Token);
        }

        await Should.NotThrowAsync(() => _store.Advanced.BulkInsertAsync(
            new List<Perishable> { perishable }, BulkInsertMode.IgnoreDuplicates, token: Token));

        await using var check = _store.LightweightSession();

        // Still exactly one row, and still deleted — the insert was skipped rather than reviving it.
        (await check.Query<Perishable>().MaybeDeleted().CountAsync(Token)).ShouldBe(1);
        (await check.Query<Perishable>().CountAsync(Token)).ShouldBe(0);
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Catches { get; set; }
    }

    public class Numbered
    {
        public int Id { get; set; }
    }

    public class Perishable
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}

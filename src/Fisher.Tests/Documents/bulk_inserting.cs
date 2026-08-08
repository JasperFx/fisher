using Fisher.Linq;
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
}

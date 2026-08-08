using Fisher.Linq;
using Fisher.Linq.Metadata;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     The query operators with no standard LINQ spelling — fisher#26.
/// </summary>
public class marker_operators : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("markers");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Angler>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Name = "Frodo", Rank = Rank.Novice, Flies = ["dun", "olive"] });
            session.Store(new Angler { Name = "Sam", Rank = Rank.Expert, Flies = [] });
            session.Store(new Angler { Name = "Merry", Rank = Rank.Journeyman, Flies = ["sedge"] });
            await session.SaveChangesAsync(Token);
        }

        // Enough separation that the two groups land in different milliseconds, which is
        // last_modified's resolution.
        await Task.Delay(50, Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Name = "Pippin", Rank = Rank.Novice, Flies = ["nymph"] });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IQuerySession Session() => _store.LightweightSession();

    [Fact]
    public async Task is_one_of_and_in()
    {
        await using var session = Session();

        (await session.Query<Angler>().Where(x => x.Rank.IsOneOf(Rank.Novice, Rank.Expert))
            .ToListAsync(Token)).Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(["Frodo", "Pippin", "Sam"]);

        (await session.Query<Angler>().Where(x => x.Name.In("Sam", "Merry"))
            .ToListAsync(Token)).Count.ShouldBe(2);

        (await session.Query<Angler>().Where(x => x.Name.IsOneOf(new List<string> { "Sam" }))
            .ToListAsync(Token)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task is_one_of_matching_nothing()
    {
        await using var session = Session();

        (await session.Query<Angler>().Where(x => x.Name.IsOneOf("Gandalf")).ToListAsync(Token))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     An absent member counts as empty: <c>json_array_length(null)</c> is NULL rather than 0, so
    ///     without the explicit null arm the row falls out of the result instead of matching.
    /// </summary>
    [Fact]
    public async Task is_empty_covers_both_the_empty_array_and_the_absent_member()
    {
        await using var session = Session();

        (await session.Query<Angler>().Where(x => x.Flies.IsEmpty()).ToListAsync(Token))
            .ShouldHaveSingleItem().Name.ShouldBe("Sam");

        (await session.Query<Angler>().Where(x => x.Unset.IsEmpty()).ToListAsync(Token))
            .Count.ShouldBe(4);
    }

    [Fact]
    public async Task object_equals()
    {
        await using var session = Session();

        (await session.Query<Angler>().Where(x => Equals(x.Name, "Merry")).ToListAsync(Token))
            .ShouldHaveSingleItem().Name.ShouldBe("Merry");
    }

    /// <summary>
    ///     <c>last_modified</c> holds <c>SqliteTimestamp</c>'s fixed-width UTC form, chosen so a string
    ///     comparison is an instant comparison — so this needs none of the <c>strftime</c>
    ///     normalisation a document's own <see cref="DateTimeOffset" /> member needs.
    /// </summary>
    /// <remarks>
    ///     <b>The boundary comes from the database's clock, not the test's.</b> An earlier version took
    ///     <c>DateTimeOffset.UtcNow</c> between the two writes and compared against that; it failed once
    ///     and then passed on every rerun, which is the worst possible signal. <c>last_modified</c> is
    ///     written by SQLite's <c>strftime('now')</c>, so a client-sampled bound compares two clocks
    ///     that are only incidentally the same one. Reading the stored value back removes the question
    ///     rather than making the window wider and hoping.
    /// </remarks>
    [Fact]
    public async Task modified_since_and_before()
    {
        var lastWrite = await StoredLastModified("Pippin");

        await using var session = Session();

        (await session.Query<Angler>().ModifiedSince(lastWrite).ToListAsync(Token))
            .ShouldHaveSingleItem().Name.ShouldBe("Pippin");

        (await session.Query<Angler>().ModifiedBefore(lastWrite).ToListAsync(Token))
            .Count.ShouldBe(3);

        (await session.Query<Angler>().ModifiedSince(lastWrite)
            .Where(x => x.Rank == Rank.Novice).CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>The <c>last_modified</c> SQLite actually wrote for one document.</summary>
    private async Task<DateTimeOffset> StoredLastModified(string name)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select last_modified from fi_doc_angler where json_extract(data,'$.name') = $name";
        command.Parameters.AddWithValue("$name", name);

        return DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync(Token))!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    /// <summary>
    ///     Fisher waits for the whole store rather than for the projections feeding the queried type —
    ///     stricter than Polecat, and it needs no type-to-shard map. With no daemon running and nothing
    ///     to catch up on, the wait returns immediately.
    /// </summary>
    [Fact]
    public async Task query_for_non_stale_data_runs_the_query()
    {
        await using var session = Session();

        (await session.Query<Angler>().QueryForNonStaleData(TimeSpan.FromSeconds(5))
            .CountAsync(Token)).ShouldBe(4);

        (await session.Query<Angler>().QueryForNonStaleData(TimeSpan.FromSeconds(5))
            .Where(x => x.Rank == Rank.Novice).OrderBy(x => x.Name).Take(1)
            .ToListAsync(Token)).ShouldHaveSingleItem().Name.ShouldBe("Frodo");
    }

    public enum Rank
    {
        Novice,
        Journeyman,
        Expert
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Rank Rank { get; set; }
        public string[] Flies { get; set; } = [];

        /// <summary>Never serialized, so <c>json_extract</c> yields SQL NULL for it.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string[] Unset { get; set; } = [];
    }
}

using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#372: a LINQ <c>Contains()</c> over more than 32,766 values threw
///     <c>SQLite Error 1: 'too many SQL variables'</c>, while <c>LoadManyAsync</c> over the same ids worked.
/// </summary>
/// <remarks>
///     <para>
///         The set was bound one parameter per value — <c>locator in (@p0, @p1, …)</c> — and SQLite caps a
///         statement at 32,766 variables. <c>LoadManyAsync</c> was never affected because it binds the ids
///         as ONE JSON array and unpacks it with <c>json_each</c>. A large set now takes the same shape.
///     </para>
///     <para>
///         <b>Both shapes have to answer alike</b>, so every fact compares a large set against the same
///         matches reached through a small one, and covers each kind of value the member conversions hand
///         the filter: text, a Guid id (lowercase text), an integer, a decimal (normalized to REAL), a
///         boolean (INTEGER 1/0) and an enum.
///     </para>
/// </remarks>
public class contains_over_many_values : IAsyncLifetime
{
    private const int MoreThanSqliteAllows = 40_000;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("contains-many");
    private DocumentStore _store = null!;
    private readonly List<Ledger> _stored = [];

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Ledger>();
            options.Schema.For<KeyedLedger>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        for (var i = 0; i < 10; i++)
        {
            var ledger = new Ledger
            {
                Id = Guid.NewGuid(),
                Name = $"ledger-{i}",
                Number = i,
                Balance = i + 0.25m,
                Closed = i % 2 == 0,
                Kind = (LedgerKind)(i % 3)
            };

            _stored.Add(ledger);
            session.Store(ledger);
            session.Store(new KeyedLedger { Id = $"doc-{i}", Name = $"doc-{i}" });
        }

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>The report, as filed: 40,000 string ids, 10 of them stored.</summary>
    [Fact]
    public async Task contains_over_more_values_than_sqlite_binds_returns_the_matches()
    {
        var ids = Enumerable.Range(0, MoreThanSqliteAllows).Select(i => $"doc-{i}").ToArray();

        await using var query = _store.QuerySession();

        var matched = await query.Query<KeyedLedger>().Where(x => ids.Contains(x.Id)).ToListAsync(Token);
        matched.Count.ShouldBe(10);

        // The read that always worked, for comparison.
        (await query.LoadManyAsync<KeyedLedger>(ids)).Count.ShouldBe(10);
    }

    [Fact]
    public async Task a_large_set_matches_what_a_small_one_matches_for_every_kind_of_value()
    {
        await using var query = _store.QuerySession();

        var names = Padded(_stored.Take(4).Select(x => x.Name), i => $"absent-{i}");
        var ids = Padded(_stored.Take(4).Select(x => x.Id), _ => Guid.NewGuid());
        var numbers = Padded(_stored.Take(4).Select(x => x.Number), i => 1_000 + i);
        var balances = Padded(_stored.Take(4).Select(x => x.Balance), i => 1_000.5m + i);

        (await query.Query<Ledger>().Where(x => names.Contains(x.Name)).CountAsync(Token)).ShouldBe(4);
        (await query.Query<Ledger>().Where(x => ids.Contains(x.Id)).CountAsync(Token)).ShouldBe(4);
        (await query.Query<Ledger>().Where(x => numbers.Contains(x.Number)).CountAsync(Token)).ShouldBe(4);
        (await query.Query<Ledger>().Where(x => balances.Contains(x.Balance)).CountAsync(Token)).ShouldBe(4);

        // The marker operator reaches the same filter from the other direction.
        (await query.Query<Ledger>().Where(x => x.Name.IsOneOf(names)).CountAsync(Token)).ShouldBe(4);
    }

    /// <summary>
    ///     A set that repeats a few values many times over: a boolean, an enum, and a null that must match
    ///     nothing, exactly as <c>in (null)</c> does.
    /// </summary>
    [Fact]
    public async Task booleans_enums_and_nulls_in_a_large_set_behave_as_in_a_small_one()
    {
        await using var query = _store.QuerySession();

        var closed = Enumerable.Repeat(true, 5_000).ToArray();
        (await query.Query<Ledger>().Where(x => closed.Contains(x.Closed)).CountAsync(Token))
            .ShouldBe(_stored.Count(x => x.Closed));

        var kinds = Enumerable.Repeat(LedgerKind.Savings, 5_000).ToArray();
        (await query.Query<Ledger>().Where(x => kinds.Contains(x.Kind)).CountAsync(Token))
            .ShouldBe(_stored.Count(x => x.Kind == LedgerKind.Savings));

        var withNulls = Enumerable.Repeat<string?>(null, 5_000).Append(_stored[0].Name).ToArray();
        (await query.Query<Ledger>().Where(x => withNulls.Contains(x.Name)).CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task a_small_set_is_still_bound_value_by_value_and_a_large_one_as_one_array()
    {
        await using var query = _store.QuerySession();

        var small = new[] { "a", "b" };
        var large = Enumerable.Range(0, 5_000).Select(i => $"n{i}").ToArray();

        query.ToSql(query.Query<Ledger>().Where(x => small.Contains(x.Name)))
            .ShouldContain("in (@p0, @p1)");

        var sql = query.ToSql(query.Query<Ledger>().Where(x => large.Contains(x.Name)));
        sql.ShouldContain("in (select value from json_each(@p0))");
        sql.ShouldNotContain("@p1");
    }

    private static T[] Padded<T>(IEnumerable<T> matches, Func<int, T> absent)
        => matches.Concat(Enumerable.Range(0, MoreThanSqliteAllows).Select(absent)).ToArray();
}

public enum LedgerKind
{
    Current,
    Savings,
    Loan
}

public class Ledger
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }
    public decimal Balance { get; set; }
    public bool Closed { get; set; }
    public LedgerKind Kind { get; set; }
}

public class KeyedLedger
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

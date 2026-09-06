using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Schema;

/// <summary>
///     The first write or read of a document type provisions that type's table and only that type's
///     table (fisher#174).
/// </summary>
/// <remarks>
///     <para>
///         This used to run <c>ApplyAllConfiguredChangesToDatabaseAsync</c> — a whole-database
///         migration — per type, so warming up T types re-introspected every object the store knows
///         about T times over. The delta is the fix; these tests pin its two observable consequences,
///         since a per-type migration and a whole-configuration one are indistinguishable from any
///         single type's point of view once both have run.
///     </para>
/// </remarks>
public class first_use_table_ensure : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("first-use-ensure");
    private DocumentStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            // Registered, so both are in the store's configuration from the start — which is what
            // makes "only the one that was used" a statement about the migration rather than about
            // which mappings happen to exist.
            options.Schema.For<FirstEnsured>();
            options.Schema.For<SecondEnsured>();
            options.Schema.For<ConcurrentlyEnsured>();
        });

        // Deliberately no ApplyAllConfiguredChangesToDatabaseAsync: every table below is created by
        // the first use of its own type, which is the path under test.
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<HashSet<string>> DocumentTableNamesAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText =
            "select name from sqlite_master where type = 'table' and name like 'fi=_doc=_%' escape '='";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <remarks>
    ///     The delta itself. A whole-configuration migration would have created every mapped type's
    ///     table as a side effect of the first one being written, which is the O(types × objects)
    ///     shape — so "only this one is here" is the assertion that tells the two apart.
    /// </remarks>
    [Fact]
    public async Task ensuring_one_type_creates_that_types_table_and_no_other()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new FirstEnsured { Id = "one" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tables = await DocumentTableNamesAsync();

        tables.ShouldContain("fi_doc_firstensured");
        tables.ShouldNotContain("fi_doc_secondensured");
    }

    /// <remarks>
    ///     And the second type still provisions itself on its own first use, which is what makes the
    ///     narrowing above safe rather than merely smaller.
    /// </remarks>
    [Fact]
    public async Task a_later_type_still_provisions_itself()
    {
        await using (var first = _store.LightweightSession())
        {
            first.Store(new FirstEnsured { Id = "one" });
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var second = _store.LightweightSession())
        {
            second.Store(new SecondEnsured { Id = "two" });
            await second.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tables = await DocumentTableNamesAsync();

        tables.ShouldContain("fi_doc_firstensured");
        tables.ShouldContain("fi_doc_secondensured");
    }

    /// <remarks>
    ///     <para>
    ///         Concurrent first use of one type has to coalesce. The cache used to hold the types
    ///         themselves, so <c>HashSet.Add</c> returning false was read as "already ensured" when it
    ///         meant "somebody else is ensuring it right now" — and the loser went straight on to
    ///         write against a table that did not exist yet, surfacing as <c>no such table</c>.
    ///     </para>
    ///     <para>
    ///         Racy by nature, so it is run over several fresh types to widen the window rather than
    ///         relying on one draw. It passes intermittently against the old shape rather than always
    ///         failing, which is exactly why the coalescing is not left to be inferred from the cache.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task concurrent_first_use_of_one_type_does_not_race()
    {
        var writes = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var session = _store.LightweightSession();
            session.Store(new ConcurrentlyEnsured { Id = $"racer-{i}" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await Task.WhenAll(writes);

        await using var query = _store.QuerySession();
        var all = await query.Query<ConcurrentlyEnsured>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(8);
    }
}

public class FirstEnsured
{
    public string Id { get; set; } = string.Empty;
}

public class SecondEnsured
{
    public string Id { get; set; } = string.Empty;
}

public class ConcurrentlyEnsured
{
    public string Id { get; set; } = string.Empty;
}

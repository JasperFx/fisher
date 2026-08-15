using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     A read against a document type nothing has ever written provisions its table and answers empty,
///     rather than failing with <c>no such table</c> (fisher#74).
/// </summary>
/// <remarks>
///     <para>
///         This is what every cold start does — resolve a cache before anything has populated it, look
///         up defaults nobody has overridden, list a collection on a fresh install. The old shape was
///         asymmetric in the worst direction: it worked on a warm database and failed on a fresh one,
///         so it passed in development and failed on first deploy.
///     </para>
///     <para>
///         The store here deliberately registers <em>nothing</em>. A type declared with
///         <c>Schema.For&lt;T&gt;()</c> gets its table from the migration and would pass either way,
///         which is exactly why the workaround in the issue was a hand-maintained list of ~30 types.
///     </para>
/// </remarks>
public class reading_before_the_first_write : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cold-read");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_query_over_an_unwritten_type_is_empty()
    {
        await using var session = _store.QuerySession();

        var all = await session.Query<ShardProgression>().ToListAsync(TestContext.Current.CancellationToken);

        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_filtered_query_over_an_unwritten_type_is_empty()
    {
        await using var session = _store.QuerySession();

        var matching = await session.Query<ShardProgression>()
            .Where(x => x.ShardName == "anything")
            .ToListAsync(TestContext.Current.CancellationToken);

        matching.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_load_over_an_unwritten_type_is_null()
    {
        await using var session = _store.QuerySession();

        (await session.LoadAsync<ShardProgression>("nothing", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task a_load_many_over_an_unwritten_type_is_empty()
    {
        await using var session = _store.QuerySession();

        (await session.LoadManyAsync<ShardProgression>(TestContext.Current.CancellationToken, "a", "b"))
            .ShouldBeEmpty();
    }

    /// <remarks>
    ///     The scalar terminals wrap the real statement as a subquery, so they exercise the walk down
    ///     the <c>Subquery</c> chain rather than the statement handed to the terminal.
    /// </remarks>
    [Fact]
    public async Task the_scalar_terminals_over_an_unwritten_type_answer_from_no_rows()
    {
        await using var session = _store.QuerySession();

        (await session.Query<ShardProgression>().AnyAsync(TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        (await session.Query<ShardProgression>().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(0);

        (await session.Query<ShardProgression>().Take(5)
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        (await session.Query<ShardProgression>()
            .SumAsync(x => x.Sequence, TestContext.Current.CancellationToken)).ShouldBe(0);

        (await session.Query<ShardProgression>()
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task check_exists_over_an_unwritten_type_is_false()
    {
        await using var session = _store.QuerySession();

        (await session.CheckExistsAsync<ShardProgression>("nothing", TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task a_json_read_over_an_unwritten_type_is_null()
    {
        await using var session = _store.QuerySession();

        (await session.LoadJsonAsync<ShardProgression>("nothing", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task metadata_for_an_unwritten_type_is_null()
    {
        await using var session = _store.QuerySession();

        (await session.MetadataForAsync<ShardProgression>("nothing", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <remarks>
    ///     Both sides of a join, and neither has ever been written. The inner side's table is
    ///     provisioned from <c>Statement.DocumentTypes</c>, which each join adds to.
    /// </remarks>
    [Fact]
    public async Task a_join_between_two_unwritten_types_is_empty()
    {
        await using var session = _store.QuerySession();

        var joined = await session.Query<ShardProgression>()
            .Join(session.Query<TraceProvider>(), x => x.ShardName, y => y.Id, (x, y) => new { x, y })
            .ToListAsync(TestContext.Current.CancellationToken);

        joined.ShouldBeEmpty();
    }

    /// <remarks>
    ///     <para>
    ///         <b>A read under <c>AutoCreate.None</c> provisions, because a write under
    ///         <c>AutoCreate.None</c> already does.</b> Both go through
    ///         <c>EnsureDocumentTableAsync</c>, and Weasel's explicit
    ///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c> deliberately upgrades <c>None</c> to
    ///         <c>CreateOrUpdate</c> — being the call that means "apply it". So this pins that reads and
    ///         writes agree rather than claiming a policy neither of them honours.
    ///     </para>
    ///     <para>
    ///         Whether the on-demand path <em>should</em> honour <c>AutoCreate.None</c> is a real
    ///         question — <c>HiloSequence</c> checks it explicitly and declines — but it is one about
    ///         both paths at once, and answering it here would leave a read stricter than a write.
    ///         Filed as fisher#81.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_read_and_a_write_agree_about_auto_create_none()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
        });

        await using var reading = store.QuerySession();

        (await reading.Query<NeverCreated>().ToListAsync(TestContext.Current.CancellationToken))
            .ShouldBeEmpty();

        await using var writing = store.LightweightSession();

        writing.Store(new NeverCreated { Id = "one" });
        await writing.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await reading.Query<NeverCreated>().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    /// <remarks>
    ///     Reading twice must not re-run the migration — <c>EnsureDocumentTableAsync</c> caches per
    ///     type, so the steady-state cost of the whole feature is a <c>HashSet</c> hit.
    /// </remarks>
    [Fact]
    public async Task the_table_is_provisioned_once_and_then_written_to_normally()
    {
        await using (var session = _store.LightweightSession())
        {
            (await session.Query<ShardProgression>().ToListAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();

            session.Store(new ShardProgression { Id = "one", ShardName = "Alpha:All", Sequence = 12 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();

        var all = await query.Query<ShardProgression>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(1);
        all[0].Sequence.ShouldBe(12);
    }
}

public class ShardProgression
{
    public string Id { get; set; } = string.Empty;
    public string ShardName { get; set; } = string.Empty;
    public long Sequence { get; set; }
}

public class TraceProvider
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class NeverCreated
{
    public string Id { get; set; } = string.Empty;
}

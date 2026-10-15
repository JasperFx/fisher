using Fisher.Internal;
using Fisher.Storage;
using Fisher.Storage.ClosedShape;
using JasperFx;
using JasperFx.Events.Projections;
using SQLitePCL;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#446, the marten#5671 class: a commit that only STARTS streams must not read the inline
///     snapshot table.
/// </summary>
/// <remarks>
///     <para>
///         The single-stream base asks projection storage only for the ids of streams being appended
///         to, so a start-only commit hands <c>LoadManyAsync</c> an empty array. Marten passed it
///         through and paid a round trip on <c>id = ANY($1)</c> that could only return no rows. Fisher
///         never did, because <c>FisherDocumentStorage.QueryManyAsync</c> short-circuits it, but that
///         was three layers below the method that depends on it.
///     </para>
///     <para>
///         <b>The positive cases matter as much as the zero cases.</b> They are what makes the
///         counting fail loudly if the SQL shape changes and the filter stops matching, instead of the
///         zero assertions passing vacuously.
///     </para>
/// </remarks>
public class inline_snapshot_reads_when_starting_streams : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("inline-snapshot-reads");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string SnapshotTable => _store.Options.Schema.MappingFor(typeof(QuestParty)).TableName.Name;

    private async Task<Guid> aPartyAsync(string name)
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<QuestParty>(id, new QuestStarted(name));
        await session.SaveChangesAsync(Token);
        return id;
    }

    /// <summary>
    ///     Runs <paramref name="act" /> on a session whose connection counts every SELECT against the
    ///     snapshot table, and commits it. SQLite's own per-statement hook, as
    ///     <c>fetching_many_for_writing</c> does, so nothing depends on which path logs what.
    /// </summary>
    private async Task<int> snapshotReadsDuringAsync(Action<IDocumentSession> act)
    {
        await using var session = _store.LightweightSession();
        var connection = await ((FisherSession)session).ConnectionAsync(Token);

        var reads = 0;
        var table = SnapshotTable;
        raw.sqlite3_trace(connection.Handle, new strdelegate_trace((_, statement) =>
        {
            if (statement.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
                && statement.Contains(table))
            {
                reads++;
            }
        }), null);

        try
        {
            act(session);
            await session.SaveChangesAsync(Token);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        }

        return reads;
    }

    [Fact]
    public async Task starting_a_stream_reads_nothing()
    {
        var id = Guid.NewGuid();

        var reads = await snapshotReadsDuringAsync(session =>
            session.Events.StartStream<QuestParty>(id, new QuestStarted("Find the ring"),
                new MonsterSlain("Balrog")));

        reads.ShouldBe(0);

        await using var query = _store.QuerySession();
        (await query.LoadAsync<QuestParty>(id, Token))!.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task starting_several_streams_reads_nothing()
    {
        var reads = await snapshotReadsDuringAsync(session =>
        {
            session.Events.StartStream<QuestParty>(Guid.NewGuid(), new QuestStarted("one"));
            session.Events.StartStream<QuestParty>(Guid.NewGuid(), new QuestStarted("two"));
            session.Events.StartStream<QuestParty>(Guid.NewGuid(), new QuestStarted("three"));
        });

        reads.ShouldBe(0);
    }

    [Fact]
    public async Task appending_to_an_existing_stream_reads_once()
    {
        var id = await aPartyAsync("Find the ring");

        var reads = await snapshotReadsDuringAsync(session =>
            session.Events.Append(id, new MonsterSlain("Shelob")));

        reads.ShouldBe(1);

        await using var query = _store.QuerySession();
        (await query.LoadAsync<QuestParty>(id, Token))!.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task a_start_and_an_append_in_one_commit_read_once()
    {
        var existing = await aPartyAsync("Find the ring");
        var started = Guid.NewGuid();

        var reads = await snapshotReadsDuringAsync(session =>
        {
            session.Events.StartStream<QuestParty>(started, new QuestStarted("Destroy the ring"));
            session.Events.Append(existing, new MonsterSlain("Witch-king"));
        });

        reads.ShouldBe(1);

        await using var query = _store.QuerySession();
        (await query.LoadAsync<QuestParty>(started, Token))!.Name.ShouldBe("Destroy the ring");
        (await query.LoadAsync<QuestParty>(existing, Token))!.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task starting_a_stream_whose_id_exists_still_fails_and_leaves_the_snapshot_alone()
    {
        var id = await aPartyAsync("Find the ring");

        await Should.ThrowAsync<Fisher.Exceptions.ExistingStreamIdCollisionException>(() =>
            snapshotReadsDuringAsync(session =>
                session.Events.StartStream<QuestParty>(id, new QuestStarted("Impostor"))));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<QuestParty>(id, Token))!.Name.ShouldBe("Find the ring");
    }

    /// <summary>
    ///     The projected loader opens a connection of its own, so the only way to observe that an empty
    ///     array never reaches it is a database that cannot be opened at all.
    /// </summary>
    [Fact]
    public async Task the_projected_load_many_does_not_open_a_connection_for_no_ids()
    {
        await using var unreachable = new FisherDatabase(_store.Options,
            $"Data Source={Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}", "x.db")};Mode=ReadOnly",
            "unreachable");

        var storage = (FisherDocumentStorage<QuestParty, Guid>)_store.Database.Providers
            .StorageFor<QuestParty>().Lightweight;

        var loaded = await storage.LoadManyProjectedAsync([], unreachable, StorageConstants.DefaultTenantId, Token);
        loaded.ShouldBeEmpty();

        // The premise: the same database fails as soon as there is something to read.
        await Should.ThrowAsync<Exception>(() =>
            storage.LoadManyProjectedAsync([Guid.NewGuid()], unreachable, StorageConstants.DefaultTenantId, Token));
    }
}

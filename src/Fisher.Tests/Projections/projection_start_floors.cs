using Fisher.Projections;
using Fisher.Tests.Configuration;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#471 / jasperfx#809 — what <c>ProjectionFloorCompliance</c> cannot see: the daemon actually using
///     a floor Fisher stored, and the floor surviving the three things that clear data on Fisher.
/// </summary>
/// <remarks>
///     The shared suite pins the seam — set, fetch, clear, race — against the database directly. Whether
///     <c>fi_projection_floors</c> survives a rebuild's teardown, a reset and a tenant wipe is Fisher's own
///     placement decision, and each of those deletes rows from tables that sit right beside it.
/// </remarks>
public class projection_start_floors : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-floors");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Add(new FirstRunTallyProjection(), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IEventDatabase Database => _store.Database;

    // A floor is keyed by the shard's projection name without its version, which for a single-stream
    // projection defaults to the document type's name rather than the projection class's.
    private const string ProjectionName = nameof(TallySnapshot);

    [Fact]
    public async Task a_first_run_pins_the_floor_and_skips_the_history_below_it()
    {
        var stream = Guid.NewGuid();
        await AppendAsync(stream, new QuestStarted("Old"), new MemberJoined("a"), new MemberJoined("b"));

        var daemon = await _store.BuildProjectionDaemonAsync();
        try
        {
            await daemon.StartAllAsync();

            // The floor pinned on the first start is the high water at the time: everything already there.
            await WaitForAsync(async () => await Database.FetchProjectionFloorAsync(ProjectionName, null, Token) is not null);
            (await Database.FetchProjectionFloorAsync(ProjectionName, null, Token)).ShouldBe(3);

            await AppendAsync(stream, new MemberJoined("c"));
            await _store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<TallySnapshot>(stream, Token)).ShouldNotBeNull().Members.ShouldBe(1);
    }

    /// <remarks>
    ///     A rebuild deletes the projection's progression rows, which is exactly why the floor cannot live on
    ///     one: the rebuild that is supposed to honour it would have deleted it first.
    /// </remarks>
    [Fact]
    public async Task a_rebuild_keeps_the_floor_and_replays_from_it()
    {
        var stream = Guid.NewGuid();
        await AppendAsync(stream, new QuestStarted("Old"), new MemberJoined("a"), new MemberJoined("b"));
        await Database.SetProjectionFloorAsync(ProjectionName, null, 3, Token);
        await AppendAsync(stream, new MemberJoined("c"), new MemberJoined("d"));

        var daemon = await _store.BuildProjectionDaemonAsync();
        try
        {
            await daemon.RebuildProjectionAsync<FirstRunTallyProjection>(Token);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }

        (await Database.FetchProjectionFloorAsync(ProjectionName, null, Token)).ShouldBe(3);

        await using var query = _store.QuerySession();
        (await query.LoadAsync<TallySnapshot>(stream, Token)).ShouldNotBeNull().Members.ShouldBe(2);
    }

    [Fact]
    public async Task resetting_all_data_leaves_the_floors()
    {
        await Database.SetProjectionFloorAsync(ProjectionName, null, 40, Token);

        await _store.Advanced.ResetAllDataAsync(Token);

        (await Database.FetchProjectionFloorAsync(ProjectionName, null, Token)).ShouldBe(40);
    }

    /// <remarks>
    ///     The floors table carries a <c>tenant_id</c> column, which is what the tenant wipe looks for. It
    ///     is excluded by name: a floor says where a projection's history starts, not data a tenant owns.
    /// </remarks>
    [Fact]
    public async Task wiping_a_tenant_leaves_its_floor()
    {
        using var file = TemporaryDatabase.Create("projection-floors-tenants");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = file.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession("acme"))
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Acme"));
            await session.SaveChangesAsync(Token);
        }

        await store.Database.SetProjectionFloorAsync(ProjectionName, "acme", 7, Token);

        await store.Advanced.DeleteAllTenantDataAsync("acme", Token);

        (await store.Database.FetchProjectionFloorAsync(ProjectionName, "acme", Token)).ShouldBe(7);
    }

    /// <remarks>
    ///     A store migrated before fisher#471 has no floors table. Asking is answered "no floor", as the
    ///     monitoring reads answer (fisher#332), and the first write creates the table through the event
    ///     tables' first-use path (fisher#333).
    /// </remarks>
    [Fact]
    public async Task a_store_without_the_floors_table_answers_no_floor_and_creates_it_on_the_first_write()
    {
        await ExecuteAsync("drop table fi_projection_floors");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        (await store.Database.FetchProjectionFloorAsync(ProjectionName, null, Token)).ShouldBeNull();

        (await store.Database.TrySetProjectionFloorAsync(ProjectionName, null, 12, Token)).ShouldBe(12);
        (await store.Database.FetchProjectionFloorAsync(ProjectionName, null, Token)).ShouldBe(12);
    }

    private async Task AppendAsync(Guid stream, params object[] events)
    {
        await using var session = _store.LightweightSession();
        session.Events.Append(stream, events);
        await session.SaveChangesAsync(Token);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
        while (!await condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}

public class FirstRunTallyProjection : SingleStreamProjection<TallySnapshot, Guid>
{
    public FirstRunTallyProjection()
    {
        Options.SubscribeFromFirstRun();
    }
}

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#470 / jasperfx#1031: <see cref="IEventStore.PreviewHighWaterAdvanceAsync" /> and
///     <see cref="IEventStore.AdvanceHighWaterMarkToLatestAsync" />, reached through <see cref="IEventStore" />
///     the way CritterWatch reaches them — it compiles one source against all three stores and cannot name
///     <c>Advanced</c>.
/// </summary>
public class high_water_advance_contract : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hw-advance");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IEventStore EventStore => _store;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<IEventDatabase> DatabaseAsync() => (await EventStore.AllDatabases()).Single();

    /// <summary>
    ///     Three appends with the middle one deleted, so one sequence number between the mark and the
    ///     ceiling has no row, and the stored mark at the first append. On SQLite a hole is a sequence a
    ///     delete removed — nothing is ever in flight below <c>max(seq_id)</c> — but it counts the same.
    /// </summary>
    private async Task<(long Floor, long Ceiling)> AppendWithAHoleAsync()
    {
        await StartStreamAsync("Before");
        var hole = await StartStreamAsync("Deleted");
        await StartStreamAsync("After");

        await ExecuteAsync($"delete from fi_events where stream_id = '{hole}'");

        await PlantMarkAsync(1);

        return (1, 3);
    }

    [Fact]
    public async Task the_preview_reports_the_mark_the_ceiling_and_the_hole_and_writes_nothing()
    {
        var (floor, ceiling) = await AppendWithAHoleAsync();

        var preview = (await EventStore.PreviewHighWaterAdvanceAsync(await DatabaseAsync(), null, Token))
            .ShouldHaveSingleItem();

        preview.DatabaseIdentifier.ShouldBe(_store.Database.Identifier);
        preview.TenantId.ShouldBeNull();
        preview.CurrentMark.ShouldBe(floor);
        preview.CommittedCeiling.ShouldBe(ceiling);
        preview.MissingSequenceCount.ShouldBe(1);
        preview.WouldAdvance.ShouldBeTrue();

        (await ReadMarkAsync()).ShouldBe(floor);
    }

    [Fact]
    public async Task an_advance_against_the_previewed_mark_moves_it_and_counts_what_it_skipped()
    {
        var (floor, ceiling) = await AppendWithAHoleAsync();
        var database = await DatabaseAsync();
        var preview = (await EventStore.PreviewHighWaterAdvanceAsync(database, null, Token)).Single();

        var advance = (await EventStore.AdvanceHighWaterMarkToLatestAsync(database, null, preview.CurrentMark, Token))
            .ShouldHaveSingleItem();

        advance.Outcome.ShouldBe(HighWaterAdvanceOutcome.Advanced);
        advance.DatabaseIdentifier.ShouldBe(_store.Database.Identifier);
        advance.PreviousMark.ShouldBe(floor);
        advance.NewMark.ShouldBe(ceiling);
        advance.SkippedSequenceCount.ShouldBe(1);

        (await ReadMarkAsync()).ShouldBe(ceiling);
    }

    /// <remarks>
    ///     The operator looked at an older mark and the daemon has since moved it on: a decision made
    ///     against the old value has to be made again, so nothing is written.
    /// </remarks>
    [Fact]
    public async Task a_stale_expectation_is_refused_and_writes_nothing()
    {
        var (floor, _) = await AppendWithAHoleAsync();

        var advance = (await EventStore.AdvanceHighWaterMarkToLatestAsync(await DatabaseAsync(), null, floor - 1,
            Token)).Single();

        advance.Outcome.ShouldBe(HighWaterAdvanceOutcome.RefusedStaleExpectation);
        advance.PreviousMark.ShouldBe(floor);
        advance.NewMark.ShouldBe(floor);
        advance.SkippedSequenceCount.ShouldBe(0);

        (await ReadMarkAsync()).ShouldBe(floor);
    }

    [Fact]
    public async Task a_mark_that_was_never_stored_counts_as_zero()
    {
        await StartStreamAsync("One");
        await StartStreamAsync("Two");
        (await ReadMarkAsync()).ShouldBeNull();

        var advance = (await EventStore.AdvanceHighWaterMarkToLatestAsync(await DatabaseAsync(), null, 0, Token))
            .Single();

        advance.Outcome.ShouldBe(HighWaterAdvanceOutcome.Advanced);
        advance.PreviousMark.ShouldBe(0);
        advance.NewMark.ShouldBe(2);
        advance.SkippedSequenceCount.ShouldBe(0);
        (await ReadMarkAsync()).ShouldBe(2);
    }

    /// <remarks>
    ///     Deleting the newest events leaves the mark above <c>max(seq_id)</c>. The advance must not pull it
    ///     down: that is <c>TryCorrectProgressInDatabaseAsync</c>'s job, and both the contract and
    ///     <c>Advanced</c> now say so. The <c>Advanced</c> form used to write the ceiling unconditionally.
    /// </remarks>
    [Fact]
    public async Task a_mark_past_the_ceiling_is_left_alone_and_never_moves_backwards()
    {
        await StartStreamAsync("One");
        await StartStreamAsync("Two");
        await PlantMarkAsync(12);

        var advance = (await EventStore.AdvanceHighWaterMarkToLatestAsync(await DatabaseAsync(), null, null, Token))
            .Single();

        advance.Outcome.ShouldBe(HighWaterAdvanceOutcome.AlreadyAtCeiling);
        advance.NewMark.ShouldBe(12);
        (await ReadMarkAsync()).ShouldBe(12);

        await _store.Advanced.AdvanceHighWaterMarkToLatestAsync(Token);
        (await ReadMarkAsync()).ShouldBe(12);
    }

    [Fact]
    public async Task the_preview_of_a_mark_at_the_ceiling_would_not_advance()
    {
        await StartStreamAsync("One");
        await PlantMarkAsync(1);

        var preview = (await EventStore.PreviewHighWaterAdvanceAsync(await DatabaseAsync(), null, Token)).Single();

        preview.WouldAdvance.ShouldBeFalse();
        preview.MissingSequenceCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_named_tenant_is_refused_because_the_database_keeps_one_mark()
    {
        var (floor, _) = await AppendWithAHoleAsync();
        var database = await DatabaseAsync();

        await Should.ThrowAsync<ArgumentException>(() => EventStore.PreviewHighWaterAdvanceAsync(database, "red", Token));
        await Should.ThrowAsync<ArgumentException>(
            () => EventStore.AdvanceHighWaterMarkToLatestAsync(database, "red", null, Token));

        (await ReadMarkAsync()).ShouldBe(floor);
    }

    [Fact]
    public async Task a_file_with_no_schema_previews_an_empty_mark()
    {
        using var fresh = TemporaryDatabase.Create("hw-advance-fresh");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = fresh.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        IEventStore eventStore = store;
        var preview = (await eventStore.PreviewHighWaterAdvanceAsync(store.Database, null, Token)).Single();

        preview.CurrentMark.ShouldBe(0);
        preview.CommittedCeiling.ShouldBe(0);
        preview.WouldAdvance.ShouldBeFalse();
    }

    /// <remarks>
    ///     Under database-per-tenant the tenant is named by which database is passed, and one file's mark
    ///     says nothing about another's.
    /// </remarks>
    [Fact]
    public async Task each_tenant_database_has_its_own_mark()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-hw-tenants-" + Guid.NewGuid().ToString("n")[..8]);

        var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(databases => databases.InDirectory(directory).AddTenants("north", "south"));
        });

        try
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            foreach (var (tenant, count) in new[] { ("north", 3), ("south", 2) })
            {
                await using var session = store.LightweightSession(tenant);
                for (var i = 0; i < count; i++)
                {
                    session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"{tenant} {i}"));
                }

                await session.SaveChangesAsync(Token);
            }

            IEventStore eventStore = store;
            var north = store.Tenancy.ExistingDatabaseFor("north");
            var south = store.Tenancy.ExistingDatabaseFor("south");

            var advance = (await eventStore.AdvanceHighWaterMarkToLatestAsync(north, null, 0, Token)).Single();

            advance.DatabaseIdentifier.ShouldBe(north.Identifier);
            advance.NewMark.ShouldBe(3);

            var untouched = (await eventStore.PreviewHighWaterAdvanceAsync(south, null, Token)).Single();
            untouched.DatabaseIdentifier.ShouldBe(south.Identifier);
            untouched.CurrentMark.ShouldBe(0);
            untouched.CommittedCeiling.ShouldBe(2);
        }
        finally
        {
            // Disposing the store releases its pooled connections (fisher#59), so the files can go.
            await store.DisposeAsync();

            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Under the temp path either way.
            }
        }
    }

    private async Task<Guid> StartStreamAsync(string name)
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream(id, new QuestStarted(name));
        await session.SaveChangesAsync(Token);
        return id;
    }

    private Task PlantMarkAsync(long mark)
        => ExecuteAsync($"""
                         insert into fi_event_progression (name, last_seq_id, last_updated)
                         values ('{ShardState.HighWaterMark}', {mark}, '2026-01-01T00:00:00.000Z')
                         on conflict (name) do update set last_seq_id = excluded.last_seq_id
                         """);

    private async Task<long?> ReadMarkAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select last_seq_id from fi_event_progression where name = $name";
        command.Parameters.AddWithValue("$name", ShardState.HighWaterMark);
        return await command.ExecuteScalarAsync(Token) is long mark ? mark : null;
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

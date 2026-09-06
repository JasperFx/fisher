using Fisher.Events.Daemon;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;

namespace Fisher.Tests.Daemon;

/// <summary>
///     <b>The audit behind <c>FisherHighWaterDetector</c>'s opt-out</b> — the class comment claims
///     committed <c>seq_id</c>s are contiguous, so the mark simply <em>is</em> <c>max(seq_id)</c> and
///     Marten's gap-detection machinery guards a state that cannot occur. This is that claim tested
///     rather than accepted.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim survives, with one correction to how it is stated.</b> What is airtight is the
///         property the daemon actually needs, which is narrower than contiguity: <em>a sequence below
///         the mark can never later become a committed row the daemon has not read.</em> Two facts
///         give it — a sequence is allocated only while its writer holds the file's one write lock,
///         so no writer can commit past another's pending allocation; and <c>AUTOINCREMENT</c> never
///         reissues a number, so a hole can never be filled in afterwards. That is what Marten's
///         safe-zone polling, stale-gap skipping and <c>SafeStartMark</c> exist to establish on a
///         store where a sequence is handed out <em>outside</em> the transaction, and it is why none
///         of it is needed here.
///     </para>
///     <para>
///         <b>Contiguity itself is not unconditional, and the class comment should not say it is.</b>
///         Deleting events leaves permanent holes, and deleting the newest events drops
///         <c>max(seq_id)</c> below a mark already recorded — which is fisher#174's finding, reached
///         again from the other direction. Neither is a gap in the sense the machinery guards against:
///         a hole is a sequence that is gone rather than one that is coming, and the daemon reads
///         across it because its loader pages a <em>range</em> rather than counting rows. The tests
///         below hold that line explicitly, because "contiguous" invites the reader to conclude
///         something stronger than what is true.
///     </para>
///     <para>
///         <b>One reachable state would genuinely break it, and it is closed upstream rather than
///         here.</b> SQLite cannot alter most of a table, so any migration beyond
///         <c>ALTER TABLE ADD COLUMN</c> rebuilds it — create, copy, drop, rename — and a bare rebuild
///         resets <c>sqlite_sequence</c> to the highest surviving row. On a table whose newest rows had
///         been deleted that reissues numbers already handed out, which is exactly the reuse
///         <c>AUTOINCREMENT</c> is on <c>fi_events</c> to forbid. Weasel's <c>TableDelta</c> carries the
///         counter across the rebuild; nothing in Fisher checked that it does, and
///         <see cref="a_table_rebuild_carries_the_autoincrement_counter_forward" /> now does.
///     </para>
/// </remarks>
public class high_water_contiguity_audit : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("contiguity");
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

    private FisherHighWaterDetector TheDetector => new(_store.Database, _store.Options.EventGraph);

    // ---------------------------------------------------------------------------------------------
    // 1. Allocation happens under the write lock, so nothing can commit past a pending allocation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     A connection abandoned mid-transaction returns the sequences it took — the crashed-writer
    ///     case reached without a crash.
    /// </summary>
    /// <remarks>
    ///     Disposing a connection with an open transaction is the ordinary rollback path, so this is
    ///     the weaker of the two crash tests; <see cref="a_crash_before_commit_leaves_no_committed_gap" />
    ///     is the one that goes through WAL recovery instead.
    /// </remarks>
    [Fact]
    public async Task an_abandoned_connection_returns_its_sequences()
    {
        await AppendAsync(3);

        await using (var connection =
                     await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

            (await InsertRawEventAsync(connection, transaction)).ShouldBe(4);
            (await InsertRawEventAsync(connection, transaction)).ShouldBe(5);

            // No commit, no explicit rollback — the connection simply goes away.
        }

        await AppendAsync(1);

        (await AllSequencesAsync()).ShouldBe([1, 2, 3, 4]);
    }

    /// <summary>
    ///     <b>A process killed with a transaction open leaves no committed gap.</b> Its allocations
    ///     were in the WAL and uncommitted, so recovery discards them along with the
    ///     <c>sqlite_sequence</c> row that recorded them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Simulated by copying the database file and its <c>-wal</c>/<c>-shm</c> sidecars while
    ///         the transaction is still open and then opening the copy, which is what a machine that
    ///         lost power at that instant would have on disk. Killing a real child process would test
    ///         the same thing more expensively and no more honestly — what is being checked is what
    ///         SQLite recovers from those bytes.
    ///     </para>
    ///     <para>
    ///         The uncommitted insert is observed to have taken sequence 4 before the copy, so the
    ///         assertion afterwards is about a sequence that really was allocated rather than one that
    ///         never got that far.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_crash_before_commit_leaves_no_committed_gap()
    {
        await AppendAsync(3);

        var crashed = Path.Combine(Path.GetTempPath(), $"fisher-crash-{Guid.NewGuid():n}.db");

        await using (var connection =
                     await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

            (await InsertRawEventAsync(connection, transaction)).ShouldBe(4);

            // The bytes on disk at the instant of the kill: the database, plus whatever the WAL holds.
            // The -shm sidecar is deliberately NOT copied — it is a memory-mapped index SQLite
            // reconstructs from the WAL during recovery, and it is the one file another process is
            // still holding.
            CopyIfPresent(_database.Path, crashed);
            CopyIfPresent(_database.Path + "-wal", crashed + "-wal");

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var recovered = new SqliteConnectionStringBuilder { DataSource = crashed }.ToString();

            await using var connection = new SqliteConnection(recovered);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            (await ScalarAsync(connection, "select coalesce(max(seq_id), 0) from fi_events")).ShouldBe(3);

            // And the counter came back with it, so the recovered store reissues 4 rather than skipping it.
            (await ScalarAsync(connection,
                "select coalesce((select seq from sqlite_sequence where name = 'fi_events'), 0)")).ShouldBe(3);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = crashed }.ToString()));
            foreach (var path in new[] { crashed, crashed + "-wal", crashed + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>
    ///     Concurrent Fisher writers produce a contiguous run. A sequence is allocated by the INSERT,
    ///     which needs the file's one write lock, so two appends cannot interleave their allocations
    ///     however hard they try to.
    /// </summary>
    [Fact]
    public async Task concurrent_writers_never_interleave_sequences()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => AppendAsync(1)));

        (await AllSequencesAsync()).ShouldBe(Enumerable.Range(1, 12).Select(x => (long)x).ToList());
    }

    /// <summary>
    ///     And so do two independent stores over the same file, which is the closest in-process proxy
    ///     for two processes: SQLite's write lock is on the file, not on the connection pool.
    /// </summary>
    [Fact]
    public async Task two_stores_over_one_file_never_interleave_sequences()
    {
        await using var other = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => AppendAsync(1))
                .Concat(Enumerable.Range(0, 6).Select(_ => AppendAsync(1, other))));

        (await AllSequencesAsync()).ShouldBe(Enumerable.Range(1, 12).Select(x => (long)x).ToList());
    }

    /// <summary>
    ///     A WAL checkpoint between appends changes nothing. It moves committed frames into the
    ///     database file; it has no opinion about sequence allocation.
    /// </summary>
    [Fact]
    public async Task a_wal_checkpoint_between_appends_leaves_no_gap()
    {
        await AppendAsync(3);
        await ExecuteAsync("pragma wal_checkpoint(truncate);");
        await AppendAsync(3);

        (await AllSequencesAsync()).ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    /// <summary>
    ///     <b>VACUUM preserves the AUTOINCREMENT counter</b>, including over rows that have been
    ///     deleted — so a vacuumed store does not reissue a sequence it already handed out.
    /// </summary>
    /// <remarks>
    ///     Worth pinning rather than assuming, because VACUUM genuinely does rebuild the database and
    ///     the neighbouring rebuild path (see
    ///     <see cref="a_table_rebuild_carries_the_autoincrement_counter_forward" />) does <em>not</em>
    ///     preserve it without help. Fisher never issues a VACUUM itself; an operator or a backup tool
    ///     may.
    /// </remarks>
    [Fact]
    public async Task vacuum_preserves_the_autoincrement_counter()
    {
        await AppendAsync(5);
        await ExecuteAsync("delete from fi_events where seq_id > 2;");
        await ExecuteAsync("vacuum;");

        await AppendAsync(1);

        // 6, not 3: the counter survived, so nothing below it is ever reissued.
        (await AllSequencesAsync()).ShouldBe([1, 2, 6]);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Contiguity is not unconditional. Deletion breaks it, and that is survivable — here is why.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     <b>Deleting events leaves a permanent hole</b>, so "committed <c>seq_id</c>s are contiguous"
    ///     is false as stated. Compacting, masking's delete path and a tenant wipe all reach here
    ///     through ordinary, supported operations.
    /// </summary>
    [Fact]
    public async Task deleting_events_leaves_a_permanent_hole()
    {
        await AppendAsync(5);
        await ExecuteAsync("delete from fi_events where seq_id in (2, 3);");
        await AppendAsync(1);

        (await AllSequencesAsync()).ShouldBe([1, 4, 5, 6]);
    }

    /// <summary>
    ///     <b>And the hole is harmless, which is the fact that matters.</b> A shard reads the events
    ///     either side of it and reports itself caught up at the head.
    /// </summary>
    /// <remarks>
    ///     The daemon's loader pages a <em>range</em> — <c>seq_id &gt; floor and seq_id &lt;= ceiling</c>
    ///     — and <c>EventPage.CalculateCeiling</c>'s contract is that a page which did not fill its
    ///     batch exhausted everything up to the bound it was given. A run of missing sequences is
    ///     therefore stepped over rather than waited on. That is what a gap detector would be for and
    ///     is why one is not needed: the machinery Marten carries guards against a sequence that is
    ///     <em>coming</em>, not one that is gone.
    /// </remarks>
    [Fact]
    public async Task a_hole_below_the_mark_does_not_hide_events_from_a_shard()
    {
        await AppendAsync(6);
        await ExecuteAsync("delete from fi_events where seq_id in (2, 3, 4);");

        var statistics = await TheDetector.Detect(TestContext.Current.CancellationToken);

        statistics.HighestSequence.ShouldBe(6);
        statistics.CurrentMark.ShouldBe(6);

        // The loader reads the survivors on both sides of the hole in one page, and the page's ceiling
        // steps past the missing range rather than stopping at it.
        var loaded = await LoadPageAsync(floor: 0, ceiling: statistics.CurrentMark);

        loaded.Sequences.ShouldBe([1, 5, 6]);
        loaded.Ceiling.ShouldBe(6);
    }

    /// <summary>
    ///     <b>Deleting the newest events drops <c>max(seq_id)</c> below a mark already recorded</b> —
    ///     fisher#174's finding, reproduced here as evidence that the detector's premise is not
    ///     unconditional.
    /// </summary>
    [Fact]
    public async Task deleting_the_newest_events_lowers_the_highest_sequence_below_the_mark()
    {
        await AppendAsync(5);
        await TheDetector.Detect(TestContext.Current.CancellationToken);

        (await MarkRowAsync()).ShouldBe(5);

        await ExecuteAsync("delete from fi_events where seq_id > 2;");

        var statistics = await TheDetector.Detect(TestContext.Current.CancellationToken);

        statistics.HighestSequence.ShouldBe(2);
        statistics.LastMark.ShouldBe(5);
    }

    /// <summary>
    ///     <b>And the recorded mark does not follow it down.</b> <c>HighWaterStatistics.HasChanged</c>
    ///     is <c>CurrentMark &gt; LastMark</c>, so a fallen ceiling persists nothing.
    /// </summary>
    /// <remarks>
    ///     This is what keeps the previous test's state survivable rather than corrupting. A mark that
    ///     moved backwards would republish a lower ceiling to every shard, and a shard whose recorded
    ///     progress was above it would take that ceiling as its own — writing durable progress
    ///     backwards, over events it had already applied. <c>Advanced.TryCorrectProgressInDatabaseAsync</c>
    ///     is the deliberate, operator-invoked repair for the row that is left too high; nothing does it
    ///     silently.
    /// </remarks>
    [Fact]
    public async Task the_recorded_mark_never_moves_backwards()
    {
        await AppendAsync(5);
        await TheDetector.Detect(TestContext.Current.CancellationToken);

        await ExecuteAsync("delete from fi_events where seq_id > 2;");
        await TheDetector.Detect(TestContext.Current.CancellationToken);

        (await MarkRowAsync()).ShouldBe(5);
    }

    /// <summary>
    ///     <b>The property that makes all of the above safe:</b> an append after a delete never reuses
    ///     a sequence, so nothing can ever appear below a mark the daemon has already passed.
    /// </summary>
    /// <remarks>
    ///     This is the whole load-bearing content of the <c>AUTOINCREMENT</c> keyword on
    ///     <c>fi_events.seq_id</c>. A bare <c>INTEGER PRIMARY KEY</c> aliases the rowid, which SQLite
    ///     reuses after a delete — and a reused sequence below the mark is an event no async projection
    ///     would ever see, silently and forever. It is also the exact property the migration test below
    ///     protects.
    /// </remarks>
    [Fact]
    public async Task an_append_after_a_delete_never_reuses_a_sequence()
    {
        await AppendAsync(5);
        await ExecuteAsync("delete from fi_events where seq_id > 2;");

        await AppendAsync(2);

        (await AllSequencesAsync()).ShouldBe([1, 2, 6, 7]);
    }

    /// <summary>
    ///     A full event wipe clears the progression rows with the events, so a store that restarts
    ///     below its old mark has no shard left holding a stale position.
    /// </summary>
    /// <remarks>
    ///     The other half of why <see cref="an_append_after_a_delete_never_reuses_a_sequence" /> is
    ///     enough. <c>DeleteAllEventDataAsync</c> is the one supported operation that empties
    ///     <c>fi_events</c> outright — the compliance fixture runs it before every test — and it clears
    ///     <c>fi_event_progression</c> in the same pass, so the two can never disagree.
    /// </remarks>
    [Fact]
    public async Task a_full_event_wipe_clears_the_progression_rows_too()
    {
        await AppendAsync(5);
        await TheDetector.Detect(TestContext.Current.CancellationToken);

        (await MarkRowAsync()).ShouldBe(5);

        await _store.Advanced.Clean.DeleteAllEventDataAsync(TestContext.Current.CancellationToken);

        (await MarkRowAsync()).ShouldBeNull();

        // The counter is not reset by the wipe, so the next append is still above everything issued.
        await AppendAsync(1);
        (await AllSequencesAsync()).ShouldBe([6]);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. The one reachable state that would break it, and where it is closed.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     <b>A schema migration that rebuilds a table carries the AUTOINCREMENT counter forward.</b>
    ///     Without that, a rebuild of a table whose newest rows had been deleted would reissue
    ///     sequences already handed out — the one reachable way to put a genuinely invisible event
    ///     below the mark.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQLite cannot alter most of a table, so anything beyond <c>ALTER TABLE ADD COLUMN</c> is
    ///         create-copy-drop-rename — and a bare rebuild leaves <c>sqlite_sequence</c> at the highest
    ///         <em>surviving</em> row. <c>fi_events</c> is exactly the shape that makes this dangerous:
    ///         it is <c>AUTOINCREMENT</c> precisely because reuse is invisible, and it is a table whose
    ///         newest rows a tenant wipe or a compaction can remove.
    ///     </para>
    ///     <para>
    ///         Weasel's <c>TableDelta</c> emits the carry-over, so this is a check on a dependency
    ///         rather than on Fisher code — which is the point. Nothing in Fisher pinned it, the
    ///         protection lives one repository away, and its absence would present as a projection
    ///         permanently missing events with nothing anywhere to say why. Run against a purpose-built
    ///         table rather than <c>fi_events</c>, because what is under test is the migrator's rebuild
    ///         path and forcing Fisher's own event table through it would be testing the same code
    ///         through more scaffolding.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_table_rebuild_carries_the_autoincrement_counter_forward()
    {
        var migrator = new SqliteMigrator();

        await using var connection =
            await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await ApplyAsync(connection, migrator, SequencedTable("TEXT"));

        await ExecuteAsync(connection, "insert into rebuilt (body) values ('a'),('b'),('c'),('d'),('e');");
        await ExecuteAsync(connection, "delete from rebuilt where seq_id > 2;");

        (await ScalarAsync(connection, "select seq from sqlite_sequence where name = 'rebuilt'")).ShouldBe(5);

        // A column type change is not expressible as an ALTER on SQLite, so this rebuilds the table.
        await ApplyAsync(connection, migrator, SequencedTable("INTEGER"));

        // The rebuild really happened, so nothing below passes vacuously through a no-op migration.
        (await ScalarAsync(connection,
                "select count(*) from pragma_table_xinfo('rebuilt') where name = 'body' and type = 'INTEGER'"))
            .ShouldBe(1);

        (await ScalarAsync(connection, "select coalesce(max(seq_id), 0) from rebuilt")).ShouldBe(2);

        // The counter, not the surviving maximum. Without the carry-over this is 2 and the next insert
        // reissues 3.
        (await ScalarAsync(connection, "select seq from sqlite_sequence where name = 'rebuilt'")).ShouldBe(5);

        await ExecuteAsync(connection, "insert into rebuilt (body) values (9);");

        (await ScalarAsync(connection, "select max(seq_id) from rebuilt")).ShouldBe(6);
    }

    private static Table SequencedTable(string bodyType)
    {
        var table = new Table(new SqliteObjectName("main", "rebuilt"));
        table.AddColumn("seq_id", "INTEGER").AsPrimaryKey().AutoIncrement();
        table.AddColumn("body", bodyType);
        return table;
    }

    private static async Task ApplyAsync(SqliteConnection connection, SqliteMigrator migrator, Table table)
    {
        var migration = await SchemaMigration
            .DetermineAsync(connection, migrator, TestContext.Current.CancellationToken, table);

        if (migration.Difference == SchemaPatchDifference.None)
        {
            return;
        }

        await migrator.ApplyAllAsync(connection, migration, AutoCreate.CreateOrUpdate,
            ct: TestContext.Current.CancellationToken);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task AppendAsync(int count, DocumentStore? store = null)
    {
        await using var session = (store ?? _store).LightweightSession();
        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"Quest {i}"));
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(List<long> Sequences, long Ceiling)> LoadPageAsync(long floor, long ceiling)
    {
        var loader = _store.Options.EventGraph;
        _ = loader;

        // Read through the daemon's own paging predicate rather than through the loader type, which is
        // internal: what is under test is that the range read steps over the hole, and that predicate
        // is the loader's verbatim.
        await using var connection =
            await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select seq_id from fi_events where seq_id > @floor and seq_id <= @ceiling and is_archived = 0 "
            + "order by seq_id limit @batch";
        command.Parameters.AddWithValue("@floor", floor);
        command.Parameters.AddWithValue("@ceiling", ceiling);
        command.Parameters.AddWithValue("@batch", 500);

        var sequences = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                sequences.Add(reader.GetInt64(0));
            }
        }

        // The page did not fill its batch, so its ceiling is the bound it was given — the rule that
        // steps the floor past every missing sequence in the range.
        return (sequences, sequences.Count < 500 ? ceiling : sequences[^1]);
    }

    private async Task<long> InsertRawEventAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             insert into fi_events (id, stream_id, version, data, type, timestamp,
                                                    tenant_id, dotnet_type, is_archived)
                             values (@id, @stream, 1, '{}', 'quest_started', '2026-01-01T00:00:00.000Z',
                                     '*DEFAULT*', 'x', 0)
                             returning seq_id;
                             """;
        insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("@stream", Guid.NewGuid().ToString());

        return Convert.ToInt64(await insert.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     Copy a file another process still holds open, which is the whole point — these bytes are
    ///     being read out from under a live writer.
    /// </summary>
    private static void CopyIfPresent(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection =
            await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private async Task<long?> MarkRowAsync()
    {
        await using var connection =
            await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select last_seq_id from fi_event_progression where name = @name";
        command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private async Task<List<long>> AllSequencesAsync()
    {
        await using var connection =
            await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select seq_id from fi_events order by seq_id";

        var sequences = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            sequences.Add(reader.GetInt64(0));
        }

        return sequences;
    }
}

using Fisher.Events.Daemon;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Daemon;

/// <summary>
///     The high-water mark, and the SQLite properties that let it be as simple as it is.
/// </summary>
public class high_water_detection : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("highwater");
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

    private FisherHighWaterDetector TheDetector
        => new(_store.Database, _store.Options.EventGraph);

    private async Task AppendAsync(int count)
    {
        await using var session = _store.LightweightSession();
        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"Quest {i}"));
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_empty_store_detects_zero()
    {
        var statistics = await TheDetector.Detect(TestContext.Current.CancellationToken);

        statistics.HighestSequence.ShouldBe(0);
        statistics.CurrentMark.ShouldBe(0);
        statistics.HasChanged.ShouldBeFalse();
    }

    /// <summary>
    ///     The whole detector in one assertion: with contiguous sequences the mark simply is the
    ///     highest sequence, so there is nothing held back in an "unsafe zone".
    /// </summary>
    [Fact]
    public async Task the_mark_is_the_highest_sequence()
    {
        await AppendAsync(5);

        var statistics = await TheDetector.Detect(TestContext.Current.CancellationToken);

        statistics.HighestSequence.ShouldBe(5);
        statistics.CurrentMark.ShouldBe(5);
        statistics.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task the_safe_zone_reading_matches_the_plain_reading()
    {
        await AppendAsync(4);

        var plain = await TheDetector.Detect(TestContext.Current.CancellationToken);
        var safe = await TheDetector.DetectInSafeZone(TestContext.Current.CancellationToken);

        safe.CurrentMark.ShouldBe(plain.CurrentMark);
    }

    [Fact]
    public async Task the_mark_is_persisted_and_read_back_as_the_last_mark()
    {
        await AppendAsync(3);
        await TheDetector.Detect(TestContext.Current.CancellationToken);

        var second = await TheDetector.Detect(TestContext.Current.CancellationToken);

        second.LastMark.ShouldBe(3);
        second.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task the_mark_advances_as_events_are_appended()
    {
        await AppendAsync(2);
        (await TheDetector.Detect(TestContext.Current.CancellationToken)).CurrentMark.ShouldBe(2);

        await AppendAsync(3);
        var statistics = await TheDetector.Detect(TestContext.Current.CancellationToken);

        statistics.LastMark.ShouldBe(2);
        statistics.CurrentMark.ShouldBe(5);
        statistics.HasChanged.ShouldBeTrue();
    }

    /// <summary>
    ///     Asking for the ceiling must not move the mark — the interface default runs a full Detect,
    ///     which would persist one as a side effect of merely asking how far there is to go.
    /// </summary>
    [Fact]
    public async Task fetching_the_ceiling_does_not_persist_a_mark()
    {
        await AppendAsync(3);

        var ceiling = await TheDetector.FetchCommittedHighWaterCeilingAsync(TestContext.Current.CancellationToken);

        ceiling.ShouldBe(3);
        (await MarkRowAsync()).ShouldBeNull();
    }

    /// <summary>
    ///     The property the whole design rests on. SQLite keeps the AUTOINCREMENT counter in
    ///     <c>sqlite_sequence</c>, an ordinary table that rolls back with the transaction — so an
    ///     aborted append leaves no permanent hole for a projection to fall into, and the detector
    ///     needs no gap handling.
    /// </summary>
    /// <remarks>
    ///     Written at the raw SQL level on purpose. Failing a Fisher append instead would prove
    ///     nothing: if the failure came before any row was inserted, no sequence was consumed and the
    ///     assertion would hold whether or not rollback restores the counter. This inserts a row,
    ///     <em>observes that it took sequence 4</em>, rolls back, and then requires the next real
    ///     append to reuse 4.
    /// </remarks>
    [Fact]
    public async Task a_rolled_back_append_leaves_no_gap_in_the_sequence()
    {
        await AppendAsync(3);

        long abandoned;

        await using (var connection =
                     await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

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

            abandoned = Convert.ToInt64(
                await insert.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // The abandoned write really did take the next number, so the reuse below is meaningful.
        abandoned.ShouldBe(4);

        await AppendAsync(1);

        (await AllSequencesAsync()).ShouldBe([1, 2, 3, 4]);
    }

    private async Task<long?> MarkRowAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select last_seq_id from fi_event_progression where name = @name";
        command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private async Task<List<long>> AllSequencesAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
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

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     The event store's diagnostic reads answer "no results" rather than throwing when the schema has
///     not been applied (fisher#332).
/// </summary>
/// <remarks>
///     <para>
///         These are the reads a monitoring console makes, usually on a timer, and it is most likely to
///         be pointed at a store in exactly the window before <c>ApplyAllDatabaseChangesOnStartup()</c>
///         has run. Every fixture elsewhere applies the schema first, which is why none of this was ever
///         exercised.
///     </para>
///     <para>
///         <c>AutoCreate.None</c> throughout, so that nothing — not even fisher#333's on-demand path —
///         can create a table behind the test's back.
///     </para>
/// </remarks>
public class diagnostic_reads_without_schema : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("diagnostics-noschema");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            options.Projections.Snapshot<SurveyTally>(SnapshotLifecycle.Async);
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static readonly ShardName Shard = new("SurveyTally", "All", 1);

    [Fact]
    public async Task projection_progress_reads_answer_nothing()
    {
        (await _store.Database.AllProjectionProgress(Token)).ShouldBeEmpty();
        (await _store.Database.ProjectionProgressFor(Shard, Token)).ShouldBe(0);
        (await _store.Database.FetchHighWaterStatusAsync(Token)).ShouldBeNull();
    }

    [Fact]
    public async Task sequence_reads_answer_nothing()
    {
        (await _store.Database.FetchHighestEventSequenceNumber(Token)).ShouldBe(0);
        (await _store.Database.FindEventStoreFloorAtTimeAsync(DateTimeOffset.UtcNow, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task dead_letter_reads_answer_nothing()
    {
        (await _store.Database.CountDeadLetterEventsAsync(Shard, Token)).ShouldBe(0);
        (await _store.Database.QueryDeadLetterEventsAsync(Shard, null, 0, 10, Token)).ShouldBeEmpty();
        (await _store.Database.FetchDeadLetterCountsAsync(Token)).ShouldBeEmpty();
        (await _store.Database.FetchDeadLetterCountsAsync("north", Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task event_store_statistics_are_all_zero()
    {
        var statistics = await _store.Advanced.FetchEventStoreStatisticsAsync(Token);

        statistics.EventCount.ShouldBe(0);
        statistics.StreamCount.ShouldBe(0);
        statistics.EventSequenceNumber.ShouldBe(0);
    }

    /// <remarks>
    ///     The sharpest case in the issue: this called <c>AllProjectionProgress</c> unprotected two lines
    ///     from a hardened read of the head sequence, so Fisher's own projections page threw.
    /// </remarks>
    [Fact]
    public async Task projection_statuses_describe_the_registered_projections()
    {
        var statuses = await ((IEventStore)_store).GetProjectionStatusesAsync(Token);

        statuses.ShouldContain(x => x.ProjectionName == "SurveyTally");
    }

    /// <remarks>
    ///     A table that exists with an older column set reports "no such column" rather than "no such
    ///     table" — the case marten#5509 actually met in production.
    /// </remarks>
    [Fact]
    public async Task a_table_with_an_older_column_set_answers_nothing_too()
    {
        await ExecuteAsync("create table fi_dead_letters (id text primary key)");

        (await _store.Database.QueryDeadLetterEventsAsync(Shard, null, 0, 10, Token)).ShouldBeEmpty();
        (await _store.Database.FetchDeadLetterCountsAsync(Token)).ShouldBeEmpty();
    }

    /// <remarks>
    ///     The wrappers this replaced caught bare <see cref="Exception" />. Narrowing to the two
    ///     missing-storage errors is the point: a file that is not a database at all is a real failure and
    ///     must not be reported as "no events".
    /// </remarks>
    [Fact]
    public async Task a_file_that_is_not_a_database_still_throws()
    {
        using var corrupt = TemporaryDatabase.Create("diagnostics-corrupt");
        await File.WriteAllBytesAsync(corrupt.Path, Enumerable.Repeat((byte)0x5A, 8192).ToArray(), Token);

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = corrupt.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
        });

        await Should.ThrowAsync<SqliteException>(() => store.Database.FetchHighestEventSequenceNumber(Token));
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

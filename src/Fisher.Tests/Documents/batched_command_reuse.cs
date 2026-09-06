using Fisher.Internal;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#171 — consecutive operations compiling to the same SQL share one prepared statement.
/// </summary>
/// <remarks>
///     <para>
///         The saving is a <c>sqlite3_prepare_v2</c> per operation, paid inside the exclusive
///         <c>BEGIN IMMEDIATE</c> transaction, so it is write-lock time rather than merely CPU. What has
///         to stay true is everything else: each operation still executes on its own and postprocesses
///         its own reader, operations still run in the order they were queued, and the
///         <c>exceptions</c> accumulation path still reports one failure alone and several together.
///     </para>
///     <para>
///         The counts are asserted rather than the timing, because timing is not a property a test can
///         hold. <c>FisherSession.LastBatchCommandCount</c> exists for that and for nothing else.
///     </para>
/// </remarks>
public class batched_command_reuse : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("batched-command-reuse");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Fly>();
            options.Schema.For<Net>();
            options.Schema.For<Creel>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     The shape the benchmark measures: N documents of one type in one unit of work.
    /// </summary>
    [Fact]
    public async Task a_run_of_identical_operations_prepares_one_command()
    {
        await using var session = (FisherSession)_store.LightweightSession();

        for (var i = 0; i < 100; i++)
        {
            session.Store(new Fly { Id = Guid.NewGuid(), Pattern = "Adams " + i });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        session.LastBatchOperationCount.ShouldBe(100);
        session.LastBatchCommandCount.ShouldBe(1);
    }

    /// <summary>
    ///     And the honest floor: alternating statements coalesce nothing, which is the old behaviour
    ///     plus one string comparison per step.
    /// </summary>
    /// <remarks>
    ///     Worth pinning as a decision rather than leaving it to look like an oversight. Grouping the
    ///     operations by statement would coalesce this too, and would reorder the unit of work — the
    ///     ordering that puts an event's tag rows before the event itself is load-bearing (fisher#6),
    ///     so runs are coalesced and nothing is ever moved.
    /// </remarks>
    [Fact]
    public async Task a_mixed_batch_falls_back_to_a_command_per_operation()
    {
        await using var session = (FisherSession)_store.LightweightSession();

        for (var i = 0; i < 10; i++)
        {
            session.Store(new Fly { Id = Guid.NewGuid(), Pattern = "Adams " + i });
            session.Store(new Net { Id = Guid.NewGuid(), Mesh = i });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        session.LastBatchOperationCount.ShouldBe(20);
        session.LastBatchCommandCount.ShouldBe(20);
    }

    /// <summary>
    ///     Coalescing must not lose a write, reorder one, or leave a document holding a neighbour's
    ///     values — the failure a rebound parameter would produce.
    /// </summary>
    [Fact]
    public async Task every_document_in_a_coalesced_batch_round_trips_its_own_values()
    {
        var flies = Enumerable.Range(0, 100)
            .Select(i => new Fly
            {
                Id = Guid.NewGuid(),
                Pattern = i % 3 == 0 ? null : $"Pattern — {i} éè",
                Weight = i * 0.25m
            })
            .ToList();

        await using (var session = (FisherSession)_store.LightweightSession())
        {
            foreach (var fly in flies)
            {
                session.Store(fly);
            }

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            session.LastBatchCommandCount.ShouldBe(1);
        }

        await using var query = _store.QuerySession();

        foreach (var expected in flies)
        {
            var loaded = await query.LoadAsync<Fly>(expected.Id, TestContext.Current.CancellationToken);

            loaded.ShouldNotBeNull();
            loaded!.Pattern.ShouldBe(expected.Pattern);
            loaded.Weight.ShouldBe(expected.Weight);
        }
    }

    /// <summary>
    ///     One loser in a coalesced run is reported as itself, not wrapped.
    /// </summary>
    /// <remarks>
    ///     This is the assertion that matters most. A concurrency failure is read out of the guarded
    ///     statement's <em>own</em> result set — "no row" is the failure — so an executor that let one
    ///     operation postprocess against a neighbour's rows would either lose this failure or invent
    ///     one. The batch is a single coalesced run of revision updates, so every operation goes
    ///     through the shared prepared statement.
    /// </remarks>
    [Fact]
    public async Task a_concurrency_failure_in_a_coalesced_batch_is_reported_alone()
    {
        var creels = await SeedCreelsAsync(5);
        await MoveOnAsync(creels[2]);

        await using var session = (FisherSession)_store.LightweightSession();

        foreach (var creel in creels)
        {
            creel.Capacity += 100;

            // Every one of these is the same guarded upsert, so the batch is one prepared statement.
            // The third names a revision the row has already reached, and the guard requires strictly
            // greater — see numeric_revisions for that rule.
            session.Store(creel, revision: 2);
        }

        await Should.ThrowAsync<ConcurrencyException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));

        // The failure was found while executing a coalesced run, which is the point of the test:
        // one prepared statement, five operations, each still reading its own result set.
        session.LastBatchOperationCount.ShouldBe(5);
        session.LastBatchCommandCount.ShouldBe(1);

        // And the unit of work rolled back whole — nobody's capacity moved.
        await using var query = _store.QuerySession();

        foreach (var creel in creels)
        {
            var stored = await query.LoadAsync<Creel>(creel.Id, TestContext.Current.CancellationToken);
            stored.ShouldNotBeNull();
            stored!.Capacity.ShouldBe(creel.Capacity - 100);
        }
    }

    /// <summary>
    ///     Two losers in one coalesced run are aggregated, which is what says both were seen rather
    ///     than the first one being read for everybody after it.
    /// </summary>
    [Fact]
    public async Task two_concurrency_failures_in_a_coalesced_batch_are_aggregated()
    {
        var creels = await SeedCreelsAsync(5);
        await MoveOnAsync(creels[1]);
        await MoveOnAsync(creels[3]);

        await using var session = (FisherSession)_store.LightweightSession();

        foreach (var creel in creels)
        {
            creel.Capacity += 100;
            session.Store(creel, revision: 2);
        }

        var aggregate = await Should.ThrowAsync<AggregateException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));

        aggregate.InnerExceptions.Count.ShouldBe(2);
        aggregate.InnerExceptions.ShouldAllBe(x => x is ConcurrencyException);

        session.LastBatchCommandCount.ShouldBe(1);
    }

    private async Task<List<Creel>> SeedCreelsAsync(int count)
    {
        var creels = Enumerable.Range(0, count)
            .Select(i => new Creel { Id = Guid.NewGuid(), Capacity = i })
            .ToList();

        await using var session = _store.LightweightSession();

        foreach (var creel in creels)
        {
            session.Store(creel);
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return creels;
    }

    /// <summary>
    ///     Somebody else takes the row to revision 2, so a later write naming revision 2 loses.
    /// </summary>
    private async Task MoveOnAsync(Creel creel)
    {
        await using var other = _store.LightweightSession();
        var theirs = await other.LoadAsync<Creel>(creel.Id, TestContext.Current.CancellationToken);

        // The revision moves; the values deliberately do not, so the rollback assertion below stays
        // about this unit of work rather than about who wrote last.
        other.Store(theirs!, revision: 2);

        await other.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public class Fly
    {
        public Guid Id { get; set; }
        public string? Pattern { get; set; }
        public decimal Weight { get; set; }
    }

    public class Net
    {
        public Guid Id { get; set; }
        public int Mesh { get; set; }
    }

    /// <summary>
    ///     Numeric revisions through <see cref="IRevisioned" />, which is what maps the member as well
    ///     as turning the column on — the configuration <c>numeric_revisions</c> exercises.
    /// </summary>
    public class Creel : IRevisioned
    {
        public Guid Id { get; set; }
        public int Capacity { get; set; }
        public int Version { get; set; }
    }
}

/// <summary>
///     What Microsoft.Data.Sqlite actually binds when a prepared command is re-executed with a
///     different operation's parameters, tested at the seam rather than through the session.
/// </summary>
/// <remarks>
///     <para>
///         What this pins is that a reused prepared statement stores, for every shape Fisher binds,
///         exactly what a freshly compiled command would have stored — including when the slot held a
///         different CLR type on the previous execution. Verified load-bearing by planting an executor
///         that reuses the standing command and never rebinds: all eight cases fail.
///     </para>
///     <para>
///         <b>It does not discriminate between moving the parameters and copying their values, and
///         saying so is the point.</b> <see cref="ReusedCommand" /> moves them, so it never has to
///         reason about Microsoft.Data.Sqlite's type inference; a value-copying implementation was
///         planted here too and passed all eight, because the provider binds by the CLR type of
///         <see cref="SqliteParameter.Value" /> regardless of the declared type — the fisher#34 fact.
///         The choice is therefore about what the code has to argue, not about a difference this suite
///         can see, and nobody should read a failure here as being about that choice.
///     </para>
///     <para>
///         Pinned the way <c>metadata_column_coercions</c> is: against a bare probe table, with none of
///         the storage machinery in the way, so a provider upgrade that changes a binding fails here
///         and names the shape.
///     </para>
/// </remarks>
public class reused_command_binding : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("reused-command-binding");
    private SqliteConnection _connection = null!;

    /// <summary>
    ///     One of every value shape Fisher binds, including the two that change a slot's inferred type.
    /// </summary>
    public static TheoryData<string, object> Values => new()
    {
        { "text", "a string" },
        { "guid as lowercase canonical text", "3f2504e0-4f89-11d3-9a0c-0305e82c3301" },
        { "integer", 42L },
        { "real", 3.5d },
        { "blob", new byte[] { 1, 2, 3, 0xFF } },
        { "null", DBNull.Value },
        { "timestamp text", "2026-09-05T17:29:00.123Z" },
        { "bool as integer", 1L }
    };

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection(_database.ConnectionString);
        await _connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var create = _connection.CreateCommand();
        create.CommandText = "create table probe (id integer primary key, val);";
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     Every shape stores exactly what a freshly compiled command would have stored, whatever the
    ///     previous execution of the same statement bound into that slot.
    /// </summary>
    /// <remarks>
    ///     Each case is run twice through one <see cref="ReusedCommand" />: once after a text value and
    ///     once after an integer, so the standing parameter has held a different inferred type both
    ///     ways round before the value under test reaches it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Values))]
    public async Task a_rebound_parameter_stores_what_a_fresh_command_would(string shape, object value)
    {
        var fresh = await StoredByAFreshCommandAsync(value);

        foreach (var preceding in new object[] { "a preceding string", 99L })
        {
            await using var reused = new ReusedCommand(_connection, null);

            await ExecuteAsync(reused, 1, preceding);
            var actual = await ExecuteAsync(reused, 2, value);

            actual.ShouldBe(fresh, $"{shape} bound after {preceding.GetType().Name}");

            // And the coalescing really happened, or this asserts nothing about reuse at all.
            reused.Prepared.ShouldBe(1);
        }
    }

    private async Task<string> StoredByAFreshCommandAsync(object value)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.AddWithValue("@p0", 0);
        command.Parameters.AddWithValue("@p1", value);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        return $"{reader.GetString(0)}/{reader.GetString(1)}";
    }

    private async Task<string> ExecuteAsync(ReusedCommand reused, int id, object value)
    {
        var built = _connection.CreateCommand();
        built.CommandText = Sql;
        built.Parameters.AddWithValue("@p0", id);
        built.Parameters.AddWithValue("@p1", value);

        var command = reused.Take(built);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        return $"{reader.GetString(0)}/{reader.GetString(1)}";
    }

    private const string Sql =
        "insert into probe (id, val) values (@p0, @p1) "
        + "on conflict (id) do update set val = excluded.val returning typeof(val), quote(val);";
}

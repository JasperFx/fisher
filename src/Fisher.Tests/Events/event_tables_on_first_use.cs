using System.Data;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     The event store's tables are created on first use, as document tables have been since
///     fisher#74, rather than only by an explicit migration (fisher#333).
/// </summary>
/// <remarks>
///     <para>
///         Every store here deliberately <em>never</em> calls
///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c>. That is the whole point: Fisher's own
///         fixtures all apply the schema in <c>InitializeAsync</c>, which is why the first append on a
///         fresh file failing with <c>no such table: fi_streams</c> — under every <c>AutoCreate</c> — was
///         invisible here and was found by a consumer.
///     </para>
/// </remarks>
public class event_tables_on_first_use : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event-first-use");
    private readonly List<DocumentStore> _stores = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private DocumentStore StoreWith(AutoCreate? autoCreate = null, Action<StoreOptions>? configure = null)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;

            if (autoCreate is { } value)
            {
                options.AutoCreateSchemaObjects = value;
            }

            configure?.Invoke(options);
        });

        _stores.Add(store);
        return store;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AutoCreate.CreateOrUpdate)]
    [InlineData(AutoCreate.All)]
    public async Task the_first_append_on_a_fresh_file_creates_the_event_tables(AutoCreate? autoCreate)
    {
        var store = StoreWith(autoCreate);
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<QuestParty>(id, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task fetch_for_writing_on_a_fresh_file_starts_a_new_stream()
    {
        var store = StoreWith();
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<QuestParty>(id, TestContext.Current.CancellationToken);
            stream.Aggregate.ShouldBeNull();

            stream.AppendOne(new MemberJoined("Sam"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        var party = await query.Events.AggregateStreamAsync<QuestParty>(id,
            token: TestContext.Current.CancellationToken);
        party!.Members.ShouldBe(["Sam"]);
    }

    [Fact]
    public async Task a_read_on_a_fresh_file_answers_no_events()
    {
        var store = StoreWith();

        await using var query = store.QuerySession();

        (await query.Events.FetchStreamAsync(Guid.NewGuid(), token: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        (await query.Events.FetchStreamStateAsync(Guid.NewGuid(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <remarks>
    ///     A unit of work whose only work is an event-side operation reaches the tables without an append
    ///     to have ensured them — the case the marker interface exists for.
    /// </remarks>
    [Fact]
    public async Task an_archive_alone_on_a_fresh_file_does_not_fail()
    {
        var store = StoreWith();

        await using var session = store.LightweightSession();
        session.Events.ArchiveStream(Guid.NewGuid());

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_inline_snapshot_on_a_fresh_file_is_written()
    {
        var store = StoreWith(configure: options =>
            options.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Inline));
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<QuestParty>(id, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.QuerySession();
        // Name rather than Members: QuestParty.Members is getter-only and does not round-trip through JSON.
        (await query.LoadAsync<QuestParty>(id, TestContext.Current.CancellationToken))!
            .Name.ShouldBe("Find the ring");
    }

    /// <remarks>
    ///     The daemon asks for <c>IEvent</c> storage before it starts. That used to fall through to a
    ///     no-op, so a daemon over a fresh file started against tables nobody had created.
    /// </remarks>
    [Fact]
    public async Task asking_for_event_storage_creates_it()
    {
        var store = StoreWith();

        await store.Database.EnsureStorageExistsAsync(typeof(IEvent), TestContext.Current.CancellationToken);

        (await TablesAsync()).ShouldContain("fi_events");
        (await TablesAsync()).ShouldContain("fi_event_progression");
    }

    [Fact]
    public async Task auto_create_none_refuses_by_name_instead_of_no_such_table()
    {
        var store = StoreWith(AutoCreate.None);

        await using var session = store.LightweightSession();
        session.Events.StartStream<QuestParty>(Guid.NewGuid(), new QuestStarted("Find the ring"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("fi_streams");
        ex.Message.ShouldContain("AutoCreate.None");
        ex.Message.ShouldContain("ApplyAllDatabaseChangesOnStartup");

        // And it created nothing — AutoCreate.None means the schema is not Fisher's to change.
        (await TablesAsync()).ShouldNotContain("fi_streams");
    }

    /// <remarks>
    ///     "Honours <c>AutoCreate.None</c>" could otherwise be an unconditional throw and still pass the
    ///     test above.
    /// </remarks>
    [Fact]
    public async Task auto_create_none_is_happy_once_the_schema_has_been_applied()
    {
        await StoreWith().ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var store = StoreWith(AutoCreate.None);
        var id = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Events.StartStream<QuestParty>(id, new QuestStarted("Find the ring"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    /// <remarks>
    ///     Creating the tables is a migration on its own connection, which would block against the write
    ///     lock the caller's transaction holds. Refusing by name beats a thirty-second
    ///     <c>database is locked</c>.
    /// </remarks>
    [Fact]
    public async Task an_enlisted_session_on_a_fresh_file_refuses_by_name_rather_than_deadlocking()
    {
        var store = StoreWith();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        await using var session = store.OpenSession(SessionOptions.ForTransaction(transaction));
        session.Events.StartStream<QuestParty>(Guid.NewGuid(), new QuestStarted("Find the ring"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("enlisted");
        ex.Message.ShouldContain("ApplyAllDatabaseChangesOnStartup");
    }

    private async Task<List<string>> TablesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

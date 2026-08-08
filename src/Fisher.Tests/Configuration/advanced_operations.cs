using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     The rest of <c>DocumentStore.Advanced</c> — fisher#42.
/// </summary>
public class advanced_operations : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("advanced");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor("main");
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    private DocumentStore StoreFor(string schema)
        => DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.DatabaseSchemaName = schema;
            o.Schema.For<Angler>();
            o.Schema.For<Boat>();
        });

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- statistics ----

    [Fact]
    public async Task statistics_over_an_empty_store()
    {
        var stats = await _store.Advanced.FetchEventStoreStatisticsAsync(Token);

        stats.EventCount.ShouldBe(0);
        stats.StreamCount.ShouldBe(0);

        // sqlite_sequence has no row until the first AUTOINCREMENT insert, so this must read 0
        // rather than throwing or coming back null.
        stats.EventSequenceNumber.ShouldBe(0);
    }

    [Fact]
    public async Task statistics_after_appending()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Angler>(Guid.NewGuid(), new Landed("Trout"), new Landed("Pike"));
            session.Events.StartStream<Angler>(Guid.NewGuid(), new Landed("Chub"));
            await session.SaveChangesAsync(Token);
        }

        var stats = await _store.Advanced.FetchEventStoreStatisticsAsync(Token);

        stats.EventCount.ShouldBe(3);
        stats.StreamCount.ShouldBe(2);
        stats.EventSequenceNumber.ShouldBe(3);
    }

    /// <summary>
    ///     The reason the type has three fields rather than two: the sequence outlives the events. It
    ///     does so because <c>seq_id</c> is <c>AUTOINCREMENT</c>, which is load-bearing — a reused
    ///     sequence below the daemon's high-water mark would be an event no projection ever sees.
    /// </summary>
    [Fact]
    public async Task the_sequence_exceeds_the_count_once_events_are_gone()
    {
        var stream = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Angler>(stream, new Landed("Trout"), new Landed("Pike"));
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.Clean.DeleteAllEventDataAsync(Token);

        var stats = await _store.Advanced.FetchEventStoreStatisticsAsync(Token);

        stats.EventCount.ShouldBe(0);
        stats.EventSequenceNumber.ShouldBe(2);
    }

    // ---- CleanAsync ----

    [Fact]
    public async Task cleaning_one_type_leaves_the_others()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Frodo" });
            session.Store(new Boat { Id = Guid.NewGuid(), Name = "Sea Fox" });
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.Clean.CleanAsync<Angler>(Token);

        await using var check = _store.LightweightSession();
        (await check.Query<Angler>().CountAsync(Token)).ShouldBe(0);
        (await check.Query<Boat>().CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     A document table is created on demand at first write, and SQLite resolves a table name when
    ///     it prepares the statement — so a blind delete would fail rather than no-op.
    /// </summary>
    [Fact]
    public async Task cleaning_a_type_whose_table_was_never_created()
    {
        await using var fresh = TemporaryDatabase.Create("advanced-fresh");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = fresh.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await Should.NotThrowAsync(() => store.Advanced.Clean.CleanAsync<Angler>(Token));
    }

    /// <summary>
    ///     The table prefix is the whole isolation boundary between two logical stores in one file.
    /// </summary>
    [Fact]
    public async Task cleaning_one_logical_store_does_not_touch_another()
    {
        await using var other = StoreFor("compliance");
        await other.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "mine" });
            await session.SaveChangesAsync(Token);
        }

        await using (var session = other.LightweightSession())
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "theirs" });
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.Clean.CleanAsync<Angler>(Token);

        await using var check = other.LightweightSession();
        (await check.Query<Angler>().CountAsync(Token)).ShouldBe(1);
    }

    // ---- the creation script ----

    [Fact]
    public async Task the_script_creates_the_same_schema_the_migration_does()
    {
        var script = _store.Advanced.ToDatabaseScript();

        script.ShouldContain("fi_events");
        script.ShouldContain("fi_streams");
        script.ShouldContain("fi_doc_angler");

        // The real assertion: it applies cleanly to a fresh file and leaves the same tables behind.
        using var fresh = TemporaryDatabase.Create("advanced-script");
        await using (var connection = new SqliteConnection(fresh.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(Token);
        }

        (await TableNames(fresh)).ShouldBe(await TableNames(_database), ignoreOrder: true);
    }

    [Fact]
    public async Task the_script_can_be_written_to_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fisher-script-{Guid.NewGuid():N}.sql");

        try
        {
            await _store.Advanced.WriteCreationScriptToFileAsync(path, Token);

            (await File.ReadAllTextAsync(path, Token)).ShouldContain("fi_events");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<string>> TableNames(TemporaryDatabase database)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name from sqlite_master where type = 'table' and name not like 'sqlite_%'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public record Landed(string Species);

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Boat
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}

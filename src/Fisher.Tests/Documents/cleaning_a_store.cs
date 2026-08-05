using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

public class cleaning_a_store : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cleaning");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Trail>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> SeedAsync()
    {
        var id = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Store(new Trail { Id = id, Name = "Seeded" });
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Seeded"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = $"select count(*) from \"{table}\"";

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name";
        command.Parameters.AddWithValue("@name", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) == 1;
    }

    [Fact]
    public async Task deleting_all_documents_leaves_the_events_alone()
    {
        await SeedAsync();

        await _store.Advanced.Clean.DeleteAllDocumentsAsync(TestContext.Current.CancellationToken);

        (await CountAsync("fi_doc_trail")).ShouldBe(0);
        (await CountAsync("fi_events")).ShouldBe(1);
    }

    [Fact]
    public async Task deleting_all_event_data_leaves_the_documents_alone()
    {
        var id = await SeedAsync();

        await _store.Advanced.Clean.DeleteAllEventDataAsync(TestContext.Current.CancellationToken);

        (await CountAsync("fi_events")).ShouldBe(0);
        (await CountAsync("fi_streams")).ShouldBe(0);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task resetting_all_data_empties_both()
    {
        await SeedAsync();

        await _store.Advanced.ResetAllDataAsync(TestContext.Current.CancellationToken);

        (await CountAsync("fi_doc_trail")).ShouldBe(0);
        (await CountAsync("fi_events")).ShouldBe(0);

        // The tables are still there — a delete is not a drop.
        (await TableExistsAsync("fi_doc_trail")).ShouldBeTrue();
    }

    [Fact]
    public async Task completely_removing_drops_the_tables_and_the_store_can_rebuild_them()
    {
        await SeedAsync();

        await _store.Advanced.Clean.CompletelyRemoveAllAsync(TestContext.Current.CancellationToken);

        (await TableExistsAsync("fi_doc_trail")).ShouldBeFalse();
        (await TableExistsAsync("fi_events")).ShouldBeFalse();

        // Dropping the tables has to invalidate the store's "this one already exists" cache, or the
        // next write would skip the migration and target a table that is no longer there.
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await SeedAsync();

        (await CountAsync("fi_doc_trail")).ShouldBe(1);
    }

    [Fact]
    public async Task cleaning_one_logical_store_does_not_touch_another_in_the_same_file()
    {
        await SeedAsync();

        await using var other = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "reporting";
            options.Schema.For<Trail>();
        });

        await other.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = other.LightweightSession())
        {
            session.Store(new Trail { Id = Guid.NewGuid(), Name = "Other" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The prefix is the whole of the isolation, SQLite having no schemas to scope to.
        await other.Advanced.Clean.CompletelyRemoveAllAsync(TestContext.Current.CancellationToken);

        (await TableExistsAsync("reporting_fi_doc_trail")).ShouldBeFalse();
        (await CountAsync("fi_doc_trail")).ShouldBe(1);
    }

    /// <summary>
    ///     fisher#6 — tag tables have to be cleared before the events they point at.
    /// </summary>
    /// <remarks>
    ///     Each <c>fi_event_tag_*</c> table carries a real foreign key to <c>fi_events(seq_id)</c> and
    ///     Weasel's default profile turns foreign key enforcement on, so clearing events first fails
    ///     with <c>FOREIGN KEY constraint failed</c>. The rest of the suite never caught it because
    ///     every fixture gets a fresh database, so the clean always ran before any tag row existed.
    /// </remarks>
    [Fact]
    public async Task deleting_all_event_data_clears_tag_rows_before_the_events_they_reference()
    {
        await using var tagged = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "tagclean";
            options.Events.RegisterTagType<Schema.TerritoryId>("territory");
        });

        await tagged.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = tagged.LightweightSession())
        {
            var @event = session.Events.BuildEvent(new QuestStarted("Tagged"));
            @event.WithTag(new Schema.TerritoryId(Guid.NewGuid()));
            session.Events.StartStream(Guid.NewGuid(), @event);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("tagclean_fi_event_tag_territory")).ShouldBe(1);

        await tagged.Advanced.Clean.DeleteAllEventDataAsync(TestContext.Current.CancellationToken);

        (await CountAsync("tagclean_fi_event_tag_territory")).ShouldBe(0);
        (await CountAsync("tagclean_fi_events")).ShouldBe(0);
    }
}

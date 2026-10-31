using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Tests.Schema;

public record CrateTag(Guid Value);

/// <summary>
///     fisher#491, from marten#5694. The tag tables' primary key leads with <c>value</c>, which cannot
///     answer "which tag rows belong to this event", and that is what every delete asks.
/// </summary>
public class tag_table_seq_index : IAsyncLifetime
{
    private const string IndexName = "ix_fi_event_tag_crate_seq_id";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tag-seq-index");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private DocumentStore Store() => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.RegisterTagType<CrateTag>("crate");
    });

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<bool> IndexExistsAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'index' and name = @name";
        command.Parameters.AddWithValue("@name", IndexName);

        return (long)(await command.ExecuteScalarAsync(Token))! == 1;
    }

    [Fact]
    public async Task a_delete_by_sequence_is_served_by_the_index()
    {
        await using var store = Store();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "explain query plan delete from fi_event_tag_crate where seq_id = 1";

        await using var reader = await command.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);

        reader.GetString(3).ShouldContain(IndexName);
    }

    /// <summary>An existing store has to pick it up through the ordinary migration, with no rebuild.</summary>
    [Fact]
    public async Task a_store_migrated_before_the_index_gets_it()
    {
        await using var store = Store();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await ExecuteAsync($"drop index {IndexName}");
        (await IndexExistsAsync()).ShouldBeFalse();

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await IndexExistsAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task nothing_is_outstanding_after_applying_it()
    {
        await using var store = Store();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await store.Advanced.CreateMigrationAsync(Token)).Difference.ShouldBe(SchemaPatchDifference.None);
    }
}

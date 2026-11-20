using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Schema;

public class LabelledParcel
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
}

public record ParcelPosted(string Label);

/// <summary>
///     The parity check for polecat#1095: a completed startup schema apply did not count as the event store's
///     first-use ensure, so the first save checked the catalog again.
/// </summary>
/// <remarks>
///     <para>
///         Fisher had the same gap. <c>FisherDatabase.EnsureEventStorageAsync</c> and
///         <c>EnsureDocumentTableAsync</c> each keep their own "already ensured" flag, and Weasel's
///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c> set neither, so the first append and the first write of
///         each document type diffed their tables against <c>sqlite_master</c> again. On SQLite that is a catalog
///         read rather than a wait on a migration lock, but it is the same redundant check, and it repeated per
///         database file under database-per-tenant.
///     </para>
///     <para>
///         The probe: after the apply, drop an object the apply created. A first save that re-checks recreates
///         it; one that trusts the apply leaves it missing.
///     </para>
/// </remarks>
public class startup_apply_counts_as_the_first_use_check : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("startup-apply-first-use");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<LabelledParcel>().Index(x => x.Label);
            options.Events.AddEventType(typeof(ParcelPosted));
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<bool> existsAsync(string type, string name)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = @type and name = @name";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token)) > 0;
    }

    private async Task executeAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    [Fact]
    public async Task the_first_append_trusts_the_startup_apply()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // Created by the apply, and used by nothing an append does
        (await existsAsync("table", "fi_event_range_marks")).ShouldBeTrue();
        await executeAsync("drop table fi_event_range_marks");

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new ParcelPosted("first"));
            await session.SaveChangesAsync(Token);
        }

        (await existsAsync("table", "fi_event_range_marks")).ShouldBeFalse();
    }

    [Fact]
    public async Task the_first_document_write_trusts_the_startup_apply()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var index = (await indexesOnAsync(_store.Options.Schema.MappingFor(typeof(LabelledParcel)).TableName.Name))
            .ShouldHaveSingleItem();
        await executeAsync($"drop index \"{index}\"");

        await using (var session = _store.LightweightSession())
        {
            session.Store(new LabelledParcel { Id = Guid.NewGuid(), Label = "first" });
            await session.SaveChangesAsync(Token);
        }

        (await existsAsync("index", index)).ShouldBeFalse();
    }

    [Fact]
    public async Task without_a_startup_apply_the_first_save_still_creates_what_it_needs()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new LabelledParcel { Id = Guid.NewGuid(), Label = "first" });
            session.Events.StartStream(Guid.NewGuid(), new ParcelPosted("first"));
            await session.SaveChangesAsync(Token);
        }

        (await existsAsync("table", "fi_event_range_marks")).ShouldBeTrue();
        (await indexesOnAsync(_store.Options.Schema.MappingFor(typeof(LabelledParcel)).TableName.Name))
            .ShouldHaveSingleItem();
    }

    private async Task<List<string>> indexesOnAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name from sqlite_master where type = 'index' and tbl_name = @table and sql is not null";
        command.Parameters.AddWithValue("@table", table);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

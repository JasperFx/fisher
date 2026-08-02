using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Schema;

public class event_store_schema_creation : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("schema");

    public ValueTask InitializeAsync() => default;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return default;
    }

    private FisherDatabase DatabaseFor(Action<StoreOptions>? configure = null)
    {
        var options = new StoreOptions
        {
            ConnectionString = _database.ConnectionString,
            AutoCreateSchemaObjects = AutoCreate.All
        };

        configure?.Invoke(options);

        return new FisherDatabase(options);
    }

    private async Task<HashSet<string>> TableNamesAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<(string Name, string Type, bool NotNull, bool PrimaryKey)>> ColumnsAsync(string table)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = $"pragma table_info('{table}')";

        var columns = new List<(string, string, bool, bool)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1,
                reader.GetInt32(5) > 0));
        }

        return columns;
    }

    [Fact]
    public async Task creates_the_three_event_store_tables()
    {
        await using var database = DatabaseFor();
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();

        tables.ShouldContain("fi_streams");
        tables.ShouldContain("fi_events");
        tables.ShouldContain("fi_event_progression");
    }

    [Fact]
    public async Task folds_a_non_default_schema_name_into_the_table_prefix()
    {
        await using var database = DatabaseFor(x => x.DatabaseSchemaName = "compliance");
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();

        tables.ShouldContain("compliance_fi_events");
        tables.ShouldContain("compliance_fi_streams");
        tables.ShouldNotContain("fi_events");
    }

    [Fact]
    public async Task two_schema_names_coexist_in_one_database_file()
    {
        await using var main = DatabaseFor();
        await main.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        await using var other = DatabaseFor(x => x.DatabaseSchemaName = "other");
        await other.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();

        tables.ShouldContain("fi_events");
        tables.ShouldContain("other_fi_events");
    }

    [Fact]
    public async Task events_seq_id_is_an_autoincrement_primary_key()
    {
        await using var database = DatabaseFor();
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync("fi_events");
        var seq = columns.Single(x => x.Name == "seq_id");

        seq.Type.ShouldBe("INTEGER");
        seq.PrimaryKey.ShouldBeTrue();

        // AUTOINCREMENT is what stops SQLite reusing the rowid of a deleted event, which the async
        // daemon's monotonic high-water mark depends on. SQLite records its use by creating the
        // sqlite_sequence bookkeeping table, so that is the observable proof it was emitted.
        var tables = await TableNamesAsync();
        tables.ShouldContain("sqlite_sequence");
    }

    [Fact]
    public async Task optional_metadata_columns_are_absent_until_enabled()
    {
        await using var database = DatabaseFor();
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var names = (await ColumnsAsync("fi_events")).Select(x => x.Name).ToList();

        names.ShouldNotContain("correlation_id");
        names.ShouldNotContain("causation_id");
        names.ShouldNotContain("headers");
        names.ShouldNotContain("user_name");
    }

    [Fact]
    public async Task optional_metadata_columns_are_added_when_enabled()
    {
        await using var database = DatabaseFor(x =>
        {
            x.Events.EnableCorrelationId = true;
            x.Events.EnableCausationId = true;
            x.Events.EnableHeaders = true;
            x.Events.EnableUserName = true;
        });

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var names = (await ColumnsAsync("fi_events")).Select(x => x.Name).ToList();

        names.ShouldContain("correlation_id");
        names.ShouldContain("causation_id");
        names.ShouldContain("headers");
        names.ShouldContain("user_name");
    }

    [Fact]
    public async Task conjoined_tenancy_puts_tenant_id_first_in_the_streams_primary_key()
    {
        await using var database = DatabaseFor(x => x.Events.TenancyStyle = TenancyStyle.Conjoined);
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var keyColumns = (await ColumnsAsync("fi_streams")).Where(x => x.PrimaryKey).Select(x => x.Name).ToList();

        keyColumns.ShouldBe(["tenant_id", "id"]);
    }

    [Fact]
    public async Task applying_changes_twice_is_idempotent()
    {
        await using var database = DatabaseFor();
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        (await TableNamesAsync()).ShouldContain("fi_events");
    }
}

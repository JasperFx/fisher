using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Schema;

public record TerritoryId(Guid Value);

public record CohortId(string Value);

public record SeatNumber(int Value);

/// <summary>
///     The <c>fi_event_tag_*</c> tables: that they are created, shaped correctly, and enforce what the
///     write path is about to rely on.
/// </summary>
public class tag_tables : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tags");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<TerritoryId>("territory");
            options.Events.RegisterTagType<CohortId>("cohort");
            options.Events.RegisterTagType<SeatNumber>("seat");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<List<string>> TableNamesAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table' order by name";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<(string Name, string Type, long PkOrdinal)>> ColumnsAsync(string table)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"pragma table_info('{table}')";

        var columns = new List<(string, string, long)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(5)));
        }

        return columns;
    }

    [Fact]
    public async Task a_table_is_created_for_each_registered_tag_type()
    {
        var tables = await TableNamesAsync();

        tables.ShouldContain("fi_event_tag_territory");
        tables.ShouldContain("fi_event_tag_cohort");
        tables.ShouldContain("fi_event_tag_seat");
    }

    [Fact]
    public async Task no_table_is_created_for_an_unregistered_tag_type()
    {
        (await TableNamesAsync()).ShouldNotContain("fi_event_tag_unregistered");
    }

    /// <summary>
    ///     Value first in the composite key, because a tag query filters on value — leading with
    ///     <c>seq_id</c> would make the same lookup a scan.
    /// </summary>
    [Fact]
    public async Task the_primary_key_leads_with_the_value()
    {
        var columns = await ColumnsAsync("fi_event_tag_territory");

        columns.Select(c => c.Name).ShouldBe(["value", "seq_id"]);
        columns.Single(c => c.Name == "value").PkOrdinal.ShouldBe(1);
        columns.Single(c => c.Name == "seq_id").PkOrdinal.ShouldBe(2);
    }

    [Theory]
    [InlineData("fi_event_tag_territory", "TEXT")]
    [InlineData("fi_event_tag_cohort", "TEXT")]
    [InlineData("fi_event_tag_seat", "INTEGER")]
    public async Task the_value_column_takes_the_tag_primitives_storage_type(string table, string expected)
    {
        var columns = await ColumnsAsync(table);

        columns.Single(c => c.Name == "value").Type.ShouldBe(expected);
    }

    /// <summary>
    ///     The property the write path will lean on rather than reading first: a duplicate
    ///     (value, seq_id) is rejected, so re-tagging can be an <c>on conflict do nothing</c> and
    ///     <c>AssignTagWhere</c> is idempotent for free.
    /// </summary>
    [Fact]
    public async Task the_composite_key_rejects_a_duplicate_tagging()
    {
        var seqId = await AnEventAsync();
        var region = Guid.NewGuid().ToString();

        await InsertTagAsync(region, seqId);

        var ex = await Should.ThrowAsync<SqliteException>(async () => await InsertTagAsync(region, seqId));
        ex.SqliteExtendedErrorCode.ShouldBe(1555); // SQLITE_CONSTRAINT_PRIMARYKEY
    }

    [Fact]
    public async Task the_same_value_may_tag_two_different_events()
    {
        var region = Guid.NewGuid().ToString();

        await InsertTagAsync(region, await AnEventAsync());
        await InsertTagAsync(region, await AnEventAsync());

        (await CountTagsAsync()).ShouldBe(2);
    }

    /// <summary>
    ///     Foreign keys are enforced because <c>SqlitePragmaSettings.Default</c> sets
    ///     <c>PRAGMA foreign_keys = ON</c>. Worth pinning: the pragma is overridable, and a consumer who
    ///     turns it off silently downgrades this to documentation.
    /// </summary>
    [Fact]
    public async Task a_tag_cannot_reference_an_event_that_does_not_exist()
    {
        var ex = await Should.ThrowAsync<SqliteException>(async () =>
            await InsertTagAsync(Guid.NewGuid().ToString(), 999_999));

        ex.SqliteErrorCode.ShouldBe(19); // SQLITE_CONSTRAINT
    }

    /// <summary>
    ///     Two logical stores in one file are isolated by table prefix, tag tables included.
    /// </summary>
    [Fact]
    public async Task a_named_schema_folds_into_the_table_prefix()
    {
        await using var scoped = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "other";
            options.Events.RegisterTagType<TerritoryId>("territory");
        });

        await scoped.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();
        tables.ShouldContain("other_fi_event_tag_territory");
        tables.ShouldContain("fi_event_tag_territory");
    }

    private async Task<long> AnEventAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, new Events.QuestStarted("Tagged"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select max(seq_id) from fi_events";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task InsertTagAsync(string value, long seqId)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into fi_event_tag_territory (value, seq_id) values (@value, @seq)";
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@seq", seqId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> CountTagsAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_event_tag_territory";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

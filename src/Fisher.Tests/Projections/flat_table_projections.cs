using Fisher.Projections.Flattened;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Projections;

/// <summary>
///     A flat-table projection: one table row per stream, written by declarative column mappings.
/// </summary>
/// <remarks>
///     <para>
///         The behaviour — upsert, increment, decrement, delete, rebuild — is covered once and
///         cross-store by <c>FlatTableProjectionCompliance</c>. What is pinned here is only what that
///         suite cannot see, because it is Fisher's alone: <strong>where the table lands</strong> when
///         a logical schema is in play, and <strong>that the migration creates it</strong> rather than
///         the projection issuing a CREATE TABLE on first write.
///     </para>
///     <para>
///         The second of those is the one that would rot silently. Polecat creates the table lazily
///         inside its first apply, which works but routes around
///         <see cref="StoreOptions.AutoCreateSchemaObjects" /> — a store configured
///         <see cref="AutoCreate.None" /> would still get DDL. Fisher registers a feature schema
///         instead, so the policy applies to a flat table exactly as it does to every other table.
///     </para>
/// </remarks>
public class flat_table_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("flat-table");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "reporting";
            options.Projections.Add(new QuestMetricsProjection(), ProjectionLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     SQLite has no schemas, so a logical schema is a name prefix — and a flat table takes the
    ///     prefix without the <c>fi_</c> family marker, which is reserved for tables Fisher owns the
    ///     shape of.
    /// </summary>
    [Fact]
    public async Task the_table_is_created_by_the_migration_under_the_folded_name()
    {
        var tables = await TableNamesAsync();

        tables.ShouldContain("reporting_quest_metrics");

        // Not the family prefix — that would claim the shape is Fisher's rather than the projection's.
        tables.ShouldNotContain("reporting_fi_quest_metrics");
    }

    /// <summary>
    ///     Registering the projection is what puts the table in the migration, so a store told not to
    ///     create schema objects does not get one. A lazy CREATE TABLE on first write would.
    /// </summary>
    [Fact]
    public async Task auto_create_none_leaves_the_table_alone()
    {
        using var database = TemporaryDatabase.Create("flat-table-none");

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            options.Projections.Add(new QuestMetricsProjection(), ProjectionLifecycle.Inline);
        });

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        (await TableNamesAsync(connection)).ShouldNotContain("quest_metrics");
    }

    [Fact]
    public async Task the_mapped_columns_are_written_and_updated_in_place()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamId, new MemberJoined("Frodo"), new MemberJoined("Sam"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var row = await RowAsync(streamId);

        row.ShouldNotBeNull();
        row.Value.Name.ShouldBe("Find the ring");
        row.Value.MemberCount.ShouldBe(2);
    }

    /// <summary>
    ///     The Guid trap, in the one place a flat table meets it: the primary key holds a stream id, and
    ///     it has to go down as the same lowercase canonical text every other Fisher write produces or
    ///     the second event inserts a second row instead of updating the first.
    /// </summary>
    [Fact]
    public async Task the_stream_id_key_is_stored_as_lowercase_canonical_text()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select id from reporting_quest_metrics";

        var stored = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        stored.ShouldBe(streamId.ToString());
        stored.ShouldBe(stored!.ToLowerInvariant());
    }

    [Fact]
    public async Task a_delete_mapping_removes_the_row()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowAsync(streamId)).ShouldNotBeNull();

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamId, new MonsterSlain("Balrog"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowAsync(streamId)).ShouldBeNull();
    }

    /// <summary>
    ///     A projection with no primary key column is a configuration error, and saying so at
    ///     registration is much cheaper than the "no such column" the first write would raise.
    /// </summary>
    [Fact]
    public void a_projection_with_no_primary_key_is_rejected()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            new StoreOptions { ConnectionString = "Data Source=:memory:" }
                .Projections.Add(new KeylessProjection(), ProjectionLifecycle.Inline));

        ex.Message.ShouldContain("primary key");
    }

    private async Task<(string Name, long MemberCount)?> RowAsync(Guid streamId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name, member_count from reporting_quest_metrics where id = @id";
        command.Parameters.AddWithValue("@id", streamId.ToString());

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        return await reader.ReadAsync(TestContext.Current.CancellationToken)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private async Task<List<string>> TableNamesAsync(SqliteConnection? existing = null)
    {
        var connection = existing ?? new SqliteConnection(_database.ConnectionString);

        if (existing is null)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        try
        {
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
        finally
        {
            if (existing is null)
            {
                await connection.DisposeAsync();
            }
        }
    }
}

public class QuestMetricsProjection : FlatTableProjection
{
    public QuestMetricsProjection() : base("quest_metrics")
    {
        Table.AddColumn("id", "TEXT").NotNull().AsPrimaryKey();

        Project<QuestStarted>(map =>
        {
            map.Map(x => x.Name);
            map.SetValue("member_count", 0);
        });

        Project<MemberJoined>(map => map.Increment("member_count"));

        Delete<MonsterSlain>();
    }
}

/// <summary>Declares mappings but never a primary key.</summary>
public class KeylessProjection : FlatTableProjection
{
    public KeylessProjection() : base("keyless")
    {
        Project<QuestStarted>(map => map.Map(x => x.Name));
    }
}

using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     jasperfx#1150 — <see cref="IEventDatabase.FetchFirstEventTimestampsAfterAsync" />, beyond what
///     <c>PendingEventAgeCompliance</c> covers: the query plan, single tenancy, and a list longer than any
///     parameter limit.
/// </summary>
/// <remarks>
///     A monitoring console asks this for every shard and tenant on a timer, so what matters past the
///     semantics is the cost: one statement, and each position a seek rather than a scan.
/// </remarks>
public class pending_event_ages : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("pending-event-ages");
    private DocumentStore _store = null!;
    private DocumentStore _conjoinedStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        _conjoinedStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "conjoined";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _conjoinedStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _conjoinedStore.DisposeAsync();
        _database.Dispose();
    }

    private async Task<IEvent> StartAsync(string name)
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted(name));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        return (await query.Events.FetchStreamAsync(streamId, token: Token)).Single();
    }

    private async Task<IReadOnlyList<string>> PlanAsync(FisherDatabase database)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "explain query plan " + database.FirstEventTimestampsAfterSql();
        command.Parameters.AddWithValue("@positions", """[[0,null],[5,"acme"]]""");

        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    /// <summary>
    ///     The events table is searched through the <c>(is_archived, seq_id)</c> index on both of its
    ///     columns, never scanned — with and without the conjoined tenant term, which is not in the index.
    /// </summary>
    [Fact]
    public async Task each_lookup_is_an_index_seek()
    {
        var single = await PlanAsync(_store.Database);
        single.ShouldContain(x => x.Contains("SEARCH e USING INDEX ix_fi_events_archived_seq (is_archived=? AND seq_id>?)"),
            string.Join(Environment.NewLine, single));
        single.ShouldNotContain(x => x.StartsWith("SCAN e"));

        var conjoined = await PlanAsync(_conjoinedStore.Database);
        conjoined.ShouldContain(
            x => x.Contains("SEARCH e USING INDEX ix_conjoined_fi_events_archived_seq (is_archived=? AND seq_id>?)"),
            string.Join(Environment.NewLine, conjoined));
        conjoined.ShouldNotContain(x => x.StartsWith("SCAN e"));
    }

    /// <summary>
    ///     A tenant means nothing to a single-tenanted store, so naming one is ignored rather than
    ///     filtering every event out — the events are all stamped with the default tenant.
    /// </summary>
    [Fact]
    public async Task a_tenant_is_ignored_under_single_tenancy()
    {
        var first = await StartAsync("Erebor");

        var age = (await _store.Database.FetchFirstEventTimestampsAfterAsync([("acme", 0)], Token)).Single();

        age.TenantId.ShouldBe("acme");
        age.Timestamp.ShouldBe(first.Timestamp);
    }

    /// <summary>
    ///     The positions are one JSON parameter, so a fleet's worth of shards and tenants is still one
    ///     statement with no parameter limit to chunk around.
    /// </summary>
    [Fact]
    public async Task a_long_list_is_answered_in_one_call()
    {
        var first = await StartAsync("Dale");
        var second = await StartAsync("Esgaroth");

        var positions = Enumerable.Range(0, 40_000)
            .Select(i => ((string?)null, i % 3 == 0 ? 0L : i % 3 == 1 ? first.Sequence : second.Sequence))
            .ToList();

        var ages = await _store.Database.FetchFirstEventTimestampsAfterAsync(positions, Token);

        ages.Count.ShouldBe(positions.Count);
        for (var i = 0; i < ages.Count; i++)
        {
            ages[i].Sequence.ShouldBe(positions[i].Item2);
            ages[i].Timestamp.ShouldBe(i % 3 == 0 ? first.Timestamp : i % 3 == 1 ? second.Timestamp : null);
        }
    }
}

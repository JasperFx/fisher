using System.Text.Json;
using JasperFx;
using JasperFx.Events.CommandLine;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Events;

/// <summary>
///     End-to-end coverage of the <c>stream-query</c> CLI command (jasperfx#740) against a real Fisher
///     store — the pattern <c>event_query_command</c> established: <c>StreamQueryCommand.Execute</c>
///     driven with a real <c>StreamQueryInput</c>, a host built the way the command builds one, stdout
///     captured and parsed as the JSON an agent or script would consume.
/// </summary>
/// <remarks>
///     Upstream owns the flag-to-queryable mapping and Fisher's store behavior is pinned by the
///     enrolled <c>StreamStateQueryCompliance</c> — this class owns the seam where host building,
///     store discovery, aggregate-type resolution, the queryable translation and the JSON rendering
///     meet, including <c>versionsSinceCompaction</c> riding the jasperfx#740 watermark end to end.
/// </remarks>
public class stream_query_command : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cli_stream_query");
    private Guid _compactedPurse;
    private Guid _freshPurse;

    /// <summary>
    ///     Seed through a store of this test's own, then dispose it — the command's host does the read.
    ///     Two purses (one partially compacted) plus a quest stream as the aggregate-type decoy.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        _compactedPurse = Guid.NewGuid();
        _freshPurse = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            // Version 5 once saved.
            session.Events.StartStream<Purse>(_compactedPurse, new CoinsEarned(100), new CoinsSpent(10),
                new CoinsEarned(5), new CoinsSpent(1), new CoinsEarned(2));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The creation-order tiebreak is the id, so put real wall-clock between the streams to make
        // the command's Created-ascending ordering deterministic.
        await Task.Delay(30, TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            // Version 2, never compacted.
            session.Events.StartStream<Purse>(_freshPurse, new CoinsEarned(20), new CoinsSpent(3));

            // The decoy: a different aggregate type, version 1.
            session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Guard the shire"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Partial compaction through version 3: watermark 3, un-compacted growth 5 - 3 = 2.
        await using (var session = store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(_compactedPurse, x => x.Version = 3);
        }
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     A fresh builder per call, exactly as an application's <c>Program</c> hands one to JasperFx's
    ///     command line — the command disposes the host it builds, so a builder cannot be reused.
    /// </summary>
    private Task<(bool Success, JsonDocument Report)> runAsync(StreamQueryInput input)
    {
        input.HostBuilder = new HostBuilder()
            .ConfigureServices(services => services.AddFisher(_database.ConnectionString));

        return CliJsonCapture.RunAsync(() => new StreamQueryCommand().Execute(input));
    }

    [Fact]
    public async Task an_aggregate_type_query_answers_the_exact_streams_with_the_watermark()
    {
        var (success, report) = await runAsync(new StreamQueryInput { AggregateTypeFlag = "Purse" });

        success.ShouldBeTrue();

        var root = report.RootElement;
        root.GetProperty("totalCount").GetInt32().ShouldBe(2);
        root.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        root.TryGetProperty("error", out _).ShouldBeFalse();
        root.GetProperty("query").GetProperty("aggregateType").GetString()!.ShouldContain("Purse");

        var streams = root.GetProperty("streams");
        streams.GetArrayLength().ShouldBe(2);

        // Created ascending: the compacted purse was started first.
        var compacted = streams[0];
        compacted.GetProperty("streamId").GetString().ShouldBe(_compactedPurse.ToString());
        compacted.GetProperty("version").GetInt64().ShouldBe(5);
        compacted.GetProperty("compactedVersion").GetInt64().ShouldBe(3);
        compacted.GetProperty("versionsSinceCompaction").GetInt64().ShouldBe(2);
        compacted.GetProperty("aggregateType").GetString()!.ShouldContain("Purse");
        compacted.GetProperty("isArchived").GetBoolean().ShouldBeFalse();

        var fresh = streams[1];
        fresh.GetProperty("streamId").GetString().ShouldBe(_freshPurse.ToString());
        fresh.GetProperty("version").GetInt64().ShouldBe(2);
        fresh.GetProperty("compactedVersion").GetInt64().ShouldBe(0);
        fresh.GetProperty("versionsSinceCompaction").GetInt64().ShouldBe(2);
    }

    /// <summary>
    ///     The compaction-policy threshold over the wire: growth must EXCEED the flag, both purses sit
    ///     at exactly 2, so a truthful zero comes back as a success — a real answer, not a failure —
    ///     and a store thresholding on raw Version instead of growth would return the version-5 purse.
    /// </summary>
    [Fact]
    public async Task a_growth_threshold_matching_nothing_is_a_zero_answer_and_a_success()
    {
        var (success, report) = await runAsync(new StreamQueryInput
        {
            AggregateTypeFlag = "Purse",
            VersionAboveCompactedFlag = 2
        });

        success.ShouldBeTrue();

        var root = report.RootElement;
        root.GetProperty("totalCount").GetInt32().ShouldBe(0);
        root.GetProperty("streams").GetArrayLength().ShouldBe(0);
        root.TryGetProperty("error", out _).ShouldBeFalse();
    }

    /// <summary>
    ///     The refusal case: this store has no tenant dimension, so a tenant-scoped query is refused
    ///     by name with a failure return — never an unscoped result that reads as one tenant's.
    /// </summary>
    [Fact]
    public async Task a_tenant_scope_on_a_tenantless_store_is_refused_with_a_failure_return()
    {
        var (success, report) = await runAsync(new StreamQueryInput { TenantFlag = "acme" });

        success.ShouldBeFalse();

        var root = report.RootElement;
        root.GetProperty("error").GetString()!.ShouldContain("acme");
        root.GetProperty("totalCount").GetInt32().ShouldBe(0);
        root.GetProperty("streams").GetArrayLength().ShouldBe(0);
    }
}

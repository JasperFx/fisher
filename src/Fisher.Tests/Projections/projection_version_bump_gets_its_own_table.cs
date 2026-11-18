using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#638. Bumping an aggregation projection's <c>Version</c> builds the new version's documents in a table
///     of its own (<c>fi_doc_versionedtally_2</c>), as Marten's <c>ProjectionVersionAliasPolicy</c> does, instead of
///     replaying every event onto the documents the old version built. Composite members take the composite's
///     version, so a composite bump gets new member progression rows and new tables too.
/// </summary>
public class projection_version_bump_gets_its_own_table : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-version-bump");
    private readonly Guid _streamId = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void version_one_keeps_the_plain_table_name()
    {
        using var store = BuildStore(o => o.Projections.Add(new VersionedTallyProjection(1), ProjectionLifecycle.Async));

        store.Options.Schema.MappingFor(typeof(VersionedTally)).Alias.ShouldBe("versionedtally");
    }

    [Fact]
    public void a_later_version_suffixes_the_alias()
    {
        using var store = BuildStore(o => o.Projections.Add(new VersionedTallyProjection(3), ProjectionLifecycle.Async));

        store.Options.Schema.MappingFor(typeof(VersionedTally)).Alias.ShouldBe("versionedtally_3");
    }

    [Fact]
    public async Task bumping_a_projection_rebuilds_into_a_new_table_and_leaves_the_old_one_alone()
    {
        await RunAsync(o => o.Projections.Add(new VersionedTallyProjection(1), ProjectionLifecycle.Async), appendThree: true);

        (await CountInTableAsync("fi_doc_versionedtally")).ShouldBe(3);

        await RunAsync(o => o.Projections.Add(new VersionedTallyProjection(2), ProjectionLifecycle.Async), appendThree: false);

        // Version 2 replayed the three events into its own table, from nothing.
        (await CountInTableAsync("fi_doc_versionedtally_2")).ShouldBe(3);

        // Version 1's document is exactly as version 1 left it.
        (await CountInTableAsync("fi_doc_versionedtally")).ShouldBe(3);

        await using var store = BuildStore(o => o.Projections.Add(new VersionedTallyProjection(2), ProjectionLifecycle.Async));
        var progress = await store.Database.AllProjectionProgress(Token);
        progress.Where(x => x.ShardName.StartsWith("VersionedTally")).Select(x => (x.ShardName, x.Sequence))
            .OrderBy(x => x.ShardName, StringComparer.Ordinal).ShouldBe([("VersionedTally:All", 3L), ("VersionedTally:V2:All", 3L)]);

        await using var query = store.QuerySession();
        (await query.LoadAsync<VersionedTally>(_streamId, Token))!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task bumping_a_composite_gives_its_members_the_new_version_and_new_tables()
    {
        await RunAsync(o => o.Projections.CompositeProjectionFor("tallies", composite =>
        {
            composite.Version = 1;
            composite.Add(new VersionedTallyProjection(1));
        }), appendThree: true);

        await RunAsync(o => o.Projections.CompositeProjectionFor("tallies", composite =>
        {
            composite.Version = 2;
            composite.Add(new VersionedTallyProjection(1));
        }), appendThree: false);

        (await CountInTableAsync("fi_doc_versionedtally")).ShouldBe(3);
        (await CountInTableAsync("fi_doc_versionedtally_2")).ShouldBe(3);

        await using var store = BuildStore(o => o.Projections.CompositeProjectionFor("tallies", composite =>
        {
            composite.Version = 2;
            composite.Add(new VersionedTallyProjection(1));
        }));

        // The member's own version followed the composite's, so its progression row is new too.
        var progress = await store.Database.AllProjectionProgress(Token);
        progress.Select(x => x.ShardName).ShouldContain("VersionedTally:V2:All");
        progress.Single(x => x.ShardName == "VersionedTally:All").Sequence.ShouldBe(3L);
        progress.Single(x => x.ShardName == "VersionedTally:V2:All").Sequence.ShouldBe(3L);
    }

    private async Task RunAsync(Action<StoreOptions> configure, bool appendThree)
    {
        await using var store = BuildStore(configure);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        if (appendThree)
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(_streamId, new VersionedIncrement(), new VersionedIncrement(),
                new VersionedIncrement());
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    private DocumentStore BuildStore(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    private async Task<long> CountInTableAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select json_extract(data, '$.count') from \"{table}\" where id = $id";
        command.Parameters.AddWithValue("$id", _streamId.ToString());

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}

public record VersionedIncrement;

public class VersionedTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

public class VersionedTallyProjection : Fisher.Projections.SingleStreamProjection<VersionedTally, Guid>
{
    public VersionedTallyProjection(uint version)
    {
        Name = "VersionedTally";
        Version = version;
    }

    public void Apply(VersionedIncrement _, VersionedTally tally) => tally.Count++;
}

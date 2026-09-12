using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#243 — <c>IEventStore.GetProjectionStatusesAsync</c>, the snapshot a monitoring console's
///     projections page renders.
/// </summary>
/// <remarks>
///     <para>
///         <b>There is no shared suite for this, so every fact here is Fisher's own.</b> What the tests
///         are shaped around is the one field that is not a database read:
///         <c>ShardStatus.State</c> is a fact about the running daemon, and the failure this feature
///         invites is filling it with a plausible constant. Polecat reports every shard as
///         <c>"Stopped"</c> (polecat#200) — which is not a partial answer but a wrong one, because it
///         is exactly what a real stopped shard reports and it is the reading an operator acts on.
///     </para>
///     <para>
///         So the discriminating tests are the ones that tell <c>Unknown</c>, <c>Stopped</c> and
///         <c>Running</c> apart, and the one that stops the daemon and appends more events to prove
///         <c>EventStoreSequence</c> is the store's head rather than the daemon's record of it.
///     </para>
/// </remarks>
public class projection_statuses : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-statuses");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<StatusTally>(SnapshotLifecycle.Async);
            options.Projections.Snapshot<InlineTally>(SnapshotLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IEventStore TheExplorer => _store;

    // ---- the field that is not a database read ----

    /// <summary>
    ///     With no daemon this process can ask, a shard's state is <c>Unknown</c> — never <c>Stopped</c>.
    /// </summary>
    /// <remarks>
    ///     <b>The load-bearing test of this feature.</b> A store under <c>DaemonMode.ExternallyManaged</c>,
    ///     a console in another process and a hand-built store like this one all genuinely cannot see
    ///     the daemon, and "I cannot tell" is a different operational situation from "it is not
    ///     running". Reporting <c>Stopped</c> here is indistinguishable from a real stopped shard, which
    ///     is the answer an operator would act on — so the plausible constant is worse than no field at
    ///     all.
    /// </remarks>
    [Fact]
    public async Task a_store_with_no_visible_daemon_reports_unknown_rather_than_stopped()
    {
        var statuses = await TheExplorer.GetProjectionStatusesAsync(Token);

        var tally = statuses.Single(x => x.ProjectionName == nameof(StatusTally));
        var shard = tally.Shards.ShouldHaveSingleItem();

        shard.State.ShouldBe("Unknown");
        shard.State.ShouldNotBe("Stopped");
    }

    /// <remarks>
    ///     The progression row is read even with no daemon here to ask, because it is the database's
    ///     answer rather than the daemon's — so a console pointed at a store whose daemon runs
    ///     elsewhere still sees how far each shard has got.
    /// </remarks>
    [Fact]
    public async Task progress_is_reported_without_a_daemon_to_ask()
    {
        await AppendAsync(Guid.NewGuid(), 2);

        // Plant a progression row the way a daemon in another process would have left it.
        await WriteProgressAsync(ShardIdentityFor(nameof(StatusTally)), 1);

        var statuses = await TheExplorer.GetProjectionStatusesAsync(Token);
        var shard = statuses.Single(x => x.ProjectionName == nameof(StatusTally)).Shards.ShouldHaveSingleItem();

        shard.ProcessedSequence.ShouldBe(1);
        shard.EventStoreSequence.ShouldBe(2);
        shard.State.ShouldBe("Unknown");
        shard.Error.ShouldBeNull();
    }

    /// <summary>
    ///     <c>EventStoreSequence</c> is <c>max(seq_id)</c>, not the persisted high-water row.
    /// </summary>
    /// <remarks>
    ///     The two are the same number on a Fisher store whose daemon is current, which is why this
    ///     needs arranging deliberately: events are appended with nothing running, so the high-water row
    ///     is behind the table. Reading the row instead would report the head as 0 here and make every
    ///     shard look caught up — the opposite of what a projections page is opened to find out.
    /// </remarks>
    [Fact]
    public async Task the_head_is_the_stores_own_rather_than_the_daemons_record_of_it()
    {
        await AppendAsync(Guid.NewGuid(), 3);

        var statuses = await TheExplorer.GetProjectionStatusesAsync(Token);
        var shard = statuses.Single(x => x.ProjectionName == nameof(StatusTally)).Shards.ShouldHaveSingleItem();

        // Nothing has run, so the high-water progression row does not exist and reads as zero.
        (await _store.Database.ProjectionProgressFor(new ShardName(ShardState.HighWaterMark), Token))
            .ShouldBe(0);

        shard.EventStoreSequence.ShouldBe(3);
    }

    // ---- what appears, and what it says about itself ----

    /// <remarks>
    ///     An inline projection has no shards, and is reported with an <em>empty</em> list rather than
    ///     omitted — the <c>Lifecycle</c> is what says why it is empty. Polecat synthesises a fake
    ///     single shard for these and puts the lifecycle string in its <c>State</c> slot, which makes
    ///     that field mean two different things depending on the row.
    /// </remarks>
    [Fact]
    public async Task an_inline_projection_is_reported_with_no_shards()
    {
        var statuses = await TheExplorer.GetProjectionStatusesAsync(Token);

        var inline = statuses.Single(x => x.ProjectionName == nameof(InlineTally));

        inline.Lifecycle.ShouldBe(nameof(ProjectionLifecycle.Inline));
        inline.Shards.ShouldBeEmpty();

        statuses.Single(x => x.ProjectionName == nameof(StatusTally))
            .Lifecycle.ShouldBe(nameof(ProjectionLifecycle.Async));
    }

    /// <remarks>
    ///     A subscription is a daemon shard with progress to report, so a projections page that omitted
    ///     it would show a store with three subscriptions as a store with none.
    /// </remarks>
    [Fact]
    public async Task a_subscription_is_reported_alongside_the_projections()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "subs";
            options.Projections.Subscribe(new QuietSubscription());
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var statuses = await ((IEventStore)store).GetProjectionStatusesAsync(Token);
        var subscription = statuses.Single(x => x.ProjectionName == nameof(QuietSubscription));

        subscription.Lifecycle.ShouldBe(nameof(ProjectionLifecycle.Async));
        subscription.Shards.ShouldHaveSingleItem().State.ShouldBe("Unknown");
    }

    // ---- with a daemon this process hosts ----

    /// <summary>
    ///     A running daemon's shard reports <c>Running</c>, and the state comes from its tracker.
    /// </summary>
    /// <remarks>
    ///     Needs a real host, because <c>DocumentStore.RunningDaemons</c> is set by the hosted service —
    ///     which is also the point: that seam is the only thing in the process that knows a daemon
    ///     exists. Asserting against <c>Unknown</c> as well as for <c>Running</c> is what makes this
    ///     fail rather than pass vacuously if the tracker lookup is dropped.
    /// </remarks>
    [Fact]
    public async Task a_running_daemon_reports_its_shard_as_running()
    {
        using var database = TemporaryDatabase.Create("statuses-hosted");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.AutoCreateSchemaObjects = AutoCreate.All;
                o.Projections.Snapshot<StatusTally>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        foreach (var service in hosted)
        {
            await service.StartAsync(Token);
        }

        try
        {
            var store = provider.GetRequiredService<DocumentStore>();

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<StatusTally>(Guid.NewGuid(), new Counted(), new Counted());
                await session.SaveChangesAsync(Token);
            }

            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

            var statuses = await ((IEventStore)store).GetProjectionStatusesAsync(Token);
            var shard = statuses.Single(x => x.ProjectionName == nameof(StatusTally))
                .Shards.ShouldHaveSingleItem();

            shard.State.ShouldBe("Running");
            shard.ProcessedSequence.ShouldBe(2);
            shard.EventStoreSequence.ShouldBe(2);
            shard.Error.ShouldBeNull();
        }
        finally
        {
            foreach (var service in hosted)
            {
                await service.StopAsync(Token);
            }
        }
    }

    private string ShardIdentityFor(string projectionName)
        => _store.Options.Projections.AllShards()
            .Single(x => x.Name.Name == projectionName).Name.Identity;

    private async Task WriteProgressAsync(string shardIdentity, long sequence)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "insert into fi_event_progression (name, last_seq_id) values (@name, @seq) "
            + "on conflict (name) do update set last_seq_id = excluded.last_seq_id";
        command.Parameters.AddWithValue("@name", shardIdentity);
        command.Parameters.AddWithValue("@seq", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task AppendAsync(Guid streamId, int count)
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream<StatusTally>(streamId,
            Enumerable.Range(0, count).Select(object (_) => new Counted()).ToArray());
        await session.SaveChangesAsync(Token);
    }
}

public record Counted;

public class StatusTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public void Apply(Counted _) => Count++;
}

public class InlineTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public void Apply(Counted _) => Count++;
}

public class QuietSubscription : Fisher.Subscriptions.SubscriptionBase
{
    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
        => Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
}

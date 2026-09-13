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
///         <b>There is a shared suite now — <c>ProjectionStatusCompliance</c> (jasperfx#818) — and it
///         adopted Fisher's reading of the field these tests are shaped around.</b>
///         <c>ShardStatus.State</c> is a fact about the running daemon, and the failure this feature
///         invites is filling it with a plausible constant. Polecat reported every shard as
///         <c>"Stopped"</c> (polecat#200) — not a partial answer but a wrong one, because it is exactly
///         what a real stopped shard reports and it is the reading an operator acts on — and Marten
///         answered <c>Unknown</c> unconditionally, never reading a daemon at all. Both change
///         (polecat#589, marten#5383).
///     </para>
///     <para>
///         <b>These stay because the suite cannot see most of what they assert.</b> Its fixture drives
///         one hand-built store and one hosted one; the planted progression row, the deliberately
///         stale high-water row, and the subscription's survival on the lag surface below all need a
///         store arranged into a state a portable fixture has no vocabulary for. The one fact that
///         moved rather than stayed is the inventory ruling — see below.
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

        shard.State.ShouldBe(ShardStatusState.Unknown);
        shard.State.ShouldNotBe(ShardStatusState.Stopped);
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
        shard.State.ShouldBe(ShardStatusState.Unknown);
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

    /// <summary>
    ///     A registered subscription is not a projection and is not in this inventory — and its
    ///     progress is still reachable, one surface over.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>fisher#249 reversed this test</b>, which used to assert the opposite. The argument it
    ///         was written on is unchanged and was never wrong: a subscription genuinely is a daemon
    ///         shard with progress to report. What the shared ruling (jasperfx#818) decided is that this
    ///         is not the page for it — a projections page is where an operator asks about <em>read
    ///         models</em>, and a subscription has no document behind it, so a row for one is something
    ///         a reader cannot click through to.
    ///     </para>
    ///     <para>
    ///         <b>So this asserts the replacement as well as the omission</b>, because "dropped from a
    ///         list" and "no longer answerable" are different outcomes and only the first was ruled on.
    ///         <c>RegisteredShardNames()</c> against <c>FetchProjectionLagAsync</c> is the pairing
    ///         jasperfx#815 built for exactly this question, and it reports the subscription's shard
    ///         with the progress this page used to carry.
    ///     </para>
    ///     <para>
    ///         Asserted on the shards as well as on the top-level names, because a store that filtered
    ///         only the outer list would leave the subscription's shard inside somebody else's status —
    ///         which is the partial fix the shared suite is shaped to catch.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_subscription_is_not_in_the_projection_inventory_and_is_still_reachable()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "subs";
            options.Projections.Snapshot<StatusTally>(SnapshotLifecycle.Async);
            options.Projections.Subscribe(new QuietSubscription());
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var explorer = (IEventStore)store;
        var statuses = await explorer.GetProjectionStatusesAsync(Token);

        statuses.Select(x => x.ProjectionName).ShouldNotContain(nameof(QuietSubscription));
        statuses.SelectMany(x => x.Shards).Select(x => x.ShardName)
            .ShouldNotContain(x => x.StartsWith(nameof(QuietSubscription), StringComparison.Ordinal));

        // The registered projection beside it is still there, so this is the inventory rule rather
        // than an empty answer.
        statuses.Select(x => x.ProjectionName).ShouldContain(nameof(StatusTally));

        // ...and the subscription's progress is reachable through the surface designed for it.
        var registered = explorer.RegisteredShardNames();
        registered.Select(x => x.Name).ShouldContain(nameof(QuietSubscription));

        var lag = await ((IEventDatabase)store.Database).FetchProjectionLagAsync(registered, Token);
        lag.Select(x => x.Shard.Name).ShouldContain(nameof(QuietSubscription));
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

            shard.State.ShouldBe(ShardStatusState.Running);
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

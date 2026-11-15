using Fisher.Events.Messaging;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     JasperFx 3.0 turns <c>GateSideEffectsBehindPriorVersion</c> on by default for every event store
///     (jasperfx#1075), Fisher included, and fisher#469's migration guide owed an account of what that does to a
///     Fisher file. These two facts are that account, against a real file and the real daemon.
/// </summary>
/// <remarks>
///     <para>
///         <b>No schema is involved.</b> The gate reads the prior version's mark through
///         <c>IEventDatabase.AllProjectionProgress</c>, which is <c>fi_event_progression</c> as it already is, and
///         the version is part of the shard name (<c>GatedQuest:V2:All</c>), so the prior version's row survives
///         the bump with nothing migrated.
///     </para>
///     <para>
///         <b>Nothing in JasperFx's own gate tests reaches a store.</b> They drive the daemon against a
///         substituted database, so whether Fisher's progression names parse back into the shard the gate looks
///         for is only answered here. No compliance suite covers the gate.
///     </para>
///     <para>
///         <b>The wait is on the published message, never on non-staleness</b> (fisher#232): the progression row
///         commits inside the batch's transaction and the outbox's after-commit hook runs after it.
///     </para>
/// </remarks>
public class side_effect_gate_behind_prior_version : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("side-effect-gate");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task a_new_version_publishes_only_for_events_the_prior_version_never_processed()
    {
        await RunVersionOneOverTwoQuestsAsync();

        var published = await RunVersionTwoAsync(gate: null, thirdQuest: "Return the sword");

        // Version 2 replayed both earlier quests to build its own documents, with side effects suppressed up
        // to version 1's mark, and published only past it.
        published.ShouldBe(["Return the sword"]);
    }

    [Fact]
    public async Task turning_the_gate_off_republishes_the_whole_replay()
    {
        await RunVersionOneOverTwoQuestsAsync();

        var published = await RunVersionTwoAsync(gate: false, thirdQuest: "Return the sword");

        // Fisher 1's behaviour, and what a projection opts back into with the setting off.
        published.ShouldBe(["Destroy the ring", "Find the sword", "Return the sword"], ignoreOrder: true);
    }

    private async Task RunVersionOneOverTwoQuestsAsync()
    {
        var outbox = new CountingOutbox();

        await using var store = BuildStore(new GatedQuestProjection(1, gate: null), outbox);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await AppendQuestAsync(store, "Destroy the ring");
        await AppendQuestAsync(store, "Find the sword");

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await outbox.WaitForAsync(2);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    private async Task<string[]> RunVersionTwoAsync(bool? gate, string thirdQuest)
    {
        var outbox = new CountingOutbox();

        await using var store = BuildStore(new GatedQuestProjection(2, gate), outbox);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await AppendQuestAsync(store, thirdQuest);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            // The third quest is the last event, so once its message is out the shard has seen everything; the
            // whole replay has run by then, and anything it was going to publish has been published.
            await outbox.WaitForQuestAsync(thirdQuest);
        }
        finally
        {
            await daemon.StopAllAsync();
        }

        return outbox.Published;
    }

    private DocumentStore BuildStore(GatedQuestProjection projection, CountingOutbox outbox)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.MessageOutbox = outbox;
            options.Projections.Add(projection, ProjectionLifecycle.Async);
        });

    private static async Task AppendQuestAsync(DocumentStore store, string name)
    {
        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted(name));
        await session.SaveChangesAsync(Token);
    }

    private sealed class CountingOutbox : IMessageOutbox
    {
        private readonly List<string> _published = [];
        private readonly SemaphoreSlim _changed = new(0);

        public string[] Published
        {
            get
            {
                lock (_published)
                {
                    return _published.ToArray();
                }
            }
        }

        public ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
            => new(new Batch(this));

        public async Task WaitForAsync(int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(DaemonWait.Timeout);

            while (Published.Length < count)
            {
                await _changed.WaitAsync(timeout.Token);
            }
        }

        public async Task WaitForQuestAsync(string name)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(DaemonWait.Timeout);

            while (!Published.Contains(name))
            {
                await _changed.WaitAsync(timeout.Token);
            }
        }

        private void Record(IEnumerable<string> names)
        {
            lock (_published)
            {
                _published.AddRange(names);
            }

            _changed.Release();
        }

        // Messages count once their batch has committed, which is when a real bus would deliver them.
        private sealed class Batch(CountingOutbox outbox) : IMessageBatch
        {
            private readonly List<string> _pending = [];

            public ValueTask PublishAsync<T>(T message, string tenantId)
            {
                if (message is GatedQuestFinished finished)
                {
                    lock (_pending)
                    {
                        _pending.Add(finished.Name);
                    }
                }

                return ValueTask.CompletedTask;
            }

            public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
                => PublishAsync(message, metadata.TenantId);

            public Task BeforeCommitAsync(CancellationToken token) => Task.CompletedTask;

            public Task AfterCommitAsync(CancellationToken token)
            {
                string[] names;
                lock (_pending)
                {
                    names = _pending.ToArray();
                    _pending.Clear();
                }

                outbox.Record(names);
                return Task.CompletedTask;
            }
        }
    }
}

public record GatedQuestFinished(string Name);

public class GatedQuest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public void Apply(QuestStarted started) => Name = started.Name;
}

public class GatedQuestProjection : Fisher.Projections.SingleStreamProjection<GatedQuest, Guid>
{
    public GatedQuestProjection(uint version, bool? gate)
    {
        Version = version;

        // Left alone, the JasperFx 3.0 default applies.
        if (gate.HasValue)
        {
            GateSideEffectsBehindPriorVersion = gate.Value;
        }
    }

    public override ValueTask RaiseSideEffects(IDocumentSession operations, IEventSlice<GatedQuest> slice)
    {
        slice.PublishMessage(new GatedQuestFinished(slice.Snapshot?.Name ?? "unknown"));
        return ValueTask.CompletedTask;
    }
}

using System.Diagnostics;
using Fisher.Linq;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#434 — what jasperfx#953's bounded drain means for Fisher's subscription runner. A stopping
///     subscription now cancels the token of the range in flight when <c>StopAndDrainTimeout</c> expires,
///     so that cancellation can land anywhere in the range: before the commit, during it, or after it.
/// </summary>
/// <remarks>
///     <para>
///         The runner is driven directly with a range and a token the test cancels, so the cancellation
///         lands at an exact point instead of whenever a timer happens to fire. The two hooks are a
///         transaction participant — whose <c>BeforeCommitAsync</c> is the last thing inside the batch's
///         transaction, and whose <c>AfterCommitAsync</c> is the first thing after it — and the
///         subscription's own post-commit listener.
///     </para>
/// </remarks>
public class subscription_drain_cancellation : IAsyncLifetime
{
    private static readonly ShardName Shard = new("DrainProbe");

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("subscription-drain");
    private DocumentStore _store = null!;
    private List<IEvent> _events = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<DrainNote>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(stream, new DrainStarted("a"), new DrainStarted("b"), new DrainStarted("c"));
            await session.SaveChangesAsync(Token);
        }

        await using (var query = _store.QuerySession())
        {
            _events = (await query.Events.FetchStreamAsync(stream, token: Token)).ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private EventRange Range() => new(Shard, 0, _events[^1].Sequence, null!) { Events = _events };

    private Task RunAsync(ISubscription subscription, CancellationToken token)
        => ((ISubscriptionRunner<ISubscription>)_store).ExecuteAsync(subscription, _store.Database, Range(),
            ShardExecutionMode.Continuous, token);

    private async Task<long> ProgressAsync()
        => await _store.Database.ProjectionProgressFor(Shard, Token);

    private async Task<int> NotesAsync()
    {
        await using var query = _store.QuerySession();
        return (await query.Query<DrainNote>().ToListAsync(Token)).Count;
    }

    // ---------------------------------------------------------------- 1a: cancelled after the commit

    /// <remarks>
    ///     The bug this pins. Before fisher#434 the runner handed the post-commit listener the range's own
    ///     token. A drain timing out between the commit and the listener cancelled it, a listener that
    ///     honours its token — any HTTP call or publish does — gave up, and JasperFx's execution swallows
    ///     an <see cref="OperationCanceledException" /> during a stop. Nothing asked again: the progression
    ///     row had committed with the range, so the next owner of the shard starts after it. The side
    ///     effect of three committed events was lost without a word.
    /// </remarks>
    [Fact]
    public async Task a_drain_cancelled_after_the_commit_still_runs_the_post_commit_listener()
    {
        using var drain = new CancellationTokenSource();
        var listener = new TokenHonouringListener();
        var subscription = new ProbeSubscription(listener, new Probe { OnAfterCommit = drain.Cancel });

        await RunAsync(subscription, drain.Token);

        drain.IsCancellationRequested.ShouldBeTrue("the test's premise: the drain landed after the commit");
        listener.Delivered.ShouldBeTrue();
        listener.TokenWasCancelled.ShouldBeFalse();

        (await ProgressAsync()).ShouldBe(_events[^1].Sequence);
        (await NotesAsync()).ShouldBe(_events.Count);
    }

    [Fact]
    public async Task a_drain_cancelled_while_the_listener_runs_does_not_interrupt_it()
    {
        using var drain = new CancellationTokenSource();
        var listener = new TokenHonouringListener { WhileRunning = drain.Cancel };
        var subscription = new ProbeSubscription(listener, new Probe());

        await RunAsync(subscription, drain.Token);

        drain.IsCancellationRequested.ShouldBeTrue();
        listener.Delivered.ShouldBeTrue();
    }

    /// <remarks>
    ///     The batch's own post-commit step has the same shape — a participant, or the outbox a
    ///     projection publishes through, is told the write is durable after the commit — and gets the
    ///     same treatment. The first participant plays the drain; the second must still be told.
    /// </remarks>
    [Fact]
    public async Task a_drain_cancelled_after_the_commit_still_tells_every_participant()
    {
        using var drain = new CancellationTokenSource();
        var second = new Probe();
        var subscription = new ProbeSubscription(new TokenHonouringListener(),
            new Probe { OnAfterCommit = drain.Cancel }, second);

        await RunAsync(subscription, drain.Token);

        second.AfterCommitRan.ShouldBeTrue();
        second.AfterCommitTokenWasCancelled.ShouldBeFalse();
    }

    // ---------------------------------------------------------------- 1b: cancelled mid-commit

    /// <remarks>
    ///     <para>
    ///         The latest point a drain can cancel a range and still stop it: inside the batch's
    ///         transaction, after the subscription's writes and the progression row, before
    ///         <c>COMMIT</c>. Everything rolls back together — no notes, no progression — the file is
    ///         intact, and the single write lock is released at once rather than when the busy timeout
    ///         would have expired for the next writer.
    ///     </para>
    ///     <para>
    ///         Then the range runs again, as the next owner of the shard would run it. That it succeeds
    ///         from the same floor is the proof progression rolled back: the progression write is
    ///         guarded on its floor (fisher#402), so a range whose progress had leaked would be refused.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_drain_cancelled_mid_commit_rolls_back_everything_and_the_range_resumes()
    {
        using var drain = new CancellationTokenSource();
        var listener = new TokenHonouringListener();
        var subscription = new ProbeSubscription(listener, new Probe { CancelBeforeCommit = drain });

        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(subscription, drain.Token));

        listener.Delivered.ShouldBeFalse();
        (await ProgressAsync()).ShouldBe(0);
        (await NotesAsync()).ShouldBe(0);

        await using (var connection = new SqliteConnection(_database.ConnectionString))
        {
            await connection.OpenAsync(Token);

            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "pragma integrity_check";
            (await integrity.ExecuteScalarAsync(Token)).ShouldBe("ok");

            // The write lock is free now: with no busy wait at all, BEGIN IMMEDIATE either gets it or
            // fails on the spot with SQLITE_BUSY.
            await using var noWait = connection.CreateCommand();
            noWait.CommandText = "pragma busy_timeout = 0";
            await noWait.ExecuteNonQueryAsync(Token);

            var stopwatch = Stopwatch.StartNew();
            await using (var transaction = connection.BeginTransaction(deferred: false))
            {
                await transaction.RollbackAsync(Token);
            }

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        }

        // An application write right behind it is not kept waiting either.
        var writing = Stopwatch.StartNew();
        await using (var session = _store.LightweightSession())
        {
            session.Store(new DrainNote { Id = Guid.NewGuid(), Text = "application" });
            await session.SaveChangesAsync(Token);
        }

        writing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        // The next owner runs the same range from the same floor.
        var rerun = new TokenHonouringListener();
        await RunAsync(new ProbeSubscription(rerun, new Probe()), Token);

        rerun.Delivered.ShouldBeTrue();
        (await ProgressAsync()).ShouldBe(_events[^1].Sequence);
        (await NotesAsync()).ShouldBe(_events.Count + 1);
    }

    // ---------------------------------------------------------------- doubles

    private sealed class ProbeSubscription(TokenHonouringListener listener, params Probe[] probes) : ISubscription
    {
        public Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller,
            IDocumentSession operations, CancellationToken cancellationToken)
        {
            foreach (var @event in page.Events)
            {
                operations.Store(new DrainNote { Id = Guid.NewGuid(), Text = ((DrainStarted)@event.Data).Name });
            }

            foreach (var probe in probes)
            {
                operations.AddTransactionParticipant(probe);
            }

            return Task.FromResult<IDaemonChangeListener>(listener);
        }
    }

    /// <summary>A post-commit listener that behaves like real external work: it honours its token.</summary>
    private sealed class TokenHonouringListener : IDaemonChangeListener
    {
        public Action? WhileRunning { get; init; }
        public bool Delivered { get; private set; }
        public bool TokenWasCancelled { get; private set; }

        public async Task AfterCommitAsync(CancellationToken token)
        {
            TokenWasCancelled = token.IsCancellationRequested;

            WhileRunning?.Invoke();
            await Task.Delay(10, token);

            token.ThrowIfCancellationRequested();
            Delivered = true;
        }
    }

    private sealed class Probe : ITransactionParticipant
    {
        public CancellationTokenSource? CancelBeforeCommit { get; init; }
        public Action? OnAfterCommit { get; init; }

        public bool AfterCommitRan { get; private set; }
        public bool AfterCommitTokenWasCancelled { get; private set; }

        public async Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken token)
        {
            if (CancelBeforeCommit is null)
            {
                return;
            }

            // The drain times out with the writes and the progression row inside the open transaction.
            await CancelBeforeCommit.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        }

        public Task AfterCommitAsync(CancellationToken token)
        {
            AfterCommitRan = true;
            AfterCommitTokenWasCancelled = token.IsCancellationRequested;
            OnAfterCommit?.Invoke();
            return Task.CompletedTask;
        }
    }
}

public record DrainStarted(string Name);

public class DrainNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

using System.Reflection;
using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;
using Shouldly;
using Weasel.Sqlite.Functions;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#576 — Microsoft.Data.Sqlite's pool can hand one native connection to two open
///     <see cref="SqliteConnection" />s, so Fisher serializes every open of a file's pool.
/// </summary>
/// <remarks>
///     <para>
///         <b>The intermittent this closes was three different failures with one cause.</b> Under load,
///         <c>dcb_append_condition_compliance.concurrently_allocated_invoice_numbers_are_gapless_and_unique</c>
///         failed in 3 of 400 runs: "This SqliteTransaction has completed" from the first statement
///         after <c>BEGIN IMMEDIATE</c>, "unable to delete/modify user-function due to active
///         statements" out of a session's <c>Dispose</c>, and a process crash with a
///         <c>NullReferenceException</c> in SQLitePCLRaw's rollback hook bridge. A probe recording each
///         open connection's native handle found two open connections sharing one handle in the one
///         failing run out of 1200, and in none of the passing runs. See
///         <see cref="SerializedOpenDataSource" /> for the race inside the pool.
///     </para>
///     <para>
///         <b>Neither half races anything.</b> The premise plants the state the race leaves for an
///         instant and shows what the pool does with it. The regression tests hold one open inside the
///         lock with a SQL function whose registration blocks, which Weasel runs as part of every open,
///         and show that a second open or a pool clear cannot get in. Without the lock the second
///         open enters at once, so the failing direction does not depend on timing.
///     </para>
/// </remarks>
public class serialized_connection_opens : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("serialized-opens");
    private readonly BlockingRegistration _blocker = new();
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Long enough that an open that was going to get past the lock would have done so. Only the
    ///     passing direction waits this long; without the lock the second open enters immediately.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Functions.Add(_blocker);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _blocker.Release();
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     The premise, as a fact about Microsoft.Data.Sqlite rather than about Fisher.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SqliteConnectionInternal.Activate</c> sets <c>_active</c> and then the weak reference to
    ///         its outer connection, outside the pool's lock. For the instant between them the pool's
    ///         <c>Leaked</c> test is true of a connection that is being opened. This plants exactly that
    ///         state on a connection that stays open, then opens two more, which is what it takes for the
    ///         pool to run its leaked-connection scan: the third open finds the pool empty with an even
    ///         count. The scan reclaims the first connection's handle and hands it to the third.
    ///     </para>
    ///     <para>
    ///         Reflection into the provider's privates, deliberately. If this starts failing on a
    ///         Microsoft.Data.Sqlite upgrade, read what changed in the pool before touching
    ///         <see cref="SerializedOpenDataSource" />: it may be the fix that lets it go.
    ///     </para>
    /// </remarks>
    [Fact]
    public void a_connection_caught_mid_open_is_handed_to_a_second_opener()
    {
        // A pool of its own, so nothing else in the process opens over it.
        var connectionString = new SqliteConnectionStringBuilder(_database.ConnectionString)
        {
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 31
        }.ToString();

        var first = new SqliteConnection(connectionString);
        var second = new SqliteConnection(connectionString);
        var third = new SqliteConnection(connectionString);

        try
        {
            first.Open();
            PlantMidActivateState(first);

            second.Open();
            third.Open();

            var firstHandle = first.Handle!.DangerousGetHandle();

            first.State.ShouldBe(System.Data.ConnectionState.Open);
            third.Handle!.DangerousGetHandle().ShouldBe(firstHandle,
                "the pool reclaimed a connection that was still open and gave its native handle to another opener");
        }
        finally
        {
            // Two outer connections over one internal: the pool's bookkeeping is now wrong for this
            // connection string, so clear it before and after closing them.
            using (var probe = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(probe);
            }

            third.Dispose();
            second.Dispose();
            first.Dispose();

            using (var probe = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(probe);
            }
        }
    }

    [Fact]
    public async Task a_second_open_of_the_same_file_waits_for_the_first()
    {
        _blocker.Arm();

        var first = Task.Run(async () => await _store.Database.OpenConnectionAsync(Token), Token);
        _blocker.FirstEntered.Wait(TimeSpan.FromSeconds(30), Token).ShouldBeTrue("the first open never started");

        var second = Task.Run(async () => await _store.Database.OpenConnectionAsync(Token), Token);

        _blocker.SecondEntered.Wait(Window, Token)
            .ShouldBeFalse("a second open got into the provider while the first was still inside it");

        _blocker.Release();

        await using (await first)
        await using (await second)
        {
        }

        _blocker.SecondEntered.IsSet.ShouldBeTrue();
        _blocker.MostAtOnce.ShouldBe(1);
    }

    /// <summary>
    ///     A connection handed out unopened, which Weasel.Storage's session-free projection loads open for
    ///     themselves, takes the same lock.
    /// </summary>
    [Fact]
    public async Task a_connection_opened_by_its_caller_waits_too()
    {
        _blocker.Arm();

        var first = Task.Run(async () => await _store.Database.OpenConnectionAsync(Token), Token);
        _blocker.FirstEntered.Wait(TimeSpan.FromSeconds(30), Token).ShouldBeTrue("the first open never started");

        // On a task of its own: DbConnection.OpenAsync runs Open synchronously on the caller's thread.
        await using var unopened = _store.Database.DataSource.CreateConnection();
        var opened = Task.Run(() => unopened.OpenAsync(Token), Token);

        (await Task.WhenAny(opened, Task.Delay(Window, Token))).ShouldNotBe(opened,
            "a connection opened by its caller got into the provider while another open was inside it");

        _blocker.Release();

        await using (await first)
        {
        }

        await opened;
        unopened.State.ShouldBe(System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task clearing_the_pool_waits_for_an_open_in_flight()
    {
        _blocker.Arm();

        var open = Task.Run(async () => await _store.Database.OpenConnectionAsync(Token), Token);
        _blocker.FirstEntered.Wait(TimeSpan.FromSeconds(30), Token).ShouldBeTrue("the open never started");

        var cleared = Task.Run(() => SerializedOpenDataSource.ClearPool(_database.ConnectionString), Token);

        (await Task.WhenAny(cleared, Task.Delay(Window, Token))).ShouldNotBe(cleared,
            "the pool was cleared, and its leaked-connection scan run, while an open was still inside the provider");

        _blocker.Release();

        await using (await open)
        {
        }

        await cleared;
    }

    /// <summary>
    ///     fisher#587: an async open that has to wait for the gate waits on the caller's own thread and
    ///     returns a completed task. It never hands the wait to the thread pool.
    /// </summary>
    /// <remarks>
    ///     This is the starvation mechanism, pinned without depending on timing. The first gate was a
    ///     <c>SemaphoreSlim</c>: a contended <c>WaitAsync</c> returned an incomplete task, so the
    ///     open finished, and later released the gate, only when the pool ran its continuation. Meanwhile
    ///     synchronous opens blocked pool threads in <c>Wait</c>. A burst of synchronous Hi-Lo stores
    ///     stalled for about 30 s. The caller here is a dedicated thread, so the test does not use the pool
    ///     itself.
    /// </remarks>
    [Fact]
    public async Task a_contended_async_open_completes_before_it_returns()
    {
        _blocker.Arm();

        var first = Task.Run(async () => await _store.Database.OpenConnectionAsync(Token), Token);
        _blocker.FirstEntered.Wait(TimeSpan.FromSeconds(30), Token).ShouldBeTrue("the first open never started");

        var completedOnReturn = false;
        System.Data.Common.DbConnection? opened = null;
        var caller = new Thread(() =>
        {
            var task = _store.Database.DataSource.OpenConnectionAsync(Token);
            completedOnReturn = task.IsCompleted;
            opened = task.AsTask().GetAwaiter().GetResult();
        }) { IsBackground = true };
        caller.Start();

        // The caller is inside the gate's wait now. Let the first open finish so it can get in.
        Thread.Sleep(Window);
        _blocker.Release();

        caller.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue("the second open never finished");

        await using (await first)
        await using (opened)
        {
        }

        completedOnReturn.ShouldBeTrue(
            "a contended async open returned an incomplete task, so finishing it needs a thread-pool thread");
    }

    /// <summary>
    ///     Make <paramref name="connection" />'s pooled internal look the way it does between the two
    ///     writes in <c>SqliteConnectionInternal.Activate</c>: active, with no outer connection.
    /// </summary>
    private static void PlantMidActivateState(SqliteConnection connection)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        var inner = typeof(SqliteConnection).GetField("_innerConnection", flags)?.GetValue(connection)
                    ?? throw new InvalidOperationException(
                        "SqliteConnection._innerConnection is gone; the pool has changed shape (fisher#576).");

        var outer = inner.GetType().GetField("_outerConnection", flags)?.GetValue(inner) as WeakReference<SqliteConnection>
                    ?? throw new InvalidOperationException(
                        "SqliteConnectionInternal._outerConnection is gone; the pool has changed shape (fisher#576).");

        outer.SetTarget(null!);
    }

    /// <summary>
    ///     A SQL function whose registration, which Weasel runs inside every open, can be held.
    /// </summary>
    private sealed class BlockingRegistration() : SqliteFunction("fi_test_blocking_registration")
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;
        private int _calls;
        private int _inside;
        private int _mostAtOnce;

        public ManualResetEventSlim FirstEntered { get; } = new(false);
        public ManualResetEventSlim SecondEntered { get; } = new(false);
        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public override void Register(SqliteConnection connection)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            var inside = Interlocked.Increment(ref _inside);
            int most;
            while (inside > (most = Volatile.Read(ref _mostAtOnce)) &&
                   Interlocked.CompareExchange(ref _mostAtOnce, inside, most) != most)
            {
            }

            try
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    FirstEntered.Set();
                    _release.Wait(TimeSpan.FromSeconds(30));
                }
                else
                {
                    SecondEntered.Set();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite.Functions;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     <b>Benchmark-only.</b> fisher#547 round 2: replace SQLite's busy handling on every connection a
///     store opens, so racing writers can be measured with a different wait policy and nothing else
///     changed.
/// </summary>
/// <remarks>
///     <para>
///         SQLitePCLRaw 3.0.5 exposes <c>sqlite3_busy_timeout</c> but not <c>sqlite3_busy_handler</c>, so
///         the handler is installed by calling the bundled <c>e_sqlite3</c> library directly, with an
///         <see cref="UnmanagedCallersOnlyAttribute" /> callback, on the native handle of each connection.
///     </para>
///     <para>
///         It rides on <see cref="StoreOptions.Functions" />: Weasel's data source applies the connection
///         PRAGMAs (<c>busy_timeout</c> among them, which installs SQLite's default handler) and then
///         registers every function in that registry, on every open. <see cref="Installer" /> is a
///         "function" whose registration installs the handler instead, so it always runs after the
///         PRAGMA and replaces it — on application sessions and the async daemon's connections alike.
///     </para>
/// </remarks>
public static class BusyHandlers
{
    public enum Policy
    {
        /// <summary>
        ///     A copy of SQLite's default handler (<c>sqliteDefaultBusyCallback</c>, what
        ///     <c>PRAGMA busy_timeout</c> installs): sleeps of 1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50 and
        ///     then 100 ms, up to the timeout. Counts every invocation, which is how the mechanism is checked.
        /// </summary>
        Mimic,

        /// <summary><see cref="Thread.Yield" /> on every invocation, until the deadline.</summary>
        Yield,

        /// <summary>A 50 µs busy-wait on every invocation, until the deadline.</summary>
        Spin50Us,

        /// <summary>A fixed 1 ms <see cref="Thread.Sleep(int)" /> on every invocation, until the deadline.</summary>
        Sleep1Ms,

        /// <summary>
        ///     Give up at once (what <c>busy_timeout = 0</c> would do), so whatever sits above SQLite —
        ///     Microsoft.Data.Sqlite's own retry, then Fisher's Polly pipeline — has to handle the wait.
        /// </summary>
        None
    }

    /// <summary>The whole deadline, matching Weasel's default <c>busy_timeout</c> of 5000 ms.</summary>
    public const int TimeoutMs = 5000;

    private static readonly int[] DefaultDelays = [1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50, 100];
    private static readonly int[] DefaultTotals = [0, 1, 3, 8, 18, 33, 53, 78, 103, 128, 178, 228];

    private static readonly Stats[] _stats = [new(), new(), new(), new(), new()];

    [ThreadStatic] private static long _episodeStarted;

    public static Stats StatsFor(Policy policy) => _stats[(int)policy];

    /// <summary>What the handler did, summed over every connection that had it installed.</summary>
    public sealed class Stats
    {
        private long _invocations;
        private long _episodes;
        private long _gaveUp;
        private long _ticksInHandler;

        /// <summary>Calls to the handler (SQLite's <c>count</c> argument starts at 0 per wait).</summary>
        public long Invocations => Interlocked.Read(ref _invocations);

        /// <summary>Waits: invocations with <c>count == 0</c>.</summary>
        public long Episodes => Interlocked.Read(ref _episodes);

        /// <summary>Waits where the handler returned 0 and SQLITE_BUSY went up to the caller.</summary>
        public long GaveUp => Interlocked.Read(ref _gaveUp);

        /// <summary>Wall time spent inside the handler (sleeping, spinning or yielding), all threads.</summary>
        public double MillisecondsInHandler => Interlocked.Read(ref _ticksInHandler) * 1000d / Stopwatch.Frequency;

        public void Reset()
        {
            Interlocked.Exchange(ref _invocations, 0);
            Interlocked.Exchange(ref _episodes, 0);
            Interlocked.Exchange(ref _gaveUp, 0);
            Interlocked.Exchange(ref _ticksInHandler, 0);
        }

        internal void Invoked(int count)
        {
            Interlocked.Increment(ref _invocations);
            if (count == 0) Interlocked.Increment(ref _episodes);
        }

        internal void GiveUp() => Interlocked.Increment(ref _gaveUp);

        internal void Spent(long ticks) => Interlocked.Add(ref _ticksInHandler, ticks);
    }

    /// <summary>
    ///     Install <paramref name="policy" /> on every connection the store opens. Must be called while
    ///     configuring the store's options.
    /// </summary>
    public static void InstallOn(StoreOptions options, Policy policy)
        => options.Functions.Add(new Installer(policy));

    /// <summary>Install <paramref name="policy" /> on one already-open connection.</summary>
    public static unsafe void InstallOn(SqliteConnection connection, Policy policy)
    {
        var db = connection.Handle!.DangerousGetHandle();
        var rc = sqlite3_busy_handler(db, &OnBusy, (IntPtr)(int)policy);
        if (rc != 0)
        {
            throw new InvalidOperationException($"sqlite3_busy_handler returned {rc}");
        }
    }

    private sealed class Installer(Policy policy) : SqliteFunction($"fisher_bench_busy_{policy}".ToLowerInvariant())
    {
        public override void Register(SqliteConnection connection) => InstallOn(connection, policy);
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int sqlite3_busy_handler(IntPtr db, delegate* unmanaged[Cdecl]<IntPtr, int, int> callback,
        IntPtr userData);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBusy(IntPtr userData, int count)
    {
        var policy = (Policy)(int)userData;
        var stats = _stats[(int)policy];
        stats.Invoked(count);

        var now = Stopwatch.GetTimestamp();
        if (count == 0) _episodeStarted = now;

        switch (policy)
        {
            case Policy.Mimic:
            {
                // sqliteDefaultBusyCallback, transcribed.
                int delay, prior;
                if (count < DefaultDelays.Length)
                {
                    delay = DefaultDelays[count];
                    prior = DefaultTotals[count];
                }
                else
                {
                    delay = DefaultDelays[^1];
                    prior = DefaultTotals[^1] + delay * (count - (DefaultDelays.Length - 1));
                }

                if (prior + delay > TimeoutMs)
                {
                    delay = TimeoutMs - prior;
                    if (delay <= 0)
                    {
                        stats.GiveUp();
                        return 0;
                    }
                }

                Thread.Sleep(delay);
                break;
            }

            case Policy.None:
                stats.GiveUp();
                return 0;

            default:
            {
                if (Stopwatch.GetElapsedTime(_episodeStarted, now).TotalMilliseconds > TimeoutMs)
                {
                    stats.GiveUp();
                    return 0;
                }

                switch (policy)
                {
                    case Policy.Yield:
                        Thread.Yield();
                        break;

                    case Policy.Spin50Us:
                        var until = now + Stopwatch.Frequency / 20_000;
                        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
                        break;

                    case Policy.Sleep1Ms:
                        Thread.Sleep(1);
                        break;
                }

                break;
            }
        }

        stats.Spent(Stopwatch.GetTimestamp() - now);
        return 1;
    }
}

using System.Diagnostics;

namespace Fisher.Diagnostics;

/// <summary>
///     An opt-in, allocation-free, in-memory ring buffer for tracing the async daemon.
/// </summary>
/// <remarks>
///     <para>
///         Built for fisher#13, an intermittent rebuild failure that <b>disappears under
///         instrumentation</b> — probes writing to a file made twelve consecutive runs pass at a point
///         where the bug was demonstrably still present. So nothing here does I/O, allocates, or takes
///         a lock on the recording path: an entry is a struct written into a preallocated array at an
///         interlocked index, and the buffer is dumped once, after the fact.
///     </para>
///     <para>
///         Off unless <c>FISHER_DAEMON_TRACE</c> is set, and <see cref="Enabled" /> is a static readonly
///         bool so the JIT removes the call entirely when it is not.
///     </para>
/// </remarks>
internal static class DaemonTrace
{
    private const int Capacity = 8192;

    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("FISHER_DAEMON_TRACE") is not null;

    private static readonly Entry[] Entries = new Entry[Capacity];
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static int _next = -1;

    internal readonly record struct Entry(long Ticks, int Thread, string? Tag, string? Detail,
        long A, long B, long C);

    internal static void Record(string tag, string? detail = null, long a = 0, long b = 0, long c = 0)
    {
        if (!Enabled)
        {
            return;
        }

        var index = Interlocked.Increment(ref _next);

        // Wrap rather than grow. A rebuild is a few hundred entries; the cap only matters if the whole
        // suite traces, in which case the tail is what is wanted anyway.
        Entries[index % Capacity] = new Entry(
            Stopwatch.GetTimestamp() - Origin, Environment.CurrentManagedThreadId, tag, detail, a, b, c);
    }

    /// <summary>
    ///     Every entry recorded so far, oldest first.
    /// </summary>
    internal static IReadOnlyList<Entry> Dump()
    {
        var count = Volatile.Read(ref _next) + 1;

        if (count <= 0)
        {
            return [];
        }

        var take = Math.Min(count, Capacity);
        var start = count - take;

        var results = new List<Entry>(take);
        for (var i = 0; i < take; i++)
        {
            results.Add(Entries[(start + i) % Capacity]);
        }

        return results;
    }

    internal static string Render()
    {
        var lines = Dump().Select(x =>
            $"{x.Ticks,12} t{x.Thread,-3} {x.Tag,-28} a={x.A,-6} b={x.B,-6} c={x.C,-6} {x.Detail}");

        return string.Join(Environment.NewLine, lines);
    }
}

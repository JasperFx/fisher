using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Replaces SQLite's default busy handler on every connection Fisher opens with one that sleeps a
///     fixed 1 ms between tries, up to the same <c>busy_timeout</c> deadline (fisher#547).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> <c>PRAGMA busy_timeout</c> installs SQLite's default handler, which sleeps 1, 2, 5,
///         10, 15, 20, 25, 25, 25, 50, 50 and then 100 ms between tries. A writer waiting at
///         <c>BEGIN IMMEDIATE</c> can therefore sleep for tens of milliseconds after the lock it wants
///         was released: one commit holds the lock for about 0.07 ms, yet the measured waits ran to
///         130 ms. Round 2 of the fisher#547 benchmark (#586) confirmed this is the whole cost of
///         racing writers, and a fixed 1 ms sleep gave 2.1x the throughput at 8 writers and 3.3x at 16,
///         at the same CPU. The numbers are in <c>src/Fisher.Benchmarks/Results.md</c>.
///     </para>
///     <para>
///         <b>No semantic change.</b> Only the sleep between tries changes. The deadline is
///         <see cref="Weasel.Sqlite.SqlitePragmaSettings.BusyTimeout" />, as before, and the handler
///         still gives up with <c>SQLITE_BUSY</c> once it passes. Because it is installed at open, it
///         covers application sessions, enlisted sessions, Hi-Lo and the async daemon alike. The
///         <c>Channel&lt;T&gt;</c> single-writer queue measured alongside it was not adopted: it moves a
///         failure away from the call, loses the caller's async flow, and serializing raised hot-stream
///         version conflicts to 75–93 %.
///     </para>
///     <para>
///         <b>Why a P/Invoke.</b> SQLitePCLRaw 3.0.5 exposes <c>sqlite3_busy_timeout</c> but not
///         <c>sqlite3_busy_handler</c>. The function is looked up in the same native library
///         SQLitePCLRaw loaded, and only when its provider is <c>e_sqlite3</c>, the bundle Fisher
///         references. Calling one library's function on another library's connection handle would be
///         memory corruption, so with any other provider, or wherever the export cannot be found, the
///         connection keeps SQLite's default handler and nothing else changes.
///     </para>
///     <para>
///         A connection whose <c>BusyTimeout</c> is zero or less is left alone: that means "no busy
///         handler" to SQLite and it stays that way. A connection the application hands to a session
///         (<c>SessionOptions.ForConnection</c>) is the application's, and it is left alone too.
///     </para>
/// </remarks>
internal static unsafe class SqliteBusyHandler
{
    private const string LibraryName = "e_sqlite3";

    private static readonly delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<IntPtr, int, int>, IntPtr, int>
        _install = resolve();

    // The handler runs synchronously on the thread stepping the statement, so the start of the current
    // wait can live in a thread static. SQLite passes count == 0 on the first call of each wait.
    [ThreadStatic] private static long _waitStarted;
    [ThreadStatic] private static int _calls;

    /// <summary>Whether the handler can be installed in this process.</summary>
    internal static bool IsAvailable => _install != null;

    /// <summary>
    ///     How many times the handler has run on the calling thread. For tests: the handler runs on the
    ///     thread that is waiting, so a synchronous statement's waits are counted on its own thread.
    /// </summary>
    internal static int CallsOnThisThread => _calls;

    /// <summary>
    ///     Install the 1 ms handler on <paramref name="connection" /> with a deadline of
    ///     <paramref name="timeoutMilliseconds" />. Returns false, and changes nothing, when the handler is
    ///     not available or the timeout is zero or less.
    /// </summary>
    internal static bool TryInstall(SqliteConnection connection, int timeoutMilliseconds)
    {
        if (_install == null || timeoutMilliseconds <= 0) return false;

        var handle = connection.Handle;
        if (handle is null || handle.IsInvalid) return false;

        var rc = _install(handle.DangerousGetHandle(), &onBusy, timeoutMilliseconds);
        return rc == 0;
    }

    private static delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<IntPtr, int, int>, IntPtr, int> resolve()
    {
        try
        {
            // The connection handles come from SQLitePCLRaw's provider, so only call into the same library.
            if (!string.Equals(SQLitePCL.raw.GetNativeLibraryName(), LibraryName, StringComparison.Ordinal))
            {
                return null;
            }

            // Resolved the way the provider's own DllImports are, so this is the library already loaded.
            if (!NativeLibrary.TryLoad(LibraryName, typeof(SQLitePCL.raw).Assembly, null, out var library))
            {
                return null;
            }

            if (!NativeLibrary.TryGetExport(library, "sqlite3_busy_handler", out var export))
            {
                return null;
            }

            return (delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<IntPtr, int, int>, IntPtr, int>)export;
        }
        catch
        {
            // No provider set, a platform without dynamic loading: keep SQLite's default handler.
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int onBusy(IntPtr timeoutMilliseconds, int count)
    {
        // An exception must never escape into native code.
        try
        {
            _calls++;

            var now = Stopwatch.GetTimestamp();
            if (count == 0) _waitStarted = now;

            if (Stopwatch.GetElapsedTime(_waitStarted, now).TotalMilliseconds >= (long)timeoutMilliseconds)
            {
                return 0;
            }

            Thread.Sleep(1);
            return 1;
        }
        catch
        {
            return 0;
        }
    }
}

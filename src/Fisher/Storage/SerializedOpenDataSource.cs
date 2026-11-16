using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;
using Weasel.Sqlite.Functions;

namespace Fisher.Storage;

/// <summary>
///     Weasel's <see cref="SqliteDataSource" />, with every open of one connection string serialized
///     (fisher#576).
/// </summary>
/// <remarks>
///     <para>
///         <b>This works around a race in Microsoft.Data.Sqlite's connection pool, and the race hands one
///         native connection to two <see cref="SqliteConnection" />s at once.</b>
///         <c>SqliteConnectionInternal.Activate</c> sets <c>_active = true</c> and only then sets the weak
///         reference to its outer connection, and it does so outside the pool's lock. Its
///         <c>Leaked</c> property is <c>_active</c> with no live outer connection. So a second
///         <c>Open</c> that finds the pool empty runs <c>ReclaimLeakedConnections</c>, and if it does
///         that between those two writes it sees a connection being opened as leaked. It deactivates
///         it, pushes it back onto the pool, and the next opener gets the same native handle as the
///         session that is about to use it.
///     </para>
///     <para>
///         <b>Everything after that is one handle with two owners, and it fails three ways.</b> Measured
///         against Microsoft.Data.Sqlite 10.0.9 and SQLitePCLRaw 3.0.5. The
///         <c>concurrently_allocated_invoice_numbers_are_gapless_and_unique</c> compliance fact failed
///         in 3 of 400 runs under load, and the three failures were different:
///     </para>
///     <list type="bullet">
///         <item>
///             One owner's <c>ROLLBACK</c> ended the other's transaction and fired its rollback hook. The
///             victim then threw <c>This SqliteTransaction has completed</c> on its next statement, from
///             the append planner or, in fisher#576's CI run, the tag writer.
///         </item>
///         <item>
///             One owner closed the handle while the other had a statement running, so unregistering
///             Fisher's SQL function failed with <c>unable to delete/modify user-function due to active
///             statements</c>, out of <c>Dispose</c>.
///         </item>
///         <item>
///             Two threads replaced the rollback hook on the same handle at once. SQLitePCLRaw freed a
///             <c>GCHandle</c> that SQLite still pointed at, and the process died of a
///             <c>NullReferenceException</c> in <c>rollback_hook_bridge_impl</c>.
///         </item>
///     </list>
///     <para>
///         <b>The mechanism was confirmed, not inferred.</b> A probe recording each open connection's
///         native handle found a handle shared by two open connections in the one failing run out of
///         1200 and in none of the 1199 that passed, and it was the handle whose transaction had
///         "completed". A stand-alone loop with nothing of Fisher in it, twelve threads opening and
///         closing connections over a fresh pool, hands out an in-use handle 66 to 132 times in 200
///         pools. With every <c>Open</c> taken under one lock it does so zero times.
///     </para>
///     <para>
///         <b>Every reclaim scan runs inside an <c>Open</c> or a <c>ClearPool</c>, and every
///         <c>Activate</c> runs inside an <c>Open</c>.</b> So serializing those two per connection
///         string closes the race. The pool is keyed by the exact connection string, and so is
///         <see cref="GateFor" />, which is static so that two stores, or a store and a tenant registry,
///         over one file share it. The lock covers Weasel's whole open, PRAGMAs and function
///         registration included, because that is the only override point there is. Those run on the
///         new connection without taking a file lock, except the database-scoped PRAGMAs, which Weasel
///         applies once per data source.
///     </para>
///     <para>
///         ⚠️ <b>It narrows the race and does not close it for connections Fisher does not open.</b>
///         A connection handed out unopened (<c>CreateConnection</c>) takes the lock in its own
///         <c>Open</c>. But Weasel's migrations open their own <c>new SqliteConnection(connectionString)</c>,
///         and so can application code. An open of one of those can still race with a Fisher open over the same
///         pool. Those are rare next to session opens, which is where the failure was found. This class
///         should move into Weasel.Sqlite's <see cref="SqliteDataSource" /> and go once
///         Microsoft.Data.Sqlite publishes the connection before activating it under the pool lock.
///     </para>
///     <para>
///         ⚠️ <b>The lock is a plain <see cref="Lock" /> held across a wholly synchronous open, never a
///         <see cref="SemaphoreSlim" /> and never across an <c>await</c></b> (fisher#587). The first version
///         used <c>WaitAsync</c> for async opens and a blocking <c>Wait</c> for sync ones. The
///         async holders needed a thread-pool thread for the continuation that released the gate, while
///         the sync waiters held pool threads blocked on it. A burst of synchronous Hi-Lo stores (each
///         sequence advance opens synchronously) then stalled for 28–34 s. Microsoft.Data.Sqlite's open and
///         Weasel's PRAGMAs and function registration are all synchronous underneath, so the async override
///         runs <c>base.OpenDbConnection()</c> under the lock and returns a completed task: the holder
///         always finishes on its own thread, and no waiter needs anything from the pool to get the lock.
///         The cost is that a cancellation token is checked before the open, not during it. An open
///         cannot be interrupted anyway.
///     </para>
/// </remarks>
internal sealed class SerializedOpenDataSource : SqliteDataSource
{
    private static readonly ConcurrentDictionary<string, Lock> _gates = new(StringComparer.Ordinal);

    private readonly Lock _gate;

    public SerializedOpenDataSource(string connectionString, SqlitePragmaSettings? pragmaSettings = null,
        SqliteFunctionRegistry? functions = null, SqliteExtensionSettings? extensions = null)
        : base(connectionString, pragmaSettings, functions, extensions)
    {
        _gate = GateFor(connectionString);
    }

    /// <summary>
    ///     The lock every Fisher open of <paramref name="connectionString" />'s pool takes.
    /// </summary>
    internal static Lock GateFor(string connectionString)
        => _gates.GetOrAdd(connectionString, static _ => new Lock());

    /// <summary>
    ///     <see cref="SqliteConnection.ClearPool" />, under the same lock as the opens: clearing a pool
    ///     runs the same reclaim scan an open does.
    /// </summary>
    internal static void ClearPool(string connectionString)
    {
        lock (GateFor(connectionString))
        {
            using var pooled = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(pooled);
        }
    }

    /// <summary>
    ///     An unopened connection whose <c>Open</c> takes the same lock, for callers that open it
    ///     themselves (Weasel.Storage's session-free projection loads, through
    ///     <c>IStorageDatabase.CreateStorageConnection</c>).
    /// </summary>
    protected override DbConnection CreateDbConnection()
    {
        // The base's only side effect is the in-memory keep-alive connection; the connection it returns
        // is a plain one that would open outside the lock.
        using (base.CreateDbConnection())
        {
        }

        return new GatedConnection(ConnectionString, _gate);
    }

    // Synchronous on purpose, under the lock (fisher#587): see the remarks.
    protected override ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(OpenDbConnection());
    }

    protected override DbConnection OpenDbConnection()
    {
        lock (_gate)
        {
            return base.OpenDbConnection();
        }
    }

    /// <summary>
    ///     A <see cref="SqliteConnection" /> whose <see cref="Open" /> takes its pool's lock.
    ///     <c>OpenAsync</c> is <see cref="DbConnection" />'s, which calls <see cref="Open" />.
    /// </summary>
    private sealed class GatedConnection(string connectionString, Lock gate)
        : SqliteConnection(connectionString)
    {
        public override void Open()
        {
            lock (gate)
            {
                base.Open();
            }
        }
    }
}

using Microsoft.Data.Sqlite;

namespace Fisher.Internal.Sessions;

/// <summary>
///     Clears a native SQLite transaction that <see cref="SqliteTransaction" /> has lost track of
///     (fisher#311).
/// </summary>
/// <remarks>
///     <para>
///         <b>The state this exists for is real and reproduces deterministically.</b> A connection on
///         which <c>BEGIN IMMEDIATE</c> was issued and never resolved, returned to
///         Microsoft.Data.Sqlite's pool, is handed to the next caller with the native transaction still
///         open — and their first <c>BeginTransactionAsync</c> fails with
///         <c>SQLite Error 1: 'cannot start a transaction within a transaction'</c>. That is the exact
///         exception fisher#311 caught once on a full-suite run, from inside
///         <c>FisherSession.WriteUnitOfWorkAsync</c>'s Polly delegate.
///     </para>
///     <para>
///         <b>Error 1 is not transient, which is what makes it worth closing rather than retrying.</b>
///         <c>FisherResilienceDefaults.IsTransient</c> declines <c>SQLITE_ERROR</c>, correctly — so it
///         escapes <c>SaveChangesAsync</c> raw, and a caller catching <c>DcbConcurrencyException</c> or
///         <c>StreamLockedException</c> and retrying gets an unhandled exception instead of the failure
///         they write retry loops against. fisher#306's translation is gated on transience and
///         deliberately does not reach this.
///     </para>
///     <para>
///         <b>Asking SQLite is the only reliable probe.</b> The condition is precisely that the
///         <see cref="SqliteTransaction" /> wrapper believes there is no transaction while SQLite says
///         there is, so the wrapper cannot be asked, and <c>sqlite3_get_autocommit()</c> has no SQL-level
///         equivalent. Issuing <c>ROLLBACK</c> and reading the refusal is the probe: it succeeds when
///         there was something to clear and fails with "cannot rollback - no transaction is active"
///         when there was not.
///     </para>
/// </remarks>
internal static class StrayTransaction
{
    /// <summary>
    ///     The message SQLite gives for a <c>ROLLBACK</c> with nothing to roll back — the ordinary
    ///     answer, and the one that means the connection was clean.
    /// </summary>
    private const string NoTransaction = "no transaction is active";

    /// <summary>
    ///     What <c>BEGIN</c> says when the connection already carries one.
    /// </summary>
    internal const string WithinATransaction = "cannot start a transaction within a transaction";

    /// <summary>
    ///     Roll back anything left open on <paramref name="connection" />, and say whether there was.
    /// </summary>
    /// <remarks>
    ///     Never throws. A connection being disposed is past the point where a diagnostic could be
    ///     acted on, and the caller that can act — the write path — reads the return value instead.
    /// </remarks>
    public static async ValueTask<bool> ClearAsync(SqliteConnection connection, CancellationToken token)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "rollback";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException e) when (e.Message.Contains(NoTransaction, StringComparison.OrdinalIgnoreCase))
        {
            // The ordinary case: nothing was open, which is what the probe was asking.
            return false;
        }
        catch (Exception)
        {
            // Anything else — a closed connection, a disposed one, a cancellation — leaves the
            // connection no worse than not having tried, and this is never the caller's real error.
            return false;
        }
    }

    /// <summary>
    ///     The synchronous form, for a session disposed through <see cref="IDisposable" />.
    /// </summary>
    public static void Clear(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "rollback";
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // See ClearAsync: never throws, and "nothing to roll back" is the ordinary answer.
        }
    }
}

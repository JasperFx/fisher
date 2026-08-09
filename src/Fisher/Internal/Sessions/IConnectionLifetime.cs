using Fisher.Storage;
using Microsoft.Data.Sqlite;

namespace Fisher.Internal.Sessions;

/// <summary>
///     Where a session's connection comes from, and whether it is running inside a transaction
///     somebody else opened (fisher#30).
/// </summary>
/// <remarks>
///     <para>
///         Deliberately narrower than Polecat's interface of the same name, which wraps every
///         execution — <c>ExecuteAsync</c>, <c>ExecuteScalarAsync</c>, two <c>ExecuteReaderAsync</c>
///         overloads. Fisher's session hands its connection out rather than executing through a
///         lifetime object (the append planner, the tag writer and the boundary check all take a
///         connection and a transaction), so wrapping execution would mean rewriting all of them to
///         gain nothing. What actually varies between an owned session and an enlisted one is the two
///         members below.
///     </para>
/// </remarks>
internal interface IConnectionLifetime : IDisposable, IAsyncDisposable
{
    /// <summary>
    ///     The session's connection, opened on first use.
    /// </summary>
    ValueTask<SqliteConnection> ConnectionAsync(CancellationToken token);

    /// <summary>
    ///     The caller's transaction every command must join, or null when the session opens its own
    ///     inside <c>SaveChangesAsync</c>.
    /// </summary>
    /// <remarks>
    ///     Non-null is what makes a session <em>enlisted</em>, and it is the single flag every
    ///     divergence in <c>FisherSession.SaveChangesAsync</c> reads — no commit, no retry, no
    ///     post-commit step, no on-demand table creation. See <see cref="SessionOptions.ForTransaction" />.
    /// </remarks>
    SqliteTransaction? EnlistedTransaction { get; }
}

/// <summary>
///     The ordinary lifetime: one connection from the store's data source, held for the session's
///     whole life and disposed with it.
/// </summary>
/// <remarks>
///     One connection rather than one per call, so a read inside a unit of work sees the writes that
///     unit of work has already committed, and so a session holds one slot in the pool rather than
///     churning them.
/// </remarks>
internal sealed class OwnedConnectionLifetime : IConnectionLifetime
{
    private readonly FisherDatabase _database;
    private SqliteConnection? _connection;

    public OwnedConnectionLifetime(FisherDatabase database) => _database = database;

    public SqliteTransaction? EnlistedTransaction => null;

    public async ValueTask<SqliteConnection> ConnectionAsync(CancellationToken token)
        => _connection ??= await _database.OpenConnectionAsync(token).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

/// <summary>
///     A connection — and possibly a transaction — the caller supplied. The session uses both and
///     disposes neither.
/// </summary>
/// <remarks>
///     <para>
///         Disposal is a no-op in both forms, which is the whole point: a session that closed the
///         connection it was handed would close it out from under the caller who is still going to
///         commit on it. The <c>PRAGMA</c> settings the store's data source applies are the caller's
///         responsibility here too — Fisher never opened this connection, so it never configured it.
///     </para>
/// </remarks>
internal sealed class ExternalConnectionLifetime : IConnectionLifetime
{
    private readonly SqliteConnection _connection;

    public ExternalConnectionLifetime(SqliteConnection connection, SqliteTransaction? transaction)
    {
        _connection = connection;
        EnlistedTransaction = transaction;
    }

    public SqliteTransaction? EnlistedTransaction { get; }

    public async ValueTask<SqliteConnection> ConnectionAsync(CancellationToken token)
    {
        // A caller may hand over a connection they have not opened yet; one carrying a transaction is
        // open by construction. Opening it is not the same as owning it — disposal stays theirs.
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(token).ConfigureAwait(false);
        }

        return _connection;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}

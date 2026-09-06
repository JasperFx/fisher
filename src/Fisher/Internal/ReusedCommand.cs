using Microsoft.Data.Sqlite;

namespace Fisher.Internal;

/// <summary>
///     The command currently prepared on a unit of work's connection, kept across consecutive
///     operations that compile to the same SQL (fisher#171).
/// </summary>
/// <remarks>
///     <para>
///         <b>What this removes is the <c>sqlite3_prepare_v2</c> per operation, and that is the whole
///         of it.</b> A hundred-document <c>SaveChangesAsync</c> queues a hundred upserts whose SQL text
///         is character-for-character identical — only the bound values differ — and the old executor
///         compiled, prepared, executed and disposed a separate <see cref="SqliteCommand" /> for each
///         one, inside the exclusive <c>BEGIN IMMEDIATE</c> transaction. Microsoft.Data.Sqlite keeps a
///         command's prepared statements alive for as long as its <see cref="SqliteCommand.CommandText" />
///         does not change, so holding the previous command and re-executing it with the next
///         operation's parameters turns N prepares into one per distinct statement.
///     </para>
///     <para>
///         <b>Only a <em>run</em> of identical statements is coalesced, never a reordering.</b>
///         Operations execute in exactly the order the unit of work queued them — a delete of an
///         event's tag rows still precedes the delete of the event itself, which is the ordering
///         fisher#6 established and the foreign key enforces. A mixed batch simply falls back to a
///         command per operation, having paid one string comparison per step.
///     </para>
///     <para>
///         <b>Each operation still executes on its own and reads its own reader, which is what keeps
///         fisher#66 closed.</b> Concatenating the batch into one multi-statement command and walking
///         it with <c>NextResultAsync</c> — Marten's shape — is the alternative that was measured and
///         rejected; see the remarks on <c>FisherSession.ExecuteBatchAsync</c> for the numbers and for
///         why the result-set walk cannot be made safe on this provider.
///     </para>
///     <para>
///         <b>The parameters are moved rather than copied, and that is a choice about what the code
///         has to argue rather than a bug fix.</b> The exact <see cref="SqliteParameter" /> instances
///         the command builder produced are what execute, so nothing here reasons about
///         Microsoft.Data.Sqlite's parameter-type inference at all — and there is something to reason
///         about: reading <see cref="SqliteParameter.SqliteType" /> on a parameter whose type was never
///         set <em>infers</em> it from the value currently held, so a value-copying executor is one
///         line away from carrying the previous operation's inferred type onto this operation's value.
///         <b>Copying values would in fact behave identically today</b>, and that was checked rather
///         than assumed — the provider binds by the CLR type of <c>Value</c> regardless of the declared
///         type, which is the same fisher#34 fact that makes Weasel's blanket TEXT stamping harmless,
///         and <c>reused_command_binding</c> passes against a deliberately planted value-copying
///         implementation. Moving is kept because it stays correct without depending on that.
///     </para>
///     <para>
///         Verified against Microsoft.Data.Sqlite 10.0.9 that replacing the collection does not
///         invalidate the prepared statement: clearing and re-adding on every execution measures
///         identical to rebinding values in place, and both are several times faster than a command per
///         operation.
///     </para>
/// </remarks>
internal sealed class ReusedCommand : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;
    private readonly int? _commandTimeout;

    private SqliteCommand? _standing;

    internal ReusedCommand(SqliteConnection connection, SqliteTransaction? transaction, int? commandTimeout = null)
    {
        _connection = connection;
        _transaction = transaction;
        _commandTimeout = commandTimeout;
    }

    /// <summary>
    ///     How many commands were actually prepared — one per run of identical statements, rather than
    ///     one per operation. Read by the tests that pin the coalescing; nothing in production branches
    ///     on it.
    /// </summary>
    internal int Prepared { get; private set; }

    /// <summary>
    ///     Take the command to execute for a freshly compiled statement, reusing the standing prepared
    ///     one when the SQL is unchanged.
    /// </summary>
    /// <remarks>
    ///     <paramref name="built" /> is consumed either way: its parameters are moved onto the standing
    ///     command and it is disposed, or it becomes the standing command itself. The caller must not
    ///     use it afterwards, and must not dispose the returned command — this type owns it.
    /// </remarks>
    internal SqliteCommand Take(SqliteCommand built)
    {
        if (_standing is not null &&
            string.Equals(_standing.CommandText, built.CommandText, StringComparison.Ordinal))
        {
            MoveParameters(built, _standing);
            built.Dispose();

            return _standing;
        }

        // A different statement: the standing command's prepared statements are of no further use, and
        // an undisposed SqliteCommand keeps its native ones alive until finalization.
        _standing?.Dispose();

        _standing = built;
        built.Connection = _connection;
        built.Transaction = _transaction;

        if (_commandTimeout.HasValue)
        {
            built.CommandTimeout = _commandTimeout.Value;
        }

        Prepared++;

        return built;
    }

    /// <summary>
    ///     Hand the freshly built command's parameters to the standing one, leaving no instance in two
    ///     collections.
    /// </summary>
    private static void MoveParameters(SqliteCommand from, SqliteCommand to)
    {
        var count = from.Parameters.Count;
        var moved = new SqliteParameter[count];

        for (var i = 0; i < count; i++)
        {
            moved[i] = from.Parameters[i];
        }

        from.Parameters.Clear();
        to.Parameters.Clear();

        foreach (var parameter in moved)
        {
            to.Parameters.Add(parameter);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_standing is not null)
        {
            await _standing.DisposeAsync().ConfigureAwait(false);
            _standing = null;
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore;

/// <summary>
///     An EF Core <see cref="DbContext" /> that saves inside Fisher's transaction, committed with it
///     (fisher#50).
/// </summary>
/// <remarks>
///     <para>
///         <b>This matters more on SQLite than the equivalent does on either sibling, and structurally
///         so.</b> One writer per database file. An application using Fisher for its events and EF
///         Core for its relational tables — in the same file, which is the natural thing to do with an
///         embedded database — cannot write both atomically without this, and cannot write both
///         <em>at all</em> without contending against itself: two transactions on one file means one
///         waits or fails with <c>SQLITE_BUSY</c>. On PostgreSQL the equivalent is a nicety.
///     </para>
///     <para>
///         <b>Verified against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before anything was
///         built on it</b>, the discipline fisher#38's foreign keys and fisher#2's generated columns
///         both followed: <c>Database.UseTransaction</c> enlists, <c>SaveChangesAsync</c> writes
///         inside the transaction, another connection sees nothing until the commit, and a rollback
///         takes EF's write with it. All four are what <see cref="ITransactionParticipant" /> needs and
///         none of them was safe to assume.
///     </para>
///     <para>
///         <b>The single most likely way to build this wrong is a <c>DbContext</c> on a second
///         connection to the same file.</b> That is two writers, and the second blocks on the first
///         from inside the first one's transaction — a genuine self-deadlock that presents as a hang
///         rather than an error. Which is why the safe constructor takes a <em>factory</em> over the
///         connection Fisher supplies, and why the one taking a built context checks the connection
///         and refuses by name.
///     </para>
/// </remarks>
public sealed class DbContextTransactionParticipant<TContext> : ITransactionParticipant, IAsyncDisposable
    where TContext : DbContext
{
    private readonly Func<SqliteConnection, TContext>? _factory;
    private readonly TContext? _context;

    /// <summary>
    ///     Build the context against the connection Fisher hands over, and dispose it afterwards.
    /// </summary>
    /// <remarks>
    ///     <b>The shape that cannot be wrong.</b> The connection is an argument rather than something
    ///     the caller has to have arranged, so the self-deadlock above is not expressible:
    ///     <c>new AppDbContext(new DbContextOptionsBuilder&lt;AppDbContext&gt;().UseSqlite(connection).Options)</c>.
    /// </remarks>
    public DbContextTransactionParticipant(Func<SqliteConnection, TContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>
    ///     Save an already-built context, which must be on Fisher's own connection.
    /// </summary>
    /// <remarks>
    ///     For an application whose <c>DbContext</c> comes from DI. Fisher does not dispose it — it did
    ///     not create it — and checks its connection before writing, because the alternative to that
    ///     check is a hang.
    /// </remarks>
    public DbContextTransactionParticipant(TContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    private DbContextTransactionParticipant(TContext context, bool movesOntoFishersConnection)
    {
        _context = context;
        _movesOntoFishersConnection = movesOntoFishersConnection;
    }

    private readonly bool _movesOntoFishersConnection;

    /// <summary>
    ///     A context that reads on its own connection and is moved onto Fisher's to write.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What EF-backed projection storage needs, and the one case where a context legitimately
    ///         starts on a different connection. A projection's storage is resolved before the batch has
    ///         opened the connection it will commit on, so the context cannot be built against it; and
    ///         the storage has to <em>read</em> — loading each slice's current aggregate — long before
    ///         there is a transaction to read in.
    ///     </para>
    ///     <para>
    ///         <b>Verified against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before this was
    ///         built on</b>, as fisher#38 and fisher#2 both did: a context that has already queried
    ///         through its own connection accepts <c>SetDbConnection</c> onto another and writes through
    ///         it, and a read on EF's own connection does not block against a
    ///         <c>BEGIN IMMEDIATE</c> held elsewhere on the file. Neither was safe to assume — the
    ///         second is the one that would have turned an EF-backed projection into a hang.
    ///     </para>
    ///     <para>
    ///         Reads therefore see <em>committed</em> state, which is what a projection wants: the
    ///         aggregate as of the last batch. Anything this batch has already changed is served from
    ///         EF's change tracker rather than from the database, so a slice never has to read its own
    ///         uncommitted write.
    ///     </para>
    /// </remarks>
    public static DbContextTransactionParticipant<TContext> MovingOntoFishersConnection(TContext context)
        => new(context ?? throw new ArgumentNullException(nameof(context)), movesOntoFishersConnection: true);

    /// <inheritdoc />
    public async Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var context = _context ?? _factory!(connection);

        try
        {
            if (_movesOntoFishersConnection)
            {
                // The whole point of the mode: it read on its own connection, and it writes on ours.
                // Moved here rather than at construction because ours did not exist until now.
                context.Database.SetDbConnection(connection);
            }
            else
            {
                AssertSameConnection(context, connection);
            }

            // EF's own transaction handling steps aside: from here its SaveChangesAsync writes into
            // Fisher's transaction and does not commit.
            await context.Database.UseTransactionAsync(transaction, token).ConfigureAwait(false);

            // acceptAllChangesOnSuccess: false is load-bearing, and only for a context this did not
            // create. EF's default accepts the changes the moment its own command succeeds — which is
            // *not* the moment Fisher commits. Verified: an entity goes Added -> Unchanged at
            // SaveChangesAsync and stays Unchanged through a rollback of the enclosing transaction. So
            // under the default, a retried SQLITE_BUSY re-invokes this against a context that believes
            // it has already saved, writes nothing, and lets Fisher commit without EF's rows — silent,
            // and only under contention. Leaving the changes pending is what makes the second attempt
            // write them; AfterCommitAsync is what stops them being pending forever.
            //
            // The factory form needs none of this: a retry runs the factory again, so the caller's
            // lambda builds a fresh context and re-adds its entities. That is the shape fisher#12's
            // rule asks for, reached without having to think about it.
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: _context is null, token)
                .ConfigureAwait(false);
        }
        finally
        {
            // Only what we created. A context from DI belongs to its scope.
            if (_context is null)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Dispose the context, but only in the mode where Fisher was given it to own for a batch.
    /// </summary>
    /// <remarks>
    ///     Called by <c>FisherProjectionBatch</c> when the batch ends, committed or not. The context of
    ///     a moving-mode participant is created per batch by whoever registered the projection and
    ///     cannot dispose itself — it has to outlive the apply that created it and survive a retried
    ///     commit. A context handed over through the plain constructor belongs to its caller's scope and
    ///     is left alone, which is the same rule <see cref="BeforeCommitAsync" /> already follows.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_movesOntoFishersConnection && _context is not null)
        {
            await _context.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task AfterCommitAsync(CancellationToken token)
    {
        // The other half of the retry rule above: the write is durable, so the entities EF is still
        // holding as pending are now what the database says. Only for a context this did not create —
        // the factory's is disposed and its changes were accepted on the way through.
        _context?.ChangeTracker.AcceptAllChanges();

        return Task.CompletedTask;
    }

    /// <remarks>
    ///     <b>By reference, not by connection string.</b> Two connections to one file have the same
    ///     string and are still two writers — comparing strings would pass the exact case this check
    ///     exists to catch.
    /// </remarks>
    private static void AssertSameConnection(TContext context, SqliteConnection connection)
    {
        var contextConnection = context.Database.GetDbConnection();

        if (ReferenceEquals(contextConnection, connection))
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{typeof(TContext).Name}' is configured against a different connection from the one this "
            + "Fisher session is writing on. On SQLite that is not a slow path — two connections to one "
            + "file are two writers, and EF's would block on Fisher's from inside Fisher's own "
            + "transaction, which hangs rather than failing. Configure the context with "
            + "UseSqlite(theSameConnection), or use the constructor that takes a factory over the "
            + "connection Fisher supplies.");
    }
}

/// <summary>
///     The shorthands for enlisting a <see cref="DbContext" /> in a Fisher unit of work.
/// </summary>
public static class FisherEntityFrameworkCoreExtensions
{
    /// <summary>
    ///     Save a <see cref="DbContext" /> built against Fisher's connection, inside Fisher's
    ///     transaction.
    /// </summary>
    /// <remarks>
    ///     The safe form — see
    ///     <see cref="DbContextTransactionParticipant{TContext}(Func{SqliteConnection,TContext})" />.
    /// </remarks>
    public static IDocumentSession UseDbContext<TContext>(this IDocumentSession session,
        Func<SqliteConnection, TContext> factory) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(session);

        session.AddTransactionParticipant(new DbContextTransactionParticipant<TContext>(factory));

        return session;
    }

    /// <inheritdoc cref="UseDbContext{TContext}(IDocumentSession,Func{SqliteConnection,TContext})" />
    /// <remarks>
    ///     For a context that already exists. Its connection is checked against Fisher's when the unit
    ///     of work commits, and a mismatch is refused by name.
    /// </remarks>
    public static IDocumentSession UseDbContext<TContext>(this IDocumentSession session, TContext context)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(session);

        session.AddTransactionParticipant(new DbContextTransactionParticipant<TContext>(context));

        return session;
    }
}

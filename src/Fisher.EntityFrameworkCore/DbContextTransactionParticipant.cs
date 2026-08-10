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
public sealed class DbContextTransactionParticipant<TContext> : ITransactionParticipant
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

    /// <inheritdoc />
    public async Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var context = _context ?? _factory!(connection);

        try
        {
            AssertSameConnection(context, connection);

            // EF's own transaction handling steps aside: from here its SaveChangesAsync writes into
            // Fisher's transaction and does not commit.
            await context.Database.UseTransactionAsync(transaction, token).ConfigureAwait(false);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
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

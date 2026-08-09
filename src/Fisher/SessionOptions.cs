using System.Data;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher;

/// <summary>
///     How one session differs from the store's defaults: its tenant, its timeout, and — the reason
///     this type earns its place on SQLite — the connection or transaction it runs in (fisher#30).
/// </summary>
/// <remarks>
///     <para>
///         <b>Enlistment is worth more here than the same feature is on Marten or Polecat.</b> SQLite
///         permits one writer per database file, and an application using Fisher keeps its own tables
///         in that same file. Without a way to hand Fisher an open transaction, writing your rows and
///         Fisher's atomically means taking the write lock twice and contending with yourself. On
///         PostgreSQL or SQL Server the equivalent is a convenience.
///         <see cref="IDocumentSession.QueueSqlCommand(string,object?[])" /> answers the same problem
///         from the other direction — your SQL inside Fisher's transaction, rather than Fisher's
///         writes inside yours.
///     </para>
///     <para>
///         There are exactly three modes, and each has one rule:
///     </para>
///     <list type="bullet">
///         <item>
///             Neither <see cref="Connection" /> nor <see cref="Transaction" /> — the ordinary session.
///             Fisher opens a connection from the store's data source, opens and commits its own
///             transaction, and disposes the connection with the session.
///         </item>
///         <item>
///             <see cref="Connection" /> only — Fisher runs on your connection and opens and commits
///             its own transaction on it. <b>It never disposes a connection it did not open.</b>
///         </item>
///         <item>
///             <see cref="Transaction" /> — Fisher runs inside your transaction and
///             <b>neither commits nor rolls back</b>. <c>SaveChangesAsync</c> writes and returns; the
///             commit is yours. See <see cref="ForTransaction" /> for what that costs.
///         </item>
///     </list>
///     <para>
///         <b>There is no <c>OwnsConnectionLifecycle</c> / <c>OwnsTransactionLifecycle</c> pair</b>, as
///         Marten has. A supplied connection is never disposed and a supplied transaction is never
///         committed — one rule each, rather than four combinations of which two are traps.
///     </para>
///     <para>
///         <b>Deliberately absent, because the behaviour it would name does not exist yet:</b> session
///         <c>Listeners</c> (<see href="https://github.com/JasperFx/fisher/issues/32">fisher#32</see>).
///         It is on Polecat's <c>SessionOptions</c>; carrying it here would be a knob that silently
///         does nothing, which is the one thing this codebase refuses to ship.
///     </para>
/// </remarks>
public class SessionOptions
{
    /// <summary>
    ///     The tenant every operation in the session is scoped to.
    /// </summary>
    public string TenantId { get; set; } = StorageConstants.DefaultTenantId;

    /// <summary>
    ///     What the session remembers about the documents it loads and stores (fisher#31).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Defaults to <see cref="DocumentTracking.None" />, where Marten's
    ///         <c>OpenSession(SessionOptions)</c> defaults to its identity map.</b> Marten's default is
    ///         historical — it predates <c>LightweightSession()</c> — and following it here would
    ///         silently change what every existing <c>OpenSession</c> caller gets: an identity map they
    ///         did not ask for, and with it Marten's rule that storing a <em>second instance</em> under
    ///         an id already in the map throws. A tracking mode is cheap to ask for and expensive to be
    ///         given, so it is asked for.
    ///     </para>
    ///     <para>
    ///         <see cref="DocumentStore.IdentitySession" /> and
    ///         <see cref="DocumentStore.DirtyTrackedSession" /> are the shorthands for the two modes
    ///         that are not the default.
    ///     </para>
    /// </remarks>
    public DocumentTracking Tracking { get; set; } = DocumentTracking.None;

    /// <summary>
    ///     The isolation level <c>SaveChangesAsync</c> opens its transaction at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Carried for parity, and only one value is refused</b> — the same reasoning
    ///         <c>IBatchedQuery</c> is carried under. Verified against Microsoft.Data.Sqlite 10.0.9:
    ///         <see cref="IsolationLevel.Unspecified" />, <see cref="IsolationLevel.ReadCommitted" />,
    ///         <see cref="IsolationLevel.RepeatableRead" /> and <see cref="IsolationLevel.Serializable" />
    ///         all produce the same <c>BEGIN IMMEDIATE</c> and all report
    ///         <see cref="IsolationLevel.Serializable" /> back off the transaction.
    ///         <see cref="IsolationLevel.Chaos" /> and <see cref="IsolationLevel.Snapshot" /> are
    ///         refused by the provider. So Polecat code that sets <c>ReadCommitted</c> ports across and
    ///         behaves identically.
    ///     </para>
    ///     <para>
    ///         <b><see cref="IsolationLevel.ReadUncommitted" /> is refused, and it is the one value
    ///         that would change anything.</b> It begins a <em>deferred</em> transaction, which does
    ///         not take the write lock — and Fisher's append path reads each stream's current version
    ///         and writes version+1 on the strength of holding it. Nothing would signal the loss:
    ///         the transaction still reports <c>Serializable</c>, so the optimistic concurrency guard
    ///         would simply stop being a guard.
    ///     </para>
    /// </remarks>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Serializable;

    /// <summary>
    ///     Command timeout in seconds for this session. Null uses
    ///     <see cref="StoreOptions.CommandTimeout" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This means something different on SQLite than it does on either sibling, in two
    ///         ways.</b> It bounds how long a statement waits for the write lock before failing with
    ///         <c>SQLITE_BUSY</c>; it does <em>not</em> interrupt a query that is genuinely slow, which
    ///         is what a command timeout means on PostgreSQL and SQL Server.
    ///     </para>
    ///     <para>
    ///         And it does not bound the wait at <c>BEGIN IMMEDIATE</c>. That transaction is begun on
    ///         the connection rather than through a command, so its busy wait comes from the connection
    ///         string's <c>Default Timeout</c> — 30 seconds unless the store's connection string says
    ///         otherwise. Both halves verified against Microsoft.Data.Sqlite 10.0.9.
    ///     </para>
    /// </remarks>
    public int? Timeout { get; set; }

    /// <summary>
    ///     Run the session on this connection instead of one from the store's data source.
    /// </summary>
    /// <remarks>
    ///     Opened by the session if it is not open already, and <b>never disposed by it</b> — the
    ///     session did not open it. Fisher still opens and commits its own transaction, unless
    ///     <see cref="Transaction" /> is also set.
    /// </remarks>
    public SqliteConnection? Connection { get; set; }

    /// <summary>
    ///     Run the session inside this already-open transaction.
    /// </summary>
    /// <remarks>
    ///     Its <see cref="SqliteTransaction.Connection" /> is the session's connection, so setting
    ///     <see cref="Connection" /> as well is only allowed when it is the same one.
    /// </remarks>
    public SqliteTransaction? Transaction { get; set; }

    /// <summary>
    ///     A session scoped to one tenant.
    /// </summary>
    public static SessionOptions ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return new SessionOptions { TenantId = tenantId };
    }

    /// <summary>
    ///     A session on a connection you opened, whose transaction Fisher still owns.
    /// </summary>
    public static SessionOptions ForConnection(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new SessionOptions { Connection = connection };
    }

    /// <summary>
    ///     A session inside a transaction you opened, and will commit.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SaveChangesAsync</c> writes everything the unit of work holds and returns without
    ///         committing. Three consequences, none of them obvious, all of them stated rather than
    ///         discovered:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>No retry.</b> An ordinary commit runs inside
    ///             <see cref="StoreOptions.ResiliencePipeline" />, which can re-execute the whole batch
    ///             after a <c>SQLITE_BUSY</c> because a failed attempt's own transaction rolled back
    ///             first. Here it did not — the transaction is yours and still open — so a retry would
    ///             write everything the first attempt already wrote a second time. A busy database
    ///             surfaces to you instead.
    ///         </item>
    ///         <item>
    ///             <b>No post-commit step.</b> An outbox's <c>AfterCommitAsync</c> and the event
    ///             store's append observer both mean "this is now visible to everyone else", and Fisher
    ///             is not told when your commit happens. They do not fire. <c>BeforeCommitAsync</c>
    ///             does, as the last thing the session writes.
    ///         </item>
    ///         <item>
    ///             <b>Document tables are not created on demand.</b> That path runs a migration on its
    ///             own connection, which would block against the write lock your transaction is
    ///             holding. A document type whose table does not exist yet throws by name instead of
    ///             waiting out the busy timeout — apply schema changes before enlisting.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         <b>Open it as <see cref="IsolationLevel.Serializable" />.</b> A deferred transaction —
    ///         ADO.NET's default in most providers, and what <c>ReadUncommitted</c> produces here —
    ///         does not hold the write lock, so Fisher's append path reads a stream version it is not
    ///         protected against. SQLite still refuses the second writer, so there is no lost update;
    ///         what changes is that you get <c>SQLITE_BUSY</c> at the first write rather than a clean
    ///         concurrency failure. Fisher cannot warn about it: the provider reports
    ///         <c>Serializable</c> for a deferred transaction and an immediate one alike, so the two
    ///         are indistinguishable from the outside. Verified against Microsoft.Data.Sqlite 10.0.9.
    ///     </para>
    /// </remarks>
    public static SessionOptions ForTransaction(SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return new SessionOptions { Transaction = transaction };
    }

    /// <summary>
    ///     Fail at session creation rather than at first use.
    /// </summary>
    internal void AssertValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantId, nameof(TenantId));

        if (IsolationLevel == IsolationLevel.ReadUncommitted)
        {
            throw new ArgumentOutOfRangeException(nameof(IsolationLevel),
                "SessionOptions.IsolationLevel cannot be ReadUncommitted on SQLite. It begins a deferred "
                + "transaction, which does not take the write lock — and Fisher's append path reads a "
                + "stream's current version and writes version+1 on the strength of holding it, so the "
                + "optimistic concurrency guard would stop guarding anything. Nothing would report it: the "
                + "transaction still describes itself as Serializable. Every other level SQLite accepts "
                + "produces the same BEGIN IMMEDIATE.");
        }

        if (Transaction is null)
        {
            return;
        }

        if (Transaction.Connection is null)
        {
            throw new ArgumentException(
                "SessionOptions.Transaction has no connection, so it has already been committed, rolled "
                + "back or disposed.", nameof(Transaction));
        }

        if (Connection is not null && !ReferenceEquals(Connection, Transaction.Connection))
        {
            throw new ArgumentException(
                "SessionOptions.Connection is not the connection SessionOptions.Transaction was opened on. "
                + "Set one or the other — a transaction already names its connection.", nameof(Connection));
        }
    }

    /// <summary>
    ///     The connection this session runs on, or null when it should open its own.
    /// </summary>
    internal SqliteConnection? ExternalConnection => Transaction?.Connection ?? Connection;
}

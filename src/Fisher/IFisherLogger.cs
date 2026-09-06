using System.Data.Common;

namespace Fisher;

/// <summary>
///     Records the SQL a Fisher store's sessions execute (fisher#207).
/// </summary>
/// <remarks>
///     <para>
///         Fisher's answers to <em>"what SQL did that actually run?"</em> were
///         <c>ToSql&lt;T&gt;(queryable)</c>, which is a design-time affordance for one statement, and
///         the <c>Fisher</c> <see cref="System.Diagnostics.ActivitySource" />, which gives spans and a
///         retry event but no SQL text. The daemon logged; a session did not. This is the seam that
///         closes that, and it is deliberately Marten's — <c>IMartenLogger</c> plus
///         <c>IMartenSessionLogger</c>, a store-level factory that hands out a per-session logger — so
///         that the concept ports and a logger written for one store is a rename away from the other.
///     </para>
///     <para>
///         <b>Attached with <see cref="StoreOptions.Logger(IFisherLogger)" />, read back with
///         <see cref="StoreOptions.Logger()" />.</b> <c>AddFisher</c> wires a
///         <see cref="DefaultFisherLogger" /> over the container's <c>ILogger</c> unless the store
///         already carries one, exactly as <c>AddMarten</c> does; a store built by hand keeps
///         <see cref="NulloFisherLogger" /> and logs nothing.
///     </para>
///     <para>
///         <b>One member, where Marten's has two.</b> <c>IMartenLogger.SchemaChange(string sql)</c> is
///         not ported — see <see cref="IFisherSessionLogger" /> for the full list of Marten members
///         Fisher declines and why.
///     </para>
/// </remarks>
public interface IFisherLogger
{
    /// <summary>
    ///     Called once as a session is created, to give it the logger it will record through.
    /// </summary>
    /// <remarks>
    ///     Returning <c>this</c> is the ordinary implementation and is what both shipped loggers do; a
    ///     logger that wants per-session state returns a new instance instead, at the cost of an
    ///     allocation per session.
    /// </remarks>
    IFisherSessionLogger StartSession(IQuerySession session);
}

/// <summary>
///     Custom logging for one Fisher session's SQL (fisher#207).
/// </summary>
/// <remarks>
///     <para>
///         Set per session through <see cref="IQuerySession.Logger" />, or store-wide through
///         <see cref="IFisherLogger" />. Mirrors Marten's <c>IMartenSessionLogger</c> closely enough
///         that a logger ports across, with the divergences below.
///     </para>
///     <para>
///         <b>Marten members Fisher deliberately does not carry, and why.</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b><c>LogSuccess(NpgsqlBatch)</c>, <c>LogFailure(NpgsqlBatch, Exception)</c> and
///                 <c>OnBeforeExecute(NpgsqlBatch)</c> — there is no batch to log.</b>
///                 Microsoft.Data.Sqlite does ship a <c>SqliteBatch</c>, so this is a decision rather
///                 than a missing provider feature: Fisher executes one command per storage operation
///                 on purpose, and CLAUDE.md carries the measurement that settled it (1000 single-row
///                 upserts in one transaction take 4–6 ms as separate commands and 82–192 ms
///                 concatenated, because the cost is parameter rebinding per prepared statement). A
///                 batch overload here would be a member that can never fire, and a logger author
///                 would have to implement it to find that out.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b><c>IMartenLogger.SchemaChange(string sql)</c> — Fisher already has that seam,
///                 one layer down.</b> All of Fisher's DDL goes through Weasel, which has its own
///                 <c>Weasel.Core.Migrations.IMigrationLogger</c>, and <c>FisherDatabase</c> derives
///                 from <c>DatabaseBase&lt;T&gt;</c> and therefore already implements
///                 <c>IDatabaseWithMigrationLogger</c> — so an application can route a Fisher store's
///                 migration DDL wherever it likes today, without this interface. Adding a second
///                 switch for one stream of output would be bad enough; honouring it would be worse,
///                 because every Weasel provider's <c>executeDelta</c> branches on
///                 <c>logger is DefaultMigrationLogger</c> to decide whether a failed DDL statement
///                 rethrows with its original stack trace or is handed to <c>OnFailure</c> and then
///                 swallowed when <c>failureIsFatal</c> is false. Displacing that type to gain a log
///                 line would change migration failure semantics.
///             </description>
///         </item>
///     </list>
///     <para>
///         <b>And one member Fisher adds: <see cref="Enabled" />.</b> See its own remarks — it is what
///         keeps an unattached or uninterested logger from costing anything on the hot path.
///     </para>
///     <para>
///         <b>Commands arrive as <see cref="DbCommand" />, not as the provider type.</b> Marten's
///         members take <c>NpgsqlCommand</c>. Fisher's storage seam
///         (<c>IStorageSession.ExecuteReaderAsync</c>) is already typed <see cref="DbCommand" />, so
///         narrowing to <c>SqliteCommand</c> would add a cast on the hot path to buy an implementer
///         nothing — every member a logger reads (<c>CommandText</c>, <c>Parameters</c>) is on the
///         base type.
///     </para>
/// </remarks>
public interface IFisherSessionLogger
{
    /// <summary>
    ///     Whether this logger would record anything at all right now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Fisher's own member, and the reason it exists is fisher#165.</b> That was a
    ///         <c>DaemonTrace.Record</c> call site whose interpolated-string argument was built
    ///         <em>before</em> the disabled gate could reject it, so a facility that promised to cost
    ///         nothing when off cost an allocation per call anyway. The same shape is available here
    ///         and is sharper: <see cref="RecordSavedChanges" /> wants an
    ///         <see cref="Services.IChangeSet" />, and <c>SaveChangesAsync</c> only builds one when a
    ///         listener is registered — so an unguarded call would put a per-commit allocation on
    ///         every store in the world to serve the ones that log.
    ///     </para>
    ///     <para>
    ///         Every call site in Fisher tests this <em>before</em> constructing any argument that is
    ///         not already in hand. Answer it as cheaply as possible: <see cref="DefaultFisherLogger" />
    ///         forwards to <c>ILogger.IsEnabled(LogLevel.Debug)</c>, and
    ///         <see cref="NulloFisherLogger" /> returns a constant false.
    ///     </para>
    ///     <para>
    ///         Defaulted to <c>true</c> so an existing implementation compiles unchanged and a logger
    ///         that always wants everything need not think about it.
    ///     </para>
    /// </remarks>
    bool Enabled => true;

    /// <summary>
    ///     Called immediately before a command executes. Use it to start a timer.
    /// </summary>
    void OnBeforeExecute(DbCommand command);

    /// <summary>
    ///     A command that executed without throwing.
    /// </summary>
    void LogSuccess(DbCommand command);

    /// <summary>
    ///     A command that threw.
    /// </summary>
    void LogFailure(DbCommand command, Exception ex);

    /// <summary>
    ///     A failure with no single command behind it — a commit that could not be completed.
    /// </summary>
    void LogFailure(Exception ex, string message);

    /// <summary>
    ///     Called after <c>SaveChangesAsync</c> has committed, with what the unit of work wrote.
    /// </summary>
    /// <remarks>
    ///     Fires for an enlisted session too, unlike
    ///     <see cref="IDocumentSessionListener.AfterCommitAsync(IDocumentSession,Services.IChangeSet,CancellationToken)" />.
    ///     A listener is a side effect that must not fire until the data is visible, and only the
    ///     caller's own commit makes that true; a log line is a record that the statements ran, which
    ///     they did.
    /// </remarks>
    void RecordSavedChanges(IDocumentSession session, Services.IChangeSet commit);
}

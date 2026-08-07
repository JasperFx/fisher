namespace Fisher.Storage;

/// <summary>
///     The <c>revision</c> column and the SQL that reads, writes and guards it — the numeric
///     alternative to <c>guid_version</c> (fisher#18).
/// </summary>
/// <remarks>
///     <para>
///         One place owns the column name and every fragment mentioning it, for the same reason
///         <see cref="SoftDelete" /> does: the table definition, the descriptor's four statements and
///         the guard would otherwise each spell it out, and they have to agree exactly.
///     </para>
///     <para>
///         <b>The semantics are Marten's, not Polecat's, and that follows from the upsert.</b> Fisher's
///         document upsert is <c>insert … on conflict … do update … returning</c> — Marten's shape —
///         and the shared numeric operations in Weasel.Storage document their trailing slots against
///         exactly that shape, guard included. So an explicit revision must be <em>greater</em> than
///         the stored one, where Polecat's bespoke pipeline made it an equality expectation. Following
///         Polecat here would mean writing SQL its own operations do not describe.
///     </para>
///     <para>
///         Revision <c>0</c> means "auto": increment whatever is stored. That is the sentinel the
///         shared operations bind when the caller did not name a revision, which is why every guard
///         starts with <c>? = 0 or</c>.
///     </para>
/// </remarks>
internal static class NumericRevision
{
    /// <summary>The column holding a document's current revision.</summary>
    internal const string Column = "revision";

    /// <summary>
    ///     The value expression for the <c>insert</c> branch: an explicit revision is honoured, and
    ///     auto starts at 1.
    /// </summary>
    /// <remarks>
    ///     Two parameter marks, which is the slot count the shared insert, upsert and overwrite
    ///     operations all bind for this binder. Both receive the same value.
    /// </remarks>
    internal const string InsertValueSql = "case when ? = 0 then 1 else ? end";

    /// <summary>
    ///     The assignment for an <c>update</c> or a <c>do update</c> branch: auto increments the stored
    ///     revision, explicit takes the caller's.
    /// </summary>
    /// <remarks>
    ///     Unqualified <c>revision</c> on the right is the pre-update row, which is what makes this an
    ///     increment — the same property the flat-table upsert relies on. <c>excluded.revision</c> would
    ///     be the value the insert branch computed, which is not what an increment means.
    /// </remarks>
    internal static string UpdateAssignmentSql(string quotedTableName)
        => $"{Column} = case when ? = 0 then {quotedTableName}.{Column} + 1 else ? end";

    /// <summary>
    ///     The guard: auto always wins, and an explicit revision must exceed the stored one.
    /// </summary>
    /// <remarks>
    ///     When it does not match, the update touches nothing and the statement returns no row — which
    ///     is exactly what the shared operations' postprocessing reads as a
    ///     <c>ConcurrencyException</c>. Same mechanism as the Guid guard.
    /// </remarks>
    internal static string GuardSql(string quotedTableName)
        => $"(? = 0 or {quotedTableName}.{Column} < ?)";
}

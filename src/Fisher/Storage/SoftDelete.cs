namespace Fisher.Storage;

/// <summary>
///     The two columns a soft-deleted document table carries, and the SQL that reads and writes them.
/// </summary>
/// <remarks>
///     <para>
///         One place, because the same column names are reached from three directions that would
///         otherwise each spell them out: the table definition, the storage's load and delete SQL, and
///         the LINQ layer's implicit filter.
///     </para>
///     <para>
///         Column names match Polecat's rather than Marten's <c>mt_deleted</c> / <c>mt_deleted_at</c>.
///         The <c>mt_</c> prefix is Marten's own table-ownership marker and means nothing here; Fisher
///         marks the tables it owns the shape of with <c>fi_</c> instead — see
///         <see cref="FisherTableNaming" />.
///     </para>
///     <para>
///         <c>is_deleted</c> is INTEGER 0/1 and <c>deleted_at</c> is ISO-8601 TEXT, following the same
///         two divergences the event tables do. That the timestamp is
///         <see cref="SqliteTimestamp.Format" /> — fixed width, UTC, milliseconds — is what lets
///         <c>DeletedSince</c> and <c>DeletedBefore</c> compare it as text.
///     </para>
/// </remarks>
internal static class SoftDelete
{
    public const string IsDeletedColumn = "is_deleted";
    public const string DeletedAtColumn = "deleted_at";

    /// <summary>The implicit filter every read of a soft-deleted type carries.</summary>
    public const string NotDeletedSql = IsDeletedColumn + " = 0";

    /// <summary>The inverse, for <c>IsDeleted()</c> and as the guard on an undelete.</summary>
    public const string DeletedSql = IsDeletedColumn + " = 1";

    /// <summary>
    ///     The <c>update … set</c> head that a soft delete is, in place of a <c>delete from</c>.
    /// </summary>
    public static string MarkDeletedSql(string quotedTableName)
        => $"update {quotedTableName} set {IsDeletedColumn} = 1, "
           + $"{DeletedAtColumn} = {SqliteTimestamp.NowExpression}";

    /// <summary>
    ///     The reverse. <c>deleted_at</c> is cleared rather than left behind, so a row that comes back
    ///     cannot be found again by <c>DeletedSince</c>.
    /// </summary>
    public static string ClearDeletedSql(string quotedTableName)
        => $"update {quotedTableName} set {IsDeletedColumn} = 0, {DeletedAtColumn} = null";
}

using Microsoft.Data.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Classifies the SQLite errors that mean "the storage this statement names does not exist".
/// </summary>
/// <remarks>
///     <para>
///         SQLite reports a missing table and a missing column as the generic <c>SQLITE_ERROR</c> (1),
///         distinguished only by message text — there is no dedicated error code to match on the way
///         SQL Server's 208/207 or PostgreSQL's 42P01/42703 allow. Both halves are needed: a missing
///         table is a schema that was never applied, and a missing column is a table that exists with
///         an older column set, which is the case marten#5509 actually met in production.
///     </para>
///     <para>
///         Deliberately free of Fisher types, since "is this SQLite error a missing table" is a fact about
///         SQLite rather than about Fisher's storage — it is written to move into Weasel.Sqlite as a file.
///     </para>
/// </remarks>
internal static class SqliteSchemaErrors
{
    private const int SqliteError = 1;

    public static bool IsUndefinedTable(Exception exception)
        => exception is SqliteException { SqliteErrorCode: SqliteError } sqlite &&
           sqlite.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    public static bool IsUndefinedColumn(Exception exception)
        => exception is SqliteException { SqliteErrorCode: SqliteError } sqlite &&
           sqlite.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

    public static bool IsMissingStorage(Exception exception)
        => IsUndefinedTable(exception) || IsUndefinedColumn(exception);
}

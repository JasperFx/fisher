using Weasel.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Resolves Fisher's physical table names.
/// </summary>
/// <remarks>
///     <para>
///         SQLite has no schemas. <c>SqliteObjectName</c> treats any schema other than <c>main</c> as
///         an <c>ATTACH</c>ed database qualifier, and Weasel's <c>EnsureSchemaExists</c> is an explicit
///         no-op — so the Marten/Polecat model of one physical schema per store has no direct
///         equivalent here.
///     </para>
///     <para>
///         Fisher therefore folds <see cref="StoreOptions.DatabaseSchemaName" /> into the table name
///         prefix instead of qualifying it. The default schema <c>main</c> yields the bare family
///         prefix (<c>fi_events</c>); any other value prepends itself (<c>compliance_fi_events</c>),
///         which is what gives multiple logical stores — and isolated test suites — separation inside
///         a single database file without paying for ATTACH lifecycle management on every pooled
///         connection.
///     </para>
///     <para>
///         Every <c>DbObjectName</c> Fisher builds consequently uses schema <c>main</c>, so nothing
///         downstream ever emits a qualified name.
///     </para>
/// </remarks>
internal static class FisherTableNaming
{
    /// <summary>
    ///     The schema name that means "no prefix folding" — SQLite's own default database.
    /// </summary>
    public const string DefaultSchemaName = "main";

    /// <summary>
    ///     The table name prefix shared by every Fisher-managed table, following the Critter Stack
    ///     convention (Marten <c>mt_</c>, Polecat <c>pc_</c>).
    /// </summary>
    public const string FamilyPrefix = "fi_";

    /// <summary>
    ///     The full table name prefix for a logical schema name.
    /// </summary>
    public static string PrefixFor(string schemaName)
        => IsDefaultSchema(schemaName) ? FamilyPrefix : $"{schemaName}_{FamilyPrefix}";

    /// <summary>
    ///     The physical table name for a Fisher table whose family name is <paramref name="suffix" />
    ///     (for example <c>events</c> becomes <c>fi_events</c> or <c>compliance_fi_events</c>).
    /// </summary>
    public static string TableName(string schemaName, string suffix) => PrefixFor(schemaName) + suffix;

    /// <summary>
    ///     The physical table name for a table the <em>user</em> named — today only a flat-table
    ///     projection's target.
    /// </summary>
    /// <remarks>
    ///     Same schema folding as <see cref="TableName" /> but without the <see cref="FamilyPrefix" />:
    ///     the family prefix marks a table Fisher owns the shape of, and a flat table's shape is
    ///     declared by the projection that writes it. So <c>compliance_flat_values</c> in logical
    ///     schema <c>reporting</c> is <c>reporting_compliance_flat_values</c>, and in the default
    ///     schema it is left exactly as the caller wrote it.
    /// </remarks>
    public static string UserTableName(string? schemaName, string tableName)
        => IsDefaultSchema(schemaName) ? tableName : $"{schemaName}_{tableName}";

    /// <summary>
    ///     The Weasel object name for a table the user named. Schema <c>main</c>, for the same reason
    ///     <see cref="ObjectFor" /> uses it.
    /// </summary>
    public static SqliteObjectName UserObjectFor(string? schemaName, string tableName)
        => new(DefaultSchemaName, UserTableName(schemaName, tableName));

    /// <summary>
    ///     The Weasel object name for a Fisher table. Always in schema <c>main</c> — the logical
    ///     schema has already been folded into the name — so it never renders as qualified SQL.
    /// </summary>
    public static SqliteObjectName ObjectFor(string schemaName, string suffix)
        => new(DefaultSchemaName, TableName(schemaName, suffix));

    /// <summary>
    ///     The quoted, ready-to-embed identifier for a Fisher table.
    /// </summary>
    public static string QuotedTableName(string schemaName, string suffix)
        => SchemaUtils.QuoteName(TableName(schemaName, suffix));

    private static bool IsDefaultSchema(string? schemaName)
        => string.IsNullOrWhiteSpace(schemaName) ||
           schemaName.Equals(DefaultSchemaName, StringComparison.OrdinalIgnoreCase);
}

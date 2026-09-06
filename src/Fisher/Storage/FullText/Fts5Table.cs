using System.Data.Common;
using Weasel.Core;
using Weasel.Sqlite;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Fisher.Storage.FullText;

/// <summary>
///     An FTS5 virtual table, as a Weasel schema object.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not a <c>Weasel.Sqlite.Tables.Table</c>, and it could not be.</b> A table's delta is
///         computed from <c>pragma_table_xinfo</c> and expressed as <c>ALTER TABLE</c>; a virtual
///         table has neither a column list worth reconciling that way nor any <c>ALTER</c> that
///         applies. So the delta here is a whitespace-insensitive comparison of the whole
///         <c>CREATE</c> statement against <c>sqlite_master.sql</c>, which is exactly how
///         <c>Weasel.Sqlite</c>'s own <c>View</c> and <c>Trigger</c> handle the same problem — SQLite
///         stores the submitted text verbatim, so the statement <em>is</em> the model.
///     </para>
///     <para>
///         <b>The create statement ends with <c>'rebuild'</c>, and that is the whole answer to
///         "an index created over existing rows starts empty".</b> The triggers that keep the index in
///         step only fire on writes made after they exist, so declaring an index on a store that
///         already holds documents would otherwise produce an index that matches nothing and reports
///         no error — the exact silent shape the house rule refuses. External content over a view is
///         what makes <c>rebuild</c> available at all: FTS5 can only repopulate itself from a content
///         source whose column names match its own, and the view is what provides those.
///     </para>
///     <para>
///         So this participates in migration like every other Fisher schema object:
///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c>, <c>db-apply</c>, <c>db-assert</c>,
///         <c>db-patch</c> and <c>db-dump</c> all see it, and <c>AutoCreate.None</c> refuses to create
///         it for free rather than by a check written here.
///     </para>
/// </remarks>
internal sealed class Fts5Table : ISchemaObject
{
    private readonly string[] _columns;
    private readonly string _contentTable;
    private readonly FullTextTokenizer _tokenizer;

    public Fts5Table(SqliteObjectName identifier, string[] columns, string contentTable,
        FullTextTokenizer tokenizer)
    {
        Identifier = identifier;
        _columns = columns;
        _contentTable = contentTable;
        _tokenizer = tokenizer;
    }

    public DbObjectName Identifier { get; }

    /// <summary>
    ///     The single <c>CREATE VIRTUAL TABLE</c> this renders to — public for the same reason a
    ///     view's create SQL is: it is what the delta compares and what a diagnostic wants to show.
    /// </summary>
    public string CreateStatement()
    {
        var columns = string.Join(", ", _columns.Select(SchemaUtils.QuoteName));

        return $"CREATE VIRTUAL TABLE {SchemaUtils.QuoteName(Identifier.Name)} USING fts5("
               + $"{columns}, content={Literal(_contentTable)}, content_rowid='rowid', "
               + $"tokenize={Literal(_tokenizer.ToSql())});";
    }

    public void WriteCreateStatement(Migrator migrator, TextWriter writer)
    {
        WriteDropStatement(migrator, writer);
        writer.WriteLine(CreateStatement());

        // Populate from the content view. A no-op on a store with no documents yet, and the whole
        // migration story for one that already has them.
        writer.WriteLine(
            $"INSERT INTO {SchemaUtils.QuoteName(Identifier.Name)}({SchemaUtils.QuoteName(Identifier.Name)}) "
            + "VALUES('rebuild');");
    }

    public void WriteDropStatement(Migrator rules, TextWriter writer)
        => writer.WriteLine($"DROP TABLE IF EXISTS {SchemaUtils.QuoteName(Identifier.Name)};");

    public void ConfigureQueryCommand(DbCommandBuilder builder)
    {
        var name = builder.AddParameter(Identifier.Name).ParameterName;

        builder.Append(
            $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @{name};");
    }

    public async Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader,
        CancellationToken ct = default)
    {
        // The reader is advanced between objects by Weasel's own delta driver, so this reads its own
        // result set and stops — calling NextResultAsync here would skip the next object's.
        string? existing = null;

        if (await reader.ReadAsync(ct).ConfigureAwait(false)
            && !await reader.IsDBNullAsync(0, ct).ConfigureAwait(false))
        {
            existing = await reader.GetFieldValueAsync<string>(0, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return new SchemaObjectDelta(this, SchemaPatchDifference.Create);
        }

        // Compared against the CREATE alone rather than against everything WriteCreateStatement
        // emits: the 'rebuild' insert is a data operation and sqlite_master never held it.
        return Normalize(existing) == Normalize(CreateStatement())
            ? new SchemaObjectDelta(this, SchemaPatchDifference.None)
            : new SchemaObjectDelta(this, SchemaPatchDifference.Update);
    }

    public IEnumerable<DbObjectName> AllNames()
    {
        yield return Identifier;
    }

    /// <summary>
    ///     Whitespace-insensitive, trailing-semicolon-insensitive comparison — the same normalisation
    ///     Weasel's SQLite trigger and view deltas use, and for the same reason: SQLite echoes back
    ///     the text it was given, so two statements that differ only in formatting describe the same
    ///     object and must not read as a pending migration on every startup.
    /// </summary>
    private static string Normalize(string sql)
        => string.Join(' ', sql.Replace(";", " ").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    ///     A single-quoted SQL string literal. Every value reaching here is derived from a table name
    ///     Fisher composed or from <see cref="FullTextTokenizer" />, so the doubling is belt and
    ///     braces rather than the only defence.
    /// </summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}

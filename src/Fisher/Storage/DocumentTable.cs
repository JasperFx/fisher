using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Storage;

/// <summary>
///     Weasel table definition for one document type — <c>fi_doc_&lt;alias&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         The column set is intentionally small. Marten and Polecat carry more here (duplicated
///         fields, hierarchy discriminators, partition keys); each of those is additive and can arrive
///         without changing the columns below, as soft delete's two did.
///     </para>
///     <para>
///         <c>tenant_id</c> exists only on conjoined mappings, matching Polecat. A single-tenant table
///         has nothing to filter by, and an always-present column would put a redundant value on every
///         row and a redundant predicate in every query.
///     </para>
/// </remarks>
internal class DocumentTable : Table
{
    public DocumentTable(DocumentMapping mapping) : base(mapping.TableName)
    {
        // The identity column carries the primary key on its own for a single-tenant table. Under
        // conjoined tenancy the same id may exist once per tenant, so the key is the pair — which is
        // also why the tenant column is added before any other, keeping the composite key's leading
        // column the one every query filters on.
        if (mapping.IsConjoined)
        {
            AddColumn(StorageConstants.TenantIdColumn, "TEXT")
                .NotNull()
                .DefaultValueByString(StorageConstants.DefaultTenantId)
                .AsPrimaryKey();
        }

        AddColumn("id", mapping.IdColumnType).NotNull().AsPrimaryKey();

        // The document body. SQLite's json1 functions read TEXT directly, so there is no jsonb
        // equivalent to reach for.
        AddColumn("data", "TEXT").NotNull();

        // Optimistic concurrency rides its own column rather than the document body, so a version
        // check never has to parse JSON.
        if (mapping.UseOptimisticConcurrency)
        {
            AddColumn("guid_version", "TEXT").NotNull();
        }

        // The numeric alternative, and mutually exclusive with the Guid one — DocumentMapping refuses
        // the pair, so at most one of these two columns is ever created. INTEGER because the revision
        // is compared and incremented as a number; a TEXT affinity here would make revision 10 sort
        // below revision 9 and turn the "must be greater" guard into nonsense.
        if (mapping.UseNumericRevisions)
        {
            AddColumn(NumericRevision.Column, "INTEGER").NotNull().DefaultValue(1);
        }

        // The concrete .NET type the row was written as. Written on every save; not selected on the
        // read path today, but it is what a future hierarchy discriminator would build on.
        AddColumn("dotnet_type", "TEXT").AllowNulls();

        // ISO-8601 UTC, same representation and the same parenthesized-expression trap as the event
        // tables: a non-literal DEFAULT must be wrapped in parentheses or CREATE TABLE will not parse.
        AddColumn("last_modified", "TEXT")
            .NotNull()
            .DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        // Soft delete adds two columns and nothing else. is_deleted is INTEGER 0/1 rather than a
        // boolean and carries a DEFAULT so a row written by anything that predates the flag still
        // reads as live; deleted_at is nullable because a live row has no deletion time. Only the
        // soft-delete operation writes a concrete timestamp there — every ordinary write clears it.
        if (mapping.IsSoftDeleted)
        {
            AddColumn(SoftDelete.IsDeletedColumn, "INTEGER").NotNull().DefaultValue(0);
            AddColumn(SoftDelete.DeletedAtColumn, "TEXT").AllowNulls();
        }

        AddDuplicatedFields(mapping);
        AddDeclaredIndexes(mapping);
    }

    /// <summary>
    ///     One SQLite expression index per user-declared index.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The indexed expression is the member's <c>TypedLocator</c>, from the same
    ///         <see cref="Linq.Members.MemberFactory" /> a query goes through — see
    ///         <see cref="DocumentIndex" /> for why building it here instead is the classic way to get an
    ///         index that is created, never used, and never wrong enough to notice.
    ///     </para>
    ///     <para>
    ///         A member that is also duplicated resolves to a <c>DuplicatedMember</c> whose
    ///         <c>TypedLocator</c> is the generated column's name, so the index lands on the column. That
    ///         is correct rather than special-cased: the locator is what the query emits either way.
    ///     </para>
    /// </remarks>
    private void AddDeclaredIndexes(DocumentMapping mapping)
    {
        if (mapping.Indexes.Count == 0)
        {
            return;
        }

        var members = new Linq.Members.MemberFactory(mapping.StoreOptions, mapping);

        foreach (var declared in mapping.Indexes)
        {
            var expressions = declared.MemberChains
                .Select(chain => members.ResolveMember(chain).TypedLocator);

            // Several expressions render as one comma-separated list, which Weasel wraps in the
            // parentheses a composite index needs.
            Indexes.Add(new Weasel.Sqlite.Tables.IndexDefinition(
                declared.Name ?? DefaultIndexName(declared.DefaultNameSuffix()))
            {
                Expression = string.Join(", ", expressions),
                IsUnique = declared.IsUnique
            });
        }
    }

    /// <summary>
    ///     <c>idx_&lt;table&gt;_&lt;members&gt;</c>, which is the shape Weasel gives the index it creates
    ///     for a duplicated column.
    /// </summary>
    /// <remarks>
    ///     Weasel.Sqlite's <c>DbObjectName.ToIndexName</c> is <see langword="internal" />, so the formula
    ///     is repeated rather than called. It is repeated on purpose: a user-declared index and a
    ///     duplicated-field index should be indistinguishable in <c>sqlite_master</c>, because which
    ///     mechanism created one is Fisher's business and not the reader's.
    /// </remarks>
    private string DefaultIndexName(string suffix) => $"idx_{Identifier.Name}_{suffix}";

    /// <summary>
    ///     A <c>VIRTUAL</c> generated column per duplicated field, and an index over it unless the
    ///     registration declined one.
    /// </summary>
    /// <remarks>
    ///     The expression is the member's own <c>TypedLocator</c>, taken from the same
    ///     <see cref="Linq.Members.MemberFactory" /> a query goes through — so the column holds exactly
    ///     what a predicate against that member looks for, including a timestamp's <c>strftime</c>
    ///     normalisation. Building it here instead would be the classic way to have an index that is
    ///     never used and never wrong enough to notice.
    /// </remarks>
    private void AddDuplicatedFields(DocumentMapping mapping)
    {
        if (mapping.DuplicatedFields.Count == 0)
        {
            return;
        }

        var members = new Linq.Members.MemberFactory(mapping.StoreOptions, mapping);

        foreach (var field in mapping.DuplicatedFields)
        {
            var member = members.ResolveMember(field.Members);

            // The resolved member is the duplicated one — its TypedLocator is the column being
            // declared. The expression has to be what that column replaces, so read it off the inner
            // JSON locator instead.
            var expression = member is Linq.Members.DuplicatedMember duplicated
                ? duplicated.GeneratedExpression
                : member.TypedLocator;

            var column = AddColumn(field.ColumnName, field.ColumnType ??
                                                     DuplicatedField.SqliteTypeFor(member.MemberType))
                .AllowNulls()
                .GeneratedAs(expression);

            if (field.ShouldIndex)
            {
                column.AddIndex();
            }
        }
    }

    /// <summary>
    ///     The same metadata query Weasel's <see cref="Table" /> issues, over
    ///     <c>pragma_table_xinfo</c> rather than <c>pragma_table_info</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>pragma_table_info</c> does not list generated columns.</b> Verified against SQLite
    ///         3.51: a virtual generated column is reported only by <c>table_xinfo</c>, with
    ///         <c>hidden = 2</c>. Weasel's delta detection would therefore see every duplicated column
    ///         as missing and emit <c>ALTER TABLE … ADD COLUMN</c> for it on every migration — and
    ///         Fisher runs a migration on the first write of each document type per process, so the
    ///         second one would fail with <c>duplicate column name</c>. This is not a rare-path
    ///         problem; it is the normal path.
    ///     </para>
    ///     <para>
    ///         Overriding the query alone is enough because <c>table_xinfo</c>'s first six columns are
    ///         <c>table_info</c>'s, in the same order, and Weasel's reader takes them by position. The
    ///         generated column comes back as an ordinary one with no expression, which
    ///         <c>TableColumn.Equals</c> — name and raw type only — matches against the expected column,
    ///         so the delta is clean.
    ///     </para>
    ///     <para>
    ///         Reported upstream as
    ///         <see href="https://github.com/JasperFx/weasel/issues/426">weasel#426</see>; this override
    ///         goes away when that ships.
    ///     </para>
    /// </remarks>
    public override void ConfigureQueryCommand(Weasel.Core.DbCommandBuilder builder)
    {
        var name = Identifier.Name.Replace("'", "''");

        builder.Append($@"
-- Get table SQL definition
SELECT sql FROM sqlite_master
WHERE type = 'table' AND name = '{name}';

-- Get column information, including generated columns, which pragma_table_info omits
SELECT cid, name, type, ""notnull"", dflt_value, pk FROM pragma_table_xinfo('{name}');

-- Get index information
SELECT name, sql FROM sqlite_master
WHERE type = 'index' AND tbl_name = '{name}' AND sql IS NOT NULL;

-- Get foreign key information
SELECT * FROM pragma_foreign_key_list('{name}');
");
    }
}

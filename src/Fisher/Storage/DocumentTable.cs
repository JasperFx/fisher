using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Storage;

/// <summary>
///     Weasel table definition for one document type — <c>fi_doc_&lt;alias&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         The column set is intentionally small and fixed. Marten and Polecat carry more here
///         (duplicated fields, soft-delete markers, hierarchy discriminators, partition keys); each of
///         those is additive and can arrive without changing the columns below.
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

        // The concrete .NET type the row was written as. Written on every save; not selected on the
        // read path today, but it is what a future hierarchy discriminator would build on.
        AddColumn("dotnet_type", "TEXT").AllowNulls();

        // ISO-8601 UTC, same representation and the same parenthesized-expression trap as the event
        // tables: a non-literal DEFAULT must be wrapped in parentheses or CREATE TABLE will not parse.
        AddColumn("last_modified", "TEXT")
            .NotNull()
            .DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
    }
}

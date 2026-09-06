using Weasel.Core.MultiTenancy;
using Weasel.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     The SQL half of Fisher's tenant registry — Weasel's <see cref="IMasterTenantTableDialect" />
///     for SQLite (fisher#213, over weasel#567).
/// </summary>
/// <remarks>
///     <para>
///         Everything that is not dialect-specific — the cache, the guarded provisioning, the runtime
///         add/disable/enable/remove lifecycle, the seed list — is
///         <see cref="MasterTableTenancyBase{TDatabase,TDataSource}" />'s, which weasel#567 lifted out
///         of Marten's <c>MasterTableTenancy</c> (515 lines) and Polecat's (434). This is the part that
///         has to know that SQLite spells an upsert <c>on conflict … do update</c> and a boolean
///         <c>0</c> / <c>1</c>.
///     </para>
///     <para>
///         <b>The table name folds the logical schema in, as every other Fisher table does.</b> SQLite
///         has no schemas, so <see cref="StoreOptions.DatabaseSchemaName" /> becomes a table prefix
///         (<see cref="FisherTableNaming" />) — <c>fi_tenants</c> under the default, and
///         <c>reporting_fi_tenants</c> under a logical schema called <c>reporting</c>. Two logical
///         stores sharing one registry file therefore keep separate registries, which is the same
///         isolation the prefix buys everywhere else.
///     </para>
///     <para>
///         ⚠️ <b><c>tenant_id</c> is declared <c>collate nocase</c>, and that is load-bearing rather
///         than a nicety.</b> The base compares cache keys with the comparer it is handed and Fisher
///         hands it <see cref="StringComparer.OrdinalIgnoreCase" />, matching
///         <see cref="SeparateDatabaseTenancy" /> and <see cref="DynamicTenancy" />. SQLite's default
///         collation is case-sensitive, so without this the cache and the table would disagree about
///         whether <c>Acme</c> and <c>acme</c> are one tenant — a tenant that resolves through the
///         cache and not through a lookup, which reads as an intermittent. NOCASE folds ASCII only,
///         which is the same reach <see cref="StringComparer.OrdinalIgnoreCase" /> has for the
///         identifier shapes a tenant id realistically takes.
///     </para>
///     <para>
///         <b>The upsert does not clear the disabled flag</b>, which the dialect contract explicitly
///         leaves to the dialect and on which the two shipped stores disagree — Polecat's <c>MERGE</c>
///         re-enables, Marten's <c>on conflict</c> does not. Fisher follows Marten: re-enabling a
///         suspended tenant as a side effect of correcting its connection string would silently undo a
///         deliberate suspension, and <c>EnableTenantAsync</c> is one call away.
///     </para>
/// </remarks>
internal sealed class SqliteMasterTenantTableDialect : IMasterTenantTableDialect
{
    public static SqliteMasterTenantTableDialect Instance { get; } = new();

    /// <summary>
    ///     The registry's unqualified table name before the logical schema is folded in.
    /// </summary>
    public const string TableName = FisherTableNaming.FamilyPrefix + "tenants";

    /// <remarks>
    ///     Never actually qualified — the schema is folded into the name, so what comes back is one
    ///     quoted identifier. Same rule <c>FisherTableNaming.ObjectFor</c> follows for every other
    ///     table, and the reason nothing Fisher emits ever renders as <c>schema.table</c>.
    /// </remarks>
    public string QualifiedTableName(string schemaName, string tableName)
        => SchemaUtils.QuoteName(FisherTableNaming.UserTableName(schemaName, tableName));

    /// <remarks>
    ///     <c>if not exists</c> rather than a prior existence check, because the base's semaphore
    ///     guards one process and a deployment has several.
    /// </remarks>
    public string CreateControlTable(string schemaName, string tableName, string qualifiedTableName)
        => $"""
            create table if not exists {qualifiedTableName} (
                tenant_id text not null collate nocase primary key,
                connection_string text not null,
                disabled integer not null default 0
            )
            """;

    public string SelectEnabledTenants(string qualifiedTableName)
        => $"select tenant_id, connection_string from {qualifiedTableName} where disabled = 0";

    public string SelectDisabledTenantIds(string qualifiedTableName)
        => $"select tenant_id from {qualifiedTableName} where disabled <> 0";

    /// <remarks>
    ///     The <c>and disabled = 0</c> half is not optional: without it a suspended tenant resolves and
    ///     opens sessions, which is the one thing suspending is for — silently, because everything else
    ///     about the tenant is intact.
    /// </remarks>
    public string SelectConnectionString(string qualifiedTableName)
        => $"select connection_string from {qualifiedTableName} where tenant_id = @id and disabled = 0";

    public string UpsertTenant(string qualifiedTableName)
        => $"""
            insert into {qualifiedTableName} (tenant_id, connection_string, disabled)
            values (@id, @connection, 0)
            on conflict (tenant_id) do update set connection_string = excluded.connection_string
            """;

    /// <remarks>
    ///     Removes the <em>record</em>. The tenant's database file is not touched — see
    ///     <see cref="MasterTableTenantSource" /> for why Fisher will not delete one.
    /// </remarks>
    public string DeleteTenant(string qualifiedTableName)
        => $"delete from {qualifiedTableName} where tenant_id = @id";

    /// <inheritdoc cref="DeleteTenant" />
    public string DeleteAllTenants(string qualifiedTableName)
        => $"delete from {qualifiedTableName}";

    public string SetTenantDisabled(string qualifiedTableName, bool disabled)
        => $"update {qualifiedTableName} set disabled = {(disabled ? 1 : 0)} where tenant_id = @id";
}

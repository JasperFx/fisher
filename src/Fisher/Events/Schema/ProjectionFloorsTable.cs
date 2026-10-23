using Fisher.Storage;
using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_projection_floors</c> — the persisted start floor of each
///     projection (fisher#471, jasperfx#809).
/// </summary>
/// <remarks>
///     <para>
///         <b>A table of its own rather than a column on <c>fi_event_progression</c></b>, because a floor has
///         to survive exactly what a progression row does not: rebuild teardown deletes a projection's
///         progression rows, and "no progression row" is how a fresh shard is recognised. A floor on that
///         row would be deleted by the rebuild that is supposed to honour it.
///     </para>
///     <para>
///         Keyed by the projection's name <em>without</em> its version, so a version bump inherits the
///         floor, plus a tenant. The tenant is an empty string for a store-global floor rather than NULL,
///         because SQLite treats NULLs in a primary key as distinct and two store-global floors for one
///         projection would then both be allowed.
///     </para>
///     <para>
///         <b>Not cleared by <c>DeleteAllEventDataAsync</c>, <c>ResetAllDataAsync</c> or
///         <c>DeleteAllTenantDataAsync</c></b>, deliberately: a floor describes where a projection's history
///         starts, not data it owns. <c>CompletelyRemoveAllAsync</c> drops it with every other table.
///     </para>
/// </remarks>
internal class ProjectionFloorsTable : Table
{
    public const string TableSuffix = "projection_floors";

    /// <summary>What a null tenant is stored as.</summary>
    public const string StoreGlobalTenant = "";

    public ProjectionFloorsTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("projection_name", "TEXT").NotNull().AsPrimaryKey();
        AddColumn(StorageConstants.TenantIdColumn, "TEXT").NotNull().AsPrimaryKey();
        AddColumn("floor", "INTEGER").NotNull();
        AddColumn("last_updated", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
    }
}

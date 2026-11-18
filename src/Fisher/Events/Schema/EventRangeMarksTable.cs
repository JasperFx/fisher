using Fisher.Storage;
using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_event_range_marks</c>: the event-range marks of jasperfx#1215 / #1222,
///     where a stored-event rewrite and a projection backfill keep their ceiling and their cursor.
/// </summary>
/// <remarks>
///     <para>
///         Stored the way <see cref="ProjectionFloorsTable" /> stores floors, for the same reason: a mark has to
///         outlive rebuild teardown and data cleaning. Keyed by the mark's string key (see
///         <c>JasperFx.Events.EventRangeMarks</c>) plus a tenant, which is an empty string for a store-global mark
///         rather than NULL, because SQLite treats NULLs in a primary key as distinct.
///     </para>
///     <para>
///         <b>Not cleared by <c>DeleteAllEventDataAsync</c>, <c>ResetAllDataAsync</c> or
///         <c>DeleteAllTenantDataAsync</c></b>. <c>CompletelyRemoveAllAsync</c> drops it with every other table.
///     </para>
/// </remarks>
internal class EventRangeMarksTable : Table
{
    public const string TableSuffix = "event_range_marks";

    /// <summary>What a null tenant is stored as.</summary>
    public const string StoreGlobalTenant = "";

    public EventRangeMarksTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("mark_key", "TEXT").NotNull().AsPrimaryKey();
        AddColumn(StorageConstants.TenantIdColumn, "TEXT").NotNull().AsPrimaryKey();
        AddColumn("mark", "INTEGER").NotNull();
        AddColumn("last_updated", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
    }
}

using Fisher.Storage;
using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     <c>fi_dcb_tag_version</c>: one version per (tag key, tag value, tenant), which moves whenever something other
///     than an append changes the events a tag reaches (fisher#635, the port of marten#6035 and marten#6078).
/// </summary>
/// <remarks>
///     <para>
///         A DCB boundary captures the rows for its query's tags before it reads the events, and its save compares
///         them inside the write transaction. A tombstone, an archive, an un-archive, <c>AssignTagWhere</c>, and the
///         deletes and replacements of stream compaction move the row of every tag on the events they touch. A
///         missing row reads as 0.
///     </para>
///     <para>
///         <b>Appends do not move these rows, unlike Marten and Polecat.</b> SQLite has one writer at a time, so
///         Fisher's save can ask directly whether an event matching the boundary's query was appended after the
///         last sequence it read, and that check honours the query's event types exactly. The rows are for what
///         that check cannot see: events removed, hidden, brought back, or tagged below the boundary's last
///         sequence.
///     </para>
///     <para>
///         <c>tag_table</c> is the tag type's table suffix for the version every event carrying the value moves, and
///         <c>{suffix}:{event type name}</c> for the one only events of that type move, Marten's keys
///         (<c>DcbTagVersionKeys</c>). A condition that names an event type is checked against its type's row, so
///         tombstoning a stream whose tagged events are of another type is not a conflict.
///     </para>
///     <para>
///         <b>A tag can be personal data</b>, so a tombstone deletes the row once no event carries the tag. That is
///         only safe because a version never repeats: every value comes from <see cref="DcbTagVersionSequenceTable" />
///         rather than from counting up the row, as on Marten and Polecat.
///     </para>
///     <para>
///         Under conjoined tenancy the tenant is the event's; otherwise every row is the default tenant's, because a
///         boundary's query then reads every tenant's events.
///     </para>
/// </remarks>
internal class DcbTagVersionTable : Table
{
    public const string TableSuffix = "dcb_tag_version";

    public DcbTagVersionTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("tag_table", "TEXT").NotNull().AsPrimaryKey();
        AddColumn("tag_value", "TEXT").NotNull().AsPrimaryKey();
        AddColumn(StorageConstants.TenantIdColumn, "TEXT").NotNull().AsPrimaryKey();
        AddColumn("version", "INTEGER").NotNull();
    }
}

/// <summary>
///     <c>fi_dcb_tag_version_sequence</c>: the one-row counter every <see cref="DcbTagVersionTable" /> version comes
///     from, SQLite having no sequences.
/// </summary>
/// <remarks>
///     Never cleared by <c>DeleteAllEventDataAsync</c> or <c>DeleteAllTenantDataAsync</c>, so a version is never
///     handed out twice. It holds no data, only a number.
/// </remarks>
internal class DcbTagVersionSequenceTable : Table
{
    public const string TableSuffix = "dcb_tag_version_sequence";

    public DcbTagVersionSequenceTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("id", "INTEGER").NotNull().AsPrimaryKey();
        AddColumn("value", "INTEGER").NotNull();
    }
}

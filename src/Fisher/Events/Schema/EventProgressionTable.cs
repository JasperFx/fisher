using Fisher.Storage;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_event_progression</c> — how far each async projection shard
///     has processed, plus the high-water mark row.
/// </summary>
internal class EventProgressionTable : Table
{
    public const string TableSuffix = "event_progression";

    public EventProgressionTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("name", "TEXT").AsPrimaryKey().NotNull();
        AddColumn("last_seq_id", "INTEGER").NotNull().DefaultValue(0);
        AddColumn("last_updated", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        // The extended monitoring columns. Created whatever EnableExtendedProgressionTracking says
        // (fisher#664, as Marten's have been since marten#5309): the flag decides only whether the
        // daemon writes and reads them. When the flag decided the shape, a flags-off store migrating a
        // file a flags-on store had shaped dropped these columns, and the flags-on store then failed
        // every progression read. All nullable, so adding them to an existing file is a column add
        // rather than a rewrite.
        AddColumn("heartbeat", "TEXT").AllowNulls();
        AddColumn("agent_status", "TEXT").AllowNulls();
        AddColumn("pause_reason", "TEXT").AllowNulls();
        AddColumn("running_on_node", "TEXT").AllowNulls();
        AddColumn("warning_behind_threshold", "INTEGER").AllowNulls();
        AddColumn("critical_behind_threshold", "INTEGER").AllowNulls();
        AddColumn("failure_category", "TEXT").AllowNulls();
        AddColumn("failure_event_sequence", "INTEGER").AllowNulls();
        AddColumn("failure_event_type", "TEXT").AllowNulls();
        AddColumn("failure_event_tenant_id", "TEXT").AllowNulls();

        // fisher#652 / marten#6003: how many identities the shard holds parked, for a monitor reading a node
        // that is down
        AddColumn("parked_count", "INTEGER").AllowNulls();
    }
}

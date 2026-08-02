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

        // Opt-in monitoring columns. All nullable so enabling the flag against an existing database is
        // a pure column-add migration rather than a rewrite.
        if (events.EnableExtendedProgressionTracking)
        {
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
        }
    }
}

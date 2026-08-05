using Fisher.Storage;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_dead_letters</c> — one row per event a projection shard could
///     not apply and was configured to skip.
/// </summary>
/// <remarks>
///     <para>
///         Columns follow <see cref="JasperFx.Events.Daemon.DeadLetterEvent" /> one for one, so the
///         shared tooling (CritterWatch) reads Fisher's dead letters with the same shape it reads
///         Marten's and Polecat's.
///     </para>
///     <para>
///         <strong>There is deliberately no foreign key to <c>fi_events</c>.</strong> The tag tables
///         carry one because a tag is meaningless without its event; a dead letter is the opposite —
///         it is the record that something went wrong, and it has to survive the event being archived,
///         compacted away, or deleted by a cleaner. A cascade here would erase exactly the evidence an
///         operator came looking for.
///     </para>
///     <para>
///         The <c>id</c> is a version-7 Guid assigned by JasperFx when the dead letter is constructed,
///         so it is time-ordered and known before the (retried, background) write lands. It binds as
///         lowercase canonical TEXT like every other Guid in Fisher.
///     </para>
/// </remarks>
internal class DeadLetterTable : Table
{
    public const string TableSuffix = "dead_letters";

    public DeadLetterTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("id", "TEXT").AsPrimaryKey().NotNull();
        AddColumn("projection_name", "TEXT").NotNull();
        AddColumn("shard_name", "TEXT").NotNull();
        AddColumn("event_sequence", "INTEGER").NotNull();
        AddColumn("tenant_id", "TEXT").AllowNulls();
        AddColumn("exception_type", "TEXT").AllowNulls();
        AddColumn("exception_message", "TEXT").AllowNulls();
        AddColumn("timestamp", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        // Every read is "this shard's dead letters" — a count, or the most recent page of them. Leading
        // with the shard pair makes both a range scan, and the trailing sequence serves the paged
        // drill-in's ordering without a separate sort.
        Indexes.Add(new IndexDefinition(
            $"ix_{FisherTableNaming.TableName(events.DatabaseSchemaName, TableSuffix)}_shard")
        {
            Columns = ["projection_name", "shard_name", "event_sequence"]
        });
    }
}

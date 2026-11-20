using Fisher.Storage;
using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_parked_identities</c> (fisher#652, marten#6003): one row per projection
///     identity the async daemon parked because its slice failed.
/// </summary>
/// <remarks>
///     <para>
///         Columns follow <see cref="JasperFx.Events.Daemon.ParkedIdentity" /> one for one. The key is the projection,
///         its version, the shard key, the slice's tenant and the identity's canonical text, so blue/green versions of
///         a projection park independently. The tenant is never null: SQLite treats NULLs in a primary key as distinct.
///     </para>
///     <para>
///         <b>Always created</b>, like Marten's <c>mt_parked_identities</c>, so two stores over one file never disagree
///         about its shape. No copy of the failing event: it is still in <c>fi_events</c>.
///     </para>
///     <para>
///         Cleared by a projection's rebuild teardown, a rewind (rows that failed above the new floor), a tenant's
///         data wipe and the eject of a retired projection version. <c>DeleteAllEventDataAsync</c> clears it too,
///         because the events it points at are gone.
///     </para>
/// </remarks>
internal class ParkedIdentitiesTable : Table
{
    public const string TableSuffix = "parked_identities";

    public ParkedIdentitiesTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        AddColumn("projection_name", "TEXT").NotNull().AsPrimaryKey();
        AddColumn("projection_version", "INTEGER").NotNull().AsPrimaryKey();
        AddColumn("shard_key", "TEXT").NotNull().AsPrimaryKey();
        AddColumn(StorageConstants.TenantIdColumn, "TEXT").NotNull().AsPrimaryKey();
        AddColumn("identity", "TEXT").NotNull().AsPrimaryKey();

        AddColumn("identity_type", "TEXT").NotNull();
        AddColumn("first_failing_seq", "INTEGER").NotNull();
        AddColumn("resume_after", "INTEGER").NotNull();
        AddColumn("failure_category", "TEXT").AllowNulls();
        AddColumn("exception_type", "TEXT").AllowNulls();
        AddColumn("exception_message", "TEXT").AllowNulls();
        AddColumn("event_type", "TEXT").AllowNulls();
        AddColumn("event_id", "TEXT").AllowNulls();
        AddColumn("failing_stream", "TEXT").AllowNulls();
        AddColumn("parked_at", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
        AddColumn("replay_attempts", "INTEGER").NotNull().DefaultValue(0);
        AddColumn("last_attempt_at", "TEXT").AllowNulls();
        AddColumn("last_attempt_error", "TEXT").AllowNulls();
        AddColumn("requested_action", "TEXT").AllowNulls();
        AddColumn("requested_at", "TEXT").AllowNulls();
    }
}

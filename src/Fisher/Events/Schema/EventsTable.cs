using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Events;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_events</c> — the individual event rows.
/// </summary>
/// <remarks>
///     <para>
///         The SQLite type system is affinity-based: only INTEGER, REAL, TEXT, BLOB and NULL exist.
///         Guids, timestamps and JSON all land in TEXT, which is why this table's column types look
///         so much flatter than Polecat's <c>pc_events</c>.
///     </para>
///     <para>
///         <c>seq_id</c> is <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>. The AUTOINCREMENT keyword is
///         load-bearing rather than decorative here: a bare <c>INTEGER PRIMARY KEY</c> is an alias for
///         the rowid, and SQLite will reuse the id of a deleted row. The async daemon's high-water
///         mark assumes sequence numbers only ever move forward, so a reused seq_id would silently
///         hide events from every async projection. AUTOINCREMENT is what forbids the reuse.
///     </para>
/// </remarks>
internal class EventsTable : Table
{
    public const string TableSuffix = "events";

    public EventsTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        // Global event position. See the AUTOINCREMENT note in the class remarks.
        AddColumn("seq_id", "INTEGER").AsPrimaryKey().AutoIncrement();

        // Event identity — a Guid, stored as its TEXT representation.
        AddColumn("id", "TEXT").NotNull();

        // Stream reference. Both stream identity styles are TEXT in SQLite; the distinction survives
        // only in how Fisher binds and reads the value.
        AddColumn("stream_id", "TEXT").NotNull();

        // Version within the stream.
        AddColumn("version", "INTEGER").NotNull();

        // Event body as JSON text. SQLite's json1 functions operate on TEXT directly.
        AddColumn("data", "TEXT").NotNull();

        // Event type alias for deserialization.
        AddColumn("type", "TEXT").NotNull();

        // ISO-8601 UTC timestamp. SQLite has no date/time type; CURRENT_TIMESTAMP would yield
        // 'YYYY-MM-DD HH:MM:SS' with no sub-second precision, which is too coarse to order events
        // appended within the same second, so the default is an explicit strftime with milliseconds.
        AddColumn("timestamp", "TEXT")
            .NotNull()
            .DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        AddColumn(StorageConstants.TenantIdColumn, "TEXT")
            .NotNull()
            .DefaultValueByString(StorageConstants.DefaultTenantId);

        // Fully qualified .NET type name for deserialization.
        AddColumn("dotnet_type", "TEXT").AllowNulls();

        if (events.EventOptions.EnableCorrelationId)
        {
            AddColumn("correlation_id", "TEXT").AllowNulls();
        }

        if (events.EventOptions.EnableCausationId)
        {
            AddColumn("causation_id", "TEXT").AllowNulls();
        }

        if (events.EventOptions.EnableHeaders)
        {
            AddColumn("headers", "TEXT").AllowNulls();
        }

        if (events.EventOptions.EnableUserName)
        {
            AddColumn("user_name", "TEXT").AllowNulls();
        }

        // SQLite has no BOOLEAN; 0/1 in an INTEGER column is the convention.
        AddColumn("is_archived", "INTEGER").NotNull().DefaultValue(0);

        var streamAndVersion = events.TenancyStyle == TenancyStyle.Conjoined
            ? new[] { StorageConstants.TenantIdColumn, "stream_id", "version" }
            : new[] { "stream_id", "version" };

        Indexes.Add(new IndexDefinition(
            $"ix_{FisherTableNaming.TableName(events.DatabaseSchemaName, TableSuffix)}_stream_and_version")
        {
            IsUnique = true,
            Columns = streamAndVersion
        });

        // The async daemon pages events by seq_id filtered on is_archived, which is a full scan
        // against the primary key alone.
        Indexes.Add(new IndexDefinition(
            $"ix_{FisherTableNaming.TableName(events.DatabaseSchemaName, TableSuffix)}_archived_seq")
        {
            Columns = ["is_archived", "seq_id"]
        });

        // No foreign key to fi_streams. SQLite enforces FKs only when the foreign_keys PRAGMA is on
        // (Weasel's default profile does set it), but the constraint would force the streams row to
        // be inserted and committed ahead of its events within the same batch, which is exactly the
        // ordering the append planner already guarantees. Referential integrity is an application
        // invariant here, as it is in Polecat's conjoined path.
    }
}

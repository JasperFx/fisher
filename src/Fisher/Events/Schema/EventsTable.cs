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

    /// <summary>
    ///     What <c>data</c> holds for a row whose body is in <c>data_binary</c> (fisher#93).
    /// </summary>
    /// <remarks>
    ///     Valid JSON rather than an empty string, so that <c>json_valid(data)</c> and every
    ///     <c>json_extract</c> over the column keep answering rather than erroring on a binary row.
    ///     They answer "no such member", which is the truth — the member is in the BLOB.
    /// </remarks>
    public const string JsonPlaceholder = "{}";

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
        //
        // Stays NOT NULL even for a binary row, which carries the placeholder EventsTable.JsonPlaceholder
        // here instead (fisher#93). Two bytes per binary row buys the whole upgrade story: relaxing a
        // NOT NULL on SQLite means rebuilding the table, where adding data_binary below is a plain
        // ALTER TABLE ADD COLUMN that an existing store takes in place.
        AddColumn("data", "TEXT").NotNull();

        // BLOB affinity, and a column of its own rather than BLOBs mixed into data. SQLite would
        // tolerate the mixture — affinity is a preference, not a constraint — but then typeof(data)
        // becomes the only way to tell a body's encoding apart, and json_extract over the column
        // silently stops meaning anything for the rows that are binary. One nullable column per row
        // buys an unambiguous shape.
        //
        // Unconditional, and that is the load-bearing half of fisher#93 rather than a simplification:
        // it is what makes a row, not the store's current configuration, the thing that says how its
        // body is encoded. So marking one event type [BinaryEvent] on a live store needs no migration
        // and no schema decision taken in advance, and un-marking it leaves the rows already written
        // perfectly readable.
        AddColumn("data_binary", "BLOB").AllowNulls();

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

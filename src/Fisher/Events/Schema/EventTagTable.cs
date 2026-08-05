using Fisher.Storage;
using JasperFx.Events.Tags;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for one DCB tag type — <c>fi_event_tag_&lt;suffix&gt;</c>, one row per
///     (tag value, event).
/// </summary>
/// <remarks>
///     <para>
///         Mirrors Marten's <c>EventTagTable</c>, minus everything that is PostgreSQL-shaped: there is
///         no <c>uuid</c> type, no archived-stream list partitioning, and no shortened identifier
///         handling. What survives is the composite primary key with <c>value</c> first, which is what
///         makes a tag lookup a range scan on the leading key rather than a table scan.
///     </para>
///     <para>
///         The primary key is also what makes <c>AssignTagWhere</c> idempotent for free: re-tagging an
///         event that already carries the tag violates the key, so the write path can use
///         <c>on conflict do nothing</c> rather than reading first.
///     </para>
///     <para>
///         The foreign key to <c>fi_events(seq_id)</c> is real and enforced —
///         <see cref="Weasel.Sqlite.SqlitePragmaSettings" /> sets <c>PRAGMA foreign_keys = ON</c> by
///         default. A consumer that overrides <c>StoreOptions.PragmaSettings</c> with foreign keys off
///         turns it into documentation, which is worth knowing before relying on it to reject a tag
///         pointing at an event that does not exist.
///     </para>
/// </remarks>
internal class EventTagTable : Table
{
    public EventTagTable(EventGraph events, ITagTypeRegistration registration)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, SuffixFor(registration)))
    {
        // Value first: a tag query filters on value and joins out to the events, so leading with it
        // lets the primary key index serve the lookup.
        AddColumn("value", ColumnTypeFor(registration.SimpleType)).NotNull().AsPrimaryKey();

        AddColumn("seq_id", "INTEGER").NotNull().AsPrimaryKey()
            .ForeignKeyTo(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, EventsTable.TableSuffix), "seq_id");
    }

    /// <summary>
    ///     The table suffix for a registration, e.g. <c>event_tag_region</c> — which
    ///     <see cref="FisherTableNaming" /> then prefixes into <c>fi_event_tag_region</c>.
    /// </summary>
    internal static string SuffixFor(ITagTypeRegistration registration)
        => $"event_tag_{registration.TableSuffix}";

    /// <summary>
    ///     The SQLite storage type for a tag's inner primitive.
    /// </summary>
    /// <remarks>
    ///     A <see cref="Guid" /> is TEXT, as everywhere else in Fisher, and the write path must bind it
    ///     as the lowercase canonical form. Binding the raw Guid writes a 16-byte BLOB that never
    ///     matches; binding it uppercase misses under SQLite's case-sensitive default collation. Both
    ///     fail by finding nothing rather than by erroring — the trap
    ///     <c>SqliteGuidIdentification</c> exists for.
    /// </remarks>
    internal static string ColumnTypeFor(Type simpleType)
    {
        if (simpleType == typeof(string) || simpleType == typeof(Guid))
        {
            return "TEXT";
        }

        if (simpleType == typeof(int) || simpleType == typeof(long) || simpleType == typeof(short))
        {
            return "INTEGER";
        }

        throw new ArgumentOutOfRangeException(nameof(simpleType),
            $"Unsupported tag value type '{simpleType.Name}'. Fisher supports string, Guid, int, long and short.");
    }
}

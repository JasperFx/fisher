using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for <c>fi_streams</c> — one row of metadata per event stream.
/// </summary>
internal class StreamsTable : Table
{
    public const string TableSuffix = "streams";

    public StreamsTable(EventGraph events)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, TableSuffix))
    {
        var conjoined = events.TenancyStyle == TenancyStyle.Conjoined;

        // Under conjoined tenancy the primary key is (tenant_id, id). Column order matters: SQLite
        // resolves a composite primary key into an index over the columns in declaration order, so
        // declaring tenant_id first is what lets a single-tenant query seek on its prefix.
        if (conjoined)
        {
            AddColumn(StorageConstants.TenantIdColumn, "TEXT").AsPrimaryKey().NotNull();
        }

        // Guid and string stream identities are both TEXT under SQLite's affinity rules.
        AddColumn("id", "TEXT").AsPrimaryKey().NotNull();

        AddColumn("type", "TEXT").AllowNulls();
        AddColumn("version", "INTEGER").NotNull().DefaultValue(0);

        AddColumn("timestamp", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
        AddColumn("created", "TEXT").NotNull().DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        if (!conjoined)
        {
            AddColumn(StorageConstants.TenantIdColumn, "TEXT")
                .NotNull()
                .DefaultValueByString(StorageConstants.DefaultTenantId);
        }

        AddColumn("is_archived", "INTEGER").NotNull().DefaultValue(0);

        // The compaction watermark (jasperfx#740): the stream version through which events have been
        // folded into a Compacted<T> snapshot. 0 means never compacted — including streams compacted
        // before this column existed, which is the honest default for metadata that was never
        // recorded. NOT NULL DEFAULT 0 is what makes the upgrade a plain ALTER TABLE ADD COLUMN on an
        // existing file, and what keeps every INSERT (all of which name their columns explicitly)
        // untouched.
        AddColumn("compacted_version", "INTEGER").NotNull().DefaultValue(0);
    }
}

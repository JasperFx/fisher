using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     <c>fi_natural_key_&lt;alias&gt;</c> — the lookup from an aggregate's business identifier to the
///     stream that holds it (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         One table per aggregate type declaring a natural key. What it buys is
///         <c>FetchForWriting&lt;Order, string&gt;("ORD-1234")</c> without the application maintaining
///         its own key-to-stream table and keeping it transactionally consistent with the appends.
///     </para>
///     <para>
///         <b>There is no <c>is_archived</c> column here, where Polecat's table has one.</b> Polecat
///         copies the flag over from <c>pc_streams</c> and keeps it in sync from a projection that
///         watches for the <c>Archived</c> event — which then needs a second, rebuild-time entry point
///         to repopulate the table after a teardown. Fisher archives a stream with a direct operation
///         rather than by appending an event, so there is nothing to watch; and the lookup joins
///         <c>fi_streams</c> anyway, for the version. Reading <c>is_archived</c> off that join makes the
///         streams table the single source of truth and removes the sync step, the projection and the
///         rebuild path with it.
///     </para>
///     <para>
///         <b>No foreign key to <c>fi_streams</c>, and uniformly so.</b> Polecat declares one for a
///         single-tenant store and omits it under conjoined tenancy, where its composite key defeats
///         Weasel.SqlServer's alphabetical column sorting — so the two tenancy styles behave
///         differently there. One rule is worth more than referential integrity in half the
///         configurations, and a row whose stream is gone resolves to nothing anyway, because the
///         lookup's join is what produces an answer at all.
///     </para>
/// </remarks>
internal class NaturalKeyTable : Table
{
    internal const string KeyColumn = "natural_key_value";

    public NaturalKeyTable(EventGraph events, NaturalKeyDefinition naturalKey)
        : base(events.NaturalKeyTableName(naturalKey.AggregateType))
    {
        // INTEGER for a numeric key so it compares and sorts as a number; TEXT otherwise. The declared
        // type is the column's comparison affinity, which is the same reason SqliteTypeFor is
        // load-bearing for a duplicated field rather than decorative.
        var columnType = naturalKey.InnerType == typeof(int) || naturalKey.InnerType == typeof(long)
            ? "INTEGER"
            : "TEXT";

        // Tenant first when there is one, so the composite key's leading column is the one every
        // lookup filters on — the same ordering fi_streams uses and for the same reason.
        if (events.TenancyStyle == TenancyStyle.Conjoined)
        {
            AddColumn(StorageConstants.TenantIdColumn, "TEXT")
                .NotNull()
                .DefaultValueByString(StorageConstants.DefaultTenantId)
                .AsPrimaryKey();
        }

        // The primary key is what makes a duplicate natural key fail rather than silently pointing at
        // two streams. Under conjoined tenancy the same key may exist once per tenant, which is why
        // the tenant column joins the key rather than replacing it.
        AddColumn(KeyColumn, columnType).NotNull().AsPrimaryKey();

        AddColumn(events.StreamIdentity == StreamIdentity.AsGuid ? "stream_id" : "stream_key", "TEXT")
            .NotNull();
    }
}

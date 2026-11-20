using Fisher.Storage;
using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Events.Schema;

/// <summary>
///     Weasel table definition for one projection version's slice index (fisher#652, marten#6003):
///     <c>fi_slice_index_&lt;projection&gt;</c>, and <c>fi_slice_index_&lt;projection&gt;_v&lt;n&gt;</c> for a version
///     above 1. One row per (tenant, identity, event) the daemon applied.
/// </summary>
/// <remarks>
///     <para>
///         Only a projection that calls <c>UseSliceIndex()</c> gets one. The identity column is TEXT for a Guid or
///         string identity (Guids as lowercase text, like everywhere else in Fisher) and INTEGER for an int or long.
///         The primary key leads with the tenant and identity, so reading one identity's events is a range scan;
///         the <c>seq_id</c> index serves pruning and rewinds.
///     </para>
///     <para>
///         No foreign key to <c>fi_events</c>: compacting or tombstoning a stream would have to cascade, and a row
///         whose event is gone is simply ignored on read.
///     </para>
/// </remarks>
internal class SliceIndexTable : Table
{
    public const string TablePrefix = "slice_index_";

    public SliceIndexTable(EventGraph events, string projectionName, uint version, Type identityType)
        : base(FisherTableNaming.ObjectFor(events.DatabaseSchemaName, SuffixFor(projectionName, version)))
    {
        AddColumn(StorageConstants.TenantIdColumn, "TEXT").NotNull().AsPrimaryKey();
        AddColumn("identity", IdentityColumnType(identityType)).NotNull().AsPrimaryKey();
        AddColumn("seq_id", "INTEGER").NotNull().AsPrimaryKey();

        Indexes.Add(new IndexDefinition(
            $"ix_{FisherTableNaming.TableName(events.DatabaseSchemaName, SuffixFor(projectionName, version))}_seq_id")
        {
            Columns = ["seq_id"]
        });
    }

    /// <summary>
    ///     The table suffix for one projection version. The name is lowercased and anything but a letter, digit or
    ///     underscore becomes an underscore, so any projection name gives a plain SQLite identifier.
    /// </summary>
    internal static string SuffixFor(string projectionName, uint version)
    {
        var chars = projectionName.ToLowerInvariant()
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? c : '_')
            .ToArray();

        var name = TablePrefix + new string(chars);
        return version > 1 ? $"{name}_v{version}" : name;
    }

    /// <summary>The column type for an identity's simple type.</summary>
    internal static string IdentityColumnType(Type simpleType)
        => simpleType == typeof(int) || simpleType == typeof(long) ? "INTEGER" : "TEXT";
}

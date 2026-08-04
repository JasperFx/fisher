using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;

namespace Fisher.Storage.Sequences;

/// <summary>
///     The Weasel feature schema for <c>fi_hilo</c>.
/// </summary>
/// <remarks>
///     Included only when a registered document type actually has a numeric identity, so a store that
///     never asks for one does not carry an empty sequence table. <see cref="HiloSequence" /> still
///     creates the table for itself — an id is assigned at <c>Store</c>, long before any commit-time
///     schema work — so this exists to put the table in the migration a consumer inspects or scripts
///     out, not because the runtime depends on it.
/// </remarks>
internal class HiloFeatureSchema : FeatureSchemaBase
{
    private readonly string _schemaName;

    public HiloFeatureSchema(string schemaName)
        : base("HiLo", new SqliteMigrator())
    {
        _schemaName = schemaName;
    }

    public override Type StorageType => typeof(HiloFeatureSchema);

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return new HiloTable(_schemaName);
    }
}

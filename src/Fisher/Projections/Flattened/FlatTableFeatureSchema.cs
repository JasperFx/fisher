using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;

namespace Fisher.Projections.Flattened;

/// <summary>
///     The Weasel feature schema for one flat-table projection's table.
/// </summary>
/// <remarks>
///     <para>
///         A flat table goes through the same migration path as every other Fisher table rather than
///         being created by a CREATE TABLE the projection issues on first use. That is what makes
///         <see cref="StoreOptions.AutoCreateSchemaObjects" /> mean the same thing here as everywhere
///         else, and what puts the table in the output of a schema dump or a patch.
///     </para>
///     <para>
///         <see cref="StorageType" /> is the projection's own type because a flat table has no
///         document type to name — the property is only a key for feature ordering and reporting.
///     </para>
/// </remarks>
internal sealed class FlatTableFeatureSchema : FeatureSchemaBase
{
    private readonly FlatTableProjection _projection;

    public FlatTableFeatureSchema(FlatTableProjection projection)
        : base($"FlatTable:{projection.Table.Identifier.Name}", new SqliteMigrator())
    {
        _projection = projection;
    }

    public override Type StorageType => _projection.GetType();

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return _projection.Table;
    }
}

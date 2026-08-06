using Fisher.Projections.Flattened;

namespace JasperFx.Events.ComplianceTests;

/*
 * The per-consumer half of FlatTableProjectionCompliance's projection. Everything portable — the
 * table name, the projection name, every event mapping — lives in the shared partial that ships with
 * the compliance package; a store supplies the constructor and the primary key column, because both
 * are dialect-specific and no single base(...) call satisfies Marten, Polecat and Fisher.
 *
 * Two Fisher-specific points in three lines of code:
 *
 *  - The schema name is passed rather than resolved. SQLite has no schemas, so Fisher folds the
 *    logical schema into the table's *name*; passing SchemaName here is what makes the physical table
 *    `compliance_flat_table_compliance_flat_values`, which is also what the fixture's QueryTableAsync
 *    resolves to.
 *
 *  - The primary key is TEXT because it holds a stream's Guid, and Fisher stores a Guid as lowercase
 *    canonical text everywhere — the SqliteGuidIdentification rule. There is no native uuid type to
 *    declare instead.
 */

public partial class ComplianceFlatTableProjection : FlatTableProjection
{
    public ComplianceFlatTableProjection() : base(TableName, SchemaName)
    {
        Table.AddColumn("id", "TEXT").NotNull().AsPrimaryKey();

        ConfigureMappings();
    }
}

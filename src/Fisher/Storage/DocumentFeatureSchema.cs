using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     The Weasel feature schema for one document type's table.
/// </summary>
/// <remarks>
///     One feature per document type rather than one for all of them, so adding a document type to an
///     application produces a migration that touches only its own table.
/// </remarks>
internal class DocumentFeatureSchema : FeatureSchemaBase
{
    private readonly DocumentMapping _mapping;

    public DocumentFeatureSchema(DocumentMapping mapping)
        : base($"Document:{mapping.DocumentType.FullName}", new SqliteMigrator())
    {
        _mapping = mapping;
    }

    public override Type StorageType => _mapping.DocumentType;

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return _mapping.BuildTable();
    }
}

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

    protected override IEnumerable<ISchemaObject> schemaObjects() => ObjectsFor(_mapping);

    /// <summary>
    ///     Every schema object one document type owns, for the full migration and for the on-demand path
    ///     alike.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The table first, then anything that depends on it. A full-text index adds a content view,
    ///         the FTS5 virtual table and three triggers — all of which name the document table, so the
    ///         order is load-bearing rather than tidy. See <see cref="FullText.FullTextSchema" />.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>One list, two callers, on purpose</b> (fisher#422). <c>FisherDatabase</c>'s on-demand
    ///         path used to build its own list, the table alone, and the two drifted: a store that never
    ///         ran the full migration had a document table and no full-text index, so every
    ///         <c>Search(...)</c> failed with <c>no such table</c> — on a fresh database only. Anything a
    ///         document type gains here reaches both paths.
    ///     </para>
    /// </remarks>
    internal static IEnumerable<ISchemaObject> ObjectsFor(DocumentMapping mapping)
    {
        yield return mapping.BuildTable();

        if (mapping.FullTextIndex is not null)
        {
            foreach (var schemaObject in FullText.FullTextSchema.ObjectsFor(mapping))
            {
                yield return schemaObject;
            }
        }
    }
}

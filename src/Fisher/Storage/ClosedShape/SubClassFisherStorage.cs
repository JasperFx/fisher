using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     Storage for a registered sub-class in a document hierarchy (fisher#17): Weasel's
///     <see cref="ClosedShapeSubClassDocumentStorage{TDoc,TRoot,TId}" />, which delegates everything to
///     the base type's storage and narrows the reads, derived only to carry
///     <see cref="IFisherDocumentStorage" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Writes delegate untouched.</b> Weasel's <c>DocumentDocTypeBinder</c> resolves the alias from
///         <c>document.GetType()</c>, so the base type's write operations already stamp a derived
///         instance with its own alias.
///     </para>
///     <para>
///         <b>Queries are narrowed in SQL, loads by id in memory</b>, by testing what came back. A load
///         names one row; a discriminator predicate on it would turn "that id is a different sub-class"
///         into the same answer as "no such id", and the id is unique across the whole hierarchy.
///     </para>
///     <para>
///         Weasel's class forwards every defaulted member of the storage interfaces explicitly,
///         <c>MappedVersionFor</c> included, which is the trap fisher#394 was when this class was
///         Fisher's own.
///     </para>
/// </remarks>
internal sealed class SubClassFisherStorage<TDoc, TBase, TId>
    : ClosedShapeSubClassDocumentStorage<TDoc, TBase, TId>, IFisherDocumentStorage
    where TDoc : notnull, TBase
    where TBase : notnull
    where TId : notnull
{
    private readonly IFisherDocumentStorage _root;

    internal SubClassFisherStorage(IDocumentStorage<TBase, TId> root,
        FisherSubClassRules<TDoc, TBase, TId> rules)
        : base(root, rules)
    {
        _root = (IFisherDocumentStorage)root;
    }

    public DeleteStyle DeleteStyle => _root.DeleteStyle;

    public IOperationFragment? UndeleteFragment => _root.UndeleteFragment;

    public bool IsConjoined => _root.IsConjoined;

    public string SelectColumnsSql => _root.SelectColumnsSql;

    public Weasel.Storage.IStorageOperation? RestoreForId(object id, string tenantId) => _root.RestoreForId(id, tenantId);
}

/// <summary>
///     What is Fisher's about a sub-class's storage: the <c>doc_type</c> filter, and where it goes.
/// </summary>
internal sealed class FisherSubClassRules<TDoc, TBase, TId> : IClosedShapeSubClassRules<TDoc, TBase, TId>
    where TDoc : notnull, TBase
    where TBase : notnull
    where TId : notnull
{
    public FisherSubClassRules(DocumentMapping mapping)
    {
        // Matches this sub-class and anything registered below it.
        DocumentTypeFilter = new Linq.SqlGeneration.LiteralSqlFragment(
            DocumentHierarchy.FilterSqlFor(mapping, typeof(TDoc)));
    }

    public ISqlFragment DocumentTypeFilter { get; }

    /// <remarks>
    ///     <b>The root's filters unchanged — the discriminator deliberately does not go here.</b> This
    ///     runs once per caller predicate, so folding the filter in would both repeat it for a
    ///     multi-clause query and, worse, omit it entirely from a query that has no predicate at all. The
    ///     query layer adds it once per statement instead; see <c>FisherQueryProvider.ApplyHierarchyFilter</c>.
    /// </remarks>
    public ISqlFragment FilterDocuments(IDocumentStorage<TBase, TId> root, ISqlFragment query,
        IStorageSession session)
        => root.FilterDocuments(query, session);

    /// <remarks>
    ///     Fisher's own compound fragment rather than Weasel's, so the LINQ layer finds the soft-delete
    ///     term inside it the way it always has.
    /// </remarks>
    public ISqlFragment? DefaultWhereFragment(IDocumentStorage<TBase, TId> root)
        => root.DefaultWhereFragment() is { } parent
            ? new Linq.SqlGeneration.CompoundWhereFragment(" and ", parent, DocumentTypeFilter)
            : DocumentTypeFilter;

    // TruncateSql stays null: a sub-class truncate clears the hierarchy's whole table, which is what
    // a rebuild's teardown needs -- a projection replaying into a hierarchy replays every sub-class,
    // and narrowing it would leave rows a replay cannot see but the unique id space still holds.
}

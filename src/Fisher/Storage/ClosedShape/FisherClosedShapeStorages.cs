using JasperFx;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

// Fisher's four document storages are Weasel's closed-shape tracking-mode bases (weasel#811),
// derived for one reason only: to carry IFisherDocumentStorage, which ~11 call sites type-test a
// storage for. Every behaviour -- the loads, Store, the identity map, the write operations for each
// concurrency mode, the selectors -- is Weasel's, and every SQLite decision is on the rules object
// (FisherStorageRules). That is the shape weasel#811 made public for exactly this; the factory's own
// leaves are sealed and internal, so a store with interfaces of its own derives the bases instead,
// and the bases build exactly the operations the leaves would, over exactly the same descriptor.
//
// Fisher used to carry ~1,000 lines of its own storage family here (fisher#465).

/// <summary>
///     The query-only flavor: Weasel's read-only storage, whose SELECT omits the id column and narrows
///     the metadata set — a contract with the query-only selectors rather than an optimization.
/// </summary>
internal sealed class QueryOnlyFisherStorage<TDoc, TId> : QueryOnlyClosedShapeDocumentStorage<TDoc, TId>,
    IFisherDocumentStorage
    where TDoc : notnull
    where TId : notnull
{
    private readonly FisherStorageRules<TDoc, TId> _fisher;

    public QueryOnlyFisherStorage(DocumentStorageDescriptor<TDoc, TId> descriptor, FisherStorageRules<TDoc, TId> rules)
        : base(descriptor, rules)
    {
        _fisher = rules;
        SelectColumnsSql = string.Join(", ", SelectFields());
    }

    public DeleteStyle DeleteStyle => _fisher.DeleteStyle;
    public bool IsConjoined => _fisher.IsConjoined;
    public string SelectColumnsSql { get; }

    Weasel.Storage.IStorageOperation? IFisherDocumentStorage.RestoreForId(object id, string tenantId)
        => RestoreForId((TId)id, tenantId);
}

/// <summary>
///     The lightweight flavor: loads go straight to the database, and nothing is remembered between
///     them.
/// </summary>
internal sealed class LightweightFisherStorage<TDoc, TId> : LightweightClosedShapeDocumentStorage<TDoc, TId>,
    IFisherDocumentStorage
    where TDoc : notnull
    where TId : notnull
{
    private readonly FisherStorageRules<TDoc, TId> _fisher;

    public LightweightFisherStorage(DocumentStorageDescriptor<TDoc, TId> descriptor, FisherStorageRules<TDoc, TId> rules)
        : base(descriptor, rules)
    {
        _fisher = rules;
        SelectColumnsSql = string.Join(", ", SelectFields());
    }

    public DeleteStyle DeleteStyle => _fisher.DeleteStyle;
    public bool IsConjoined => _fisher.IsConjoined;
    public string SelectColumnsSql { get; }

    Weasel.Storage.IStorageOperation? IFisherDocumentStorage.RestoreForId(object id, string tenantId)
        => RestoreForId((TId)id, tenantId);

    /// <inheritdoc cref="FisherStorages.LoadManyProjectedAsync{TDoc,TId}" />
    public override Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync(TId[] ids, IStorageDatabase database,
        string tenantId, CancellationToken token)
        => FisherStorages.LoadManyProjectedAsync(ids, () => base.LoadManyProjectedAsync(ids, database, tenantId, token));
}

/// <summary>
///     The identity-map flavor: a document loaded or stored in this session is returned again from
///     memory rather than re-read, and a second instance under a mapped id is refused.
/// </summary>
internal sealed class IdentityMapFisherStorage<TDoc, TId> : IdentityMapClosedShapeDocumentStorage<TDoc, TId>,
    IFisherDocumentStorage
    where TDoc : notnull
    where TId : notnull
{
    private readonly FisherStorageRules<TDoc, TId> _fisher;

    public IdentityMapFisherStorage(DocumentStorageDescriptor<TDoc, TId> descriptor, FisherStorageRules<TDoc, TId> rules)
        : base(descriptor, rules)
    {
        _fisher = rules;
        SelectColumnsSql = string.Join(", ", SelectFields());
    }

    public DeleteStyle DeleteStyle => _fisher.DeleteStyle;
    public bool IsConjoined => _fisher.IsConjoined;
    public string SelectColumnsSql { get; }

    Weasel.Storage.IStorageOperation? IFisherDocumentStorage.RestoreForId(object id, string tenantId)
        => RestoreForId((TId)id, tenantId);

    /// <inheritdoc cref="FisherStorages.LoadManyProjectedAsync{TDoc,TId}" />
    public override Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync(TId[] ids, IStorageDatabase database,
        string tenantId, CancellationToken token)
        => FisherStorages.LoadManyProjectedAsync(ids, () => base.LoadManyProjectedAsync(ids, database, tenantId, token));
}

/// <summary>
///     The dirty-tracking flavor: the identity map plus a change tracker per loaded document, which
///     Weasel's dirty-tracking selectors register. Detection and reset are the session's job:
///     <c>FisherSession.SaveChangesAsync</c> asks every tracker for its operation before taking the
///     batch, and re-baselines them after the write.
/// </summary>
internal sealed class DirtyTrackingFisherStorage<TDoc, TId> : DirtyTrackingClosedShapeDocumentStorage<TDoc, TId>,
    IFisherDocumentStorage
    where TDoc : notnull
    where TId : notnull
{
    private readonly FisherStorageRules<TDoc, TId> _fisher;

    public DirtyTrackingFisherStorage(DocumentStorageDescriptor<TDoc, TId> descriptor,
        FisherStorageRules<TDoc, TId> rules)
        : base(descriptor, rules)
    {
        _fisher = rules;
        SelectColumnsSql = string.Join(", ", SelectFields());
    }

    public DeleteStyle DeleteStyle => _fisher.DeleteStyle;
    public bool IsConjoined => _fisher.IsConjoined;
    public string SelectColumnsSql { get; }

    Weasel.Storage.IStorageOperation? IFisherDocumentStorage.RestoreForId(object id, string tenantId)
        => RestoreForId((TId)id, tenantId);

    /// <inheritdoc cref="FisherStorages.LoadManyProjectedAsync{TDoc,TId}" />
    public override Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync(TId[] ids, IStorageDatabase database,
        string tenantId, CancellationToken token)
        => FisherStorages.LoadManyProjectedAsync(ids, () => base.LoadManyProjectedAsync(ids, database, tenantId, token));
}

internal static class FisherStorages
{
    /// <summary>
    ///     The four storages of one document type, all over one descriptor and one rules object.
    /// </summary>
    public static DocumentProvider<TDoc> Build<TDoc, TId>(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor)
        where TDoc : notnull
        where TId : notnull
    {
        var rules = new FisherStorageRules<TDoc, TId>(mapping, descriptor);

        // All four flavors are built for every registered type, whether or not any session asks for
        // them: they are cheap, they are cached with the provider, and which one a session resolves is
        // decided per session by SessionOptions.Tracking.
        return new DocumentProvider<TDoc>(
            new QueryOnlyFisherStorage<TDoc, TId>(descriptor, rules),
            new LightweightFisherStorage<TDoc, TId>(descriptor, rules),
            new IdentityMapFisherStorage<TDoc, TId>(descriptor, rules),
            new DirtyTrackingFisherStorage<TDoc, TId>(descriptor, rules));
    }

    /// <summary>
    ///     The session-free load-many, which answers an empty id array without opening a connection
    ///     (fisher#446).
    /// </summary>
    /// <remarks>
    ///     The projected loader opens a connection of its own, so an empty array would cost a connection
    ///     as well as a statement that can only return no rows. Weasel's base does not guard it; the
    ///     session loads do.
    /// </remarks>
    public static Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync<TDoc, TId>(TId[] ids,
        Func<Task<IReadOnlyList<TDoc>>> load)
        => ids.Length == 0 ? Task.FromResult<IReadOnlyList<TDoc>>(Array.Empty<TDoc>()) : load();
}

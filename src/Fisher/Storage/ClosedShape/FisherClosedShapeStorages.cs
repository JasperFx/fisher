using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     The lightweight flavor: loads go straight to the database, and nothing is remembered between
///     them.
/// </summary>
internal abstract class LightweightFisherStorage<TDoc, TId> : FisherDocumentStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    protected LightweightFisherStorage(DocumentMapping mapping, DocumentStorageDescriptor<TDoc, TId> descriptor)
        : base(mapping, descriptor)
    {
    }

    public override Task<TDoc?> LoadAsync(TId id, IStorageSession session, CancellationToken token)
        => QueryOneAsync(id, session, token);

    public override Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
        => QueryManyAsync(ids, session, token);

    /// <summary>
    ///     Assign an identity if the document has none and announce it to the session. Queueing the
    ///     write is the session's job — <see cref="IStorageSession" /> has no queue of its own, so a
    ///     storage that tried to enqueue here would have to know a concrete session type.
    /// </summary>
    public override void Store(IStorageSession session, TDoc document)
        => session.MarkAsAddedForStorage(_descriptor.Identification.AssignIfMissing(document, session.Database),
            document);

    public override void Store(IStorageSession session, TDoc document, Guid? version)
    {
        if (version.HasValue)
        {
            session.Versions.StoreVersion<TDoc, TId>(Identity(document), version.Value);
        }

        Store(session, document);
    }

    /// <inheritdoc cref="FisherDocumentStorage{TDoc,TId}.UseNumericRevisions" />
    public override void Store(IStorageSession session, TDoc document, long revision)
        => throw new NotSupportedException(
            "Fisher does not implement numeric document revisions. Use optimistic concurrency instead.");
}

/// <summary>
///     The identity-map flavor: a document loaded or stored in this session is returned again from
///     memory rather than re-read.
/// </summary>
internal abstract class IdentityMapFisherStorage<TDoc, TId> : FisherDocumentStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    protected IdentityMapFisherStorage(DocumentMapping mapping, DocumentStorageDescriptor<TDoc, TId> descriptor)
        : base(mapping, descriptor)
    {
    }

    private Dictionary<TId, TDoc> MapFor(IStorageSession session)
    {
        if (session.ItemMap.TryGetValue(typeof(TDoc), out var raw) && raw is Dictionary<TId, TDoc> existing)
        {
            return existing;
        }

        var map = new Dictionary<TId, TDoc>();
        session.ItemMap[typeof(TDoc)] = map;

        return map;
    }

    public override async Task<TDoc?> LoadAsync(TId id, IStorageSession session, CancellationToken token)
    {
        var map = MapFor(session);

        if (map.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var document = await QueryOneAsync(id, session, token).ConfigureAwait(false);

        if (document is not null)
        {
            map[id] = document;
        }

        return document;
    }

    public override async Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
    {
        var documents = await QueryManyAsync(ids, session, token).ConfigureAwait(false);
        var map = MapFor(session);

        foreach (var document in documents)
        {
            map[Identity(document)] = document;
        }

        return documents;
    }

    public override void Store(IStorageSession session, TDoc document)
    {
        var id = _descriptor.Identification.AssignIfMissing(document, session.Database);

        // Mapped before the write runs, so a load later in the same session returns what the caller
        // stored rather than what the database still holds.
        MapFor(session)[id] = document;

        session.MarkAsAddedForStorage(id, document);
    }

    public override void Store(IStorageSession session, TDoc document, Guid? version)
    {
        if (version.HasValue)
        {
            session.Versions.StoreVersion<TDoc, TId>(Identity(document), version.Value);
        }

        Store(session, document);
    }

    /// <inheritdoc cref="FisherDocumentStorage{TDoc,TId}.UseNumericRevisions" />
    public override void Store(IStorageSession session, TDoc document, long revision)
        => throw new NotSupportedException(
            "Fisher does not implement numeric document revisions. Use optimistic concurrency instead.");
}

// ---- ConcurrencyMode.Off ----

internal sealed class UnversionedLightweightFisherStorage<TDoc, TId> : LightweightFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public UnversionedLightweightFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert);

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatUnversionedClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal sealed class UnversionedIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public UnversionedIdentityMapFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert);

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new UnversionedClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new UnversionedClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatUnversionedClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
}

// ---- ConcurrencyMode.Numeric ----
//
// Mirrors the Optimistic pair below/above, with two differences that are the whole of the mode: the
// tracker is RevisionsFor rather than ForType, and the operations carry a long revision instead of a
// Guid version. The *Projected variants pass a null tracker for the same reason the Optimistic ones
// do — a projection rebuild writes what the events say and has no prior read to guard against.

internal sealed class NumericLightweightFisherStorage<TDoc, TId> : LightweightFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public NumericLightweightFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    private static Dictionary<TId, long> Revisions(IStorageSession session)
        => session.Versions.RevisionsFor<TDoc, TId>();

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, Revisions(session));

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new NumericClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new NumericClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, null);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new NumericClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new NumericClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatNumericClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal sealed class NumericIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public NumericIdentityMapFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    private static Dictionary<TId, long> Revisions(IStorageSession session)
        => session.Versions.RevisionsFor<TDoc, TId>();

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, Revisions(session));

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new NumericClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Revisions(session));

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new NumericClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new NumericClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, null);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new NumericClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new NumericClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatNumericClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
}

// ---- ConcurrencyMode.Optimistic ----
//
// The *Projected variants pass a null version tracker on purpose: a projection rebuild writes what
// the events say, and has no prior read to guard against.

internal sealed class OptimisticLightweightFisherStorage<TDoc, TId> : LightweightFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public OptimisticLightweightFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    private static Dictionary<TId, Guid> Versions(IStorageSession session) => session.Versions.ForType<TDoc, TId>();

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, Versions(session));

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, null);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatOptimisticClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal sealed class OptimisticIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public OptimisticIdentityMapFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    private static Dictionary<TId, Guid> Versions(IStorageSession session) => session.Versions.ForType<TDoc, TId>();

    public override Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, Versions(session));

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => new OptimisticClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            Versions(session));

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeOverwriteOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeUpsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            OperationRole.Upsert, null);

    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeInsertOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => new OptimisticClosedShapeUpdateOperation<TDoc, TId>(document, Identity(document), tenantId, _descriptor,
            null);

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatOptimisticClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
}

// ---- query only ----

/// <summary>
///     The read-only flavor. Its SELECT omits the id column and narrows the metadata set, which is a
///     contract with the query-only selectors rather than an optimization.
/// </summary>
internal sealed class QueryOnlyFisherStorage<TDoc, TId> : FisherDocumentStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public QueryOnlyFisherStorage(DocumentMapping mapping, DocumentStorageDescriptor<TDoc, TId> descriptor)
        : base(mapping, descriptor)
    {
    }

    protected override bool IncludeIdInSelect => false;

    protected override IDocumentMetadataBinder<TDoc>[] ReadBinders() => _descriptor.QueryOnlyReadBinders;

    public override ISelector BuildSelector(IStorageSession session)
        => new FlatClosedShapeQueryOnlySelector<TDoc, TId>(session, _descriptor);

    public override Task<TDoc?> LoadAsync(TId id, IStorageSession session, CancellationToken token)
        => QueryOneAsync(id, session, token);

    public override Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
        => QueryManyAsync(ids, session, token);

    private static NotSupportedException CannotWrite()
        => new("This is a read-only document session; it cannot store or delete documents.");

    public override void Store(IStorageSession session, TDoc document) => throw CannotWrite();
    public override void Store(IStorageSession session, TDoc document, Guid? version) => throw CannotWrite();
    public override void Store(IStorageSession session, TDoc document, long revision) => throw CannotWrite();

    public override Weasel.Storage.IStorageOperation Insert(TDoc d, IStorageSession s, string t) => throw CannotWrite();
    public override Weasel.Storage.IStorageOperation Update(TDoc d, IStorageSession s, string t) => throw CannotWrite();
    public override Weasel.Storage.IStorageOperation Upsert(TDoc d, IStorageSession s, string t) => throw CannotWrite();

    public override Weasel.Storage.IStorageOperation Overwrite(TDoc d, IStorageSession s, string t)
        => throw CannotWrite();

    public override Weasel.Storage.IStorageOperation OverwriteProjected(TDoc d, string t) => throw CannotWrite();
    public override Weasel.Storage.IStorageOperation UpsertProjected(TDoc d, string t) => throw CannotWrite();
    public override Weasel.Storage.IStorageOperation InsertProjected(TDoc d, string t) => throw CannotWrite();
    public override Weasel.Storage.IStorageOperation UpdateProjected(TDoc d, string t) => throw CannotWrite();
}

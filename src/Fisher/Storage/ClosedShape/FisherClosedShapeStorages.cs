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

    /// <remarks>
    ///     The already-mapped ids are answered from memory and left out of the statement, rather than
    ///     read back and discarded. Reference identity would hold either way — the identity-map
    ///     selector returns the cached instance for a row it has already seen — so what this saves is
    ///     the read, which is the other half of what the map is for.
    /// </remarks>
    public override async Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
    {
        var map = MapFor(session);
        var results = new List<TDoc>();
        var missing = new List<TId>();

        foreach (var id in ids)
        {
            if (map.TryGetValue(id, out var cached))
            {
                results.Add(cached);
            }
            else
            {
                missing.Add(id);
            }
        }

        // The selector maps whatever it materialises, so nothing has to be mapped again here.
        results.AddRange(await QueryManyAsync(missing.ToArray(), session, token).ConfigureAwait(false));

        return results;
    }

    /// <remarks>
    ///     <b>A second instance under an id the map already holds is refused</b>, as Marten refuses it.
    ///     That is the whole safety property an identity map buys: two instances of one document in one
    ///     session, both stored, is a last-write-wins outcome indistinguishable from a lost update. A
    ///     type that declares <see cref="IEquatable{T}" /> is taken at its word, since it has said what
    ///     "the same document" means.
    /// </remarks>
    public override void Store(IStorageSession session, TDoc document)
    {
        var id = _descriptor.Identification.AssignIfMissing(document, session.Database);
        var map = MapFor(session);

        if (map.TryGetValue(id, out var existing) && !ReferenceEquals(existing, document)
            && document is not IEquatable<TDoc>)
        {
            throw new InvalidOperationException(
                $"A different instance of '{typeof(TDoc).FullName}' with identity '{id}' is already in "
                + "this session's identity map. Storing both would write one over the other, which is a "
                + "lost update wearing a last-write-wins hat. Store the instance the session handed you, "
                + $"call Eject to drop the mapped one first, or make {typeof(TDoc).Name} IEquatable<"
                + $"{typeof(TDoc).Name}> to say what 'the same document' means. A LightweightSession "
                + "keeps no map and does not check.");
        }

        // Mapped before the write runs, so a load later in the same session returns what the caller
        // stored rather than what the database still holds.
        map[id] = document;

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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalUnversionedClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor)
            : new FlatUnversionedClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal class UnversionedIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalUnversionedClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor)
            : new FlatUnversionedClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalNumericClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor)
            : new FlatNumericClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal class NumericIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalNumericClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor)
            : new FlatNumericClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalOptimisticClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor)
            : new FlatOptimisticClosedShapeLightweightSelector<TDoc, TId>(session, _descriptor);
}

internal class OptimisticIdentityMapFisherStorage<TDoc, TId> : IdentityMapFisherStorage<TDoc, TId>
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalOptimisticClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor)
            : new FlatOptimisticClosedShapeIdentityMapSelector<TDoc, TId>(session, _descriptor);
}

// ---- dirty tracking ----
//
// Each of these is its concurrency mode's identity-map storage with one member replaced: the
// selector. That is the entire difference between the two flavors, and it is a difference in what
// happens when a row is *read* — Weasel's dirty-tracking selectors do everything the identity-map
// ones do and additionally register a ChangeTracker<T> per materialised document. Nothing about
// storing, loading by id, or writing changes, so nothing else is overridden. Marten's own
// DirtyCheckedDocumentStorage is the same one-line subclass of its identity-map storage.
//
// Detection and reset are the session's job, not the storage's: FisherSession.SaveChangesAsync asks
// every tracker for its operation before taking the batch, and re-baselines them after the write.

internal sealed class UnversionedDirtyTrackingFisherStorage<TDoc, TId>
    : UnversionedIdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public UnversionedDirtyTrackingFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    public override ISelector BuildSelector(IStorageSession session)
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalUnversionedClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor)
            : new FlatUnversionedClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor);
}

internal sealed class NumericDirtyTrackingFisherStorage<TDoc, TId> : NumericIdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public NumericDirtyTrackingFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    public override ISelector BuildSelector(IStorageSession session)
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalNumericClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor)
            : new FlatNumericClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor);
}

internal sealed class OptimisticDirtyTrackingFisherStorage<TDoc, TId>
    : OptimisticIdentityMapFisherStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    public OptimisticDirtyTrackingFisherStorage(DocumentMapping mapping,
        DocumentStorageDescriptor<TDoc, TId> descriptor) : base(mapping, descriptor)
    {
    }

    public override ISelector BuildSelector(IStorageSession session)
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalOptimisticClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor)
            : new FlatOptimisticClosedShapeDirtyTrackingSelector<TDoc, TId>(session, _descriptor);
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
        => _descriptor.ResolveDocumentType is not null
            ? new HierarchicalClosedShapeQueryOnlySelector<TDoc, TId>(session, _descriptor)
            : new FlatClosedShapeQueryOnlySelector<TDoc, TId>(session, _descriptor);

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

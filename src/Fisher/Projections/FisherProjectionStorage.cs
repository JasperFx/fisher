using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using Fisher.Storage.ClosedShape;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Weasel.Storage;

namespace Fisher.Projections;

/// <summary>
///     Where a projection writes its snapshot: an <see cref="IProjectionStorage{TDoc,TId}" /> over the
///     document storage for <typeparamref name="TDoc" />.
/// </summary>
/// <remarks>
///     Every write queues an operation onto the session rather than executing one, so an inline
///     projection's snapshot lands in the same transaction as the events that produced it. That is the
///     whole point of applying projections inline, and it is why this takes a session rather than a
///     database.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: persists the projected document through the configured serializer. TDoc/TId flow in from projection registration on the caller side and are preserved per the AOT publishing guide.")]
internal class FisherProjectionStorage<TDoc, TId> : IProjectionStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    private readonly FisherSession _session;
    private readonly IDocumentStorage<TDoc, TId> _storage;

    public FisherProjectionStorage(FisherSession session, IDocumentStorage<TDoc, TId> storage, string tenantId)
    {
        _session = session;
        _storage = storage;
        TenantId = tenantId;
    }

    public string TenantId { get; }

    public void SetIdentity(TDoc document, TId identity) => _storage.SetIdentity(document, identity);

    public TId Identity(TDoc document) => (TId)_storage.IdentityFor(document);

    public void Store(TDoc snapshot) => Store(snapshot, (TId)_storage.IdentityFor(snapshot), TenantId);

    public void Store(TDoc snapshot, TId id, string tenantId)
        => _session.QueueOperation(_storage.UpsertProjected(snapshot, tenantId));

    /// <summary>
    ///     Applying a projection is storing its snapshot. The <paramref name="lastEvent" /> and
    ///     <paramref name="scope" /> exist for stores that stamp projection metadata onto the document;
    ///     Fisher has no such columns yet.
    /// </summary>
    public void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope) => Store(aggregate);

    public void Delete(TId identity) => Delete(identity, TenantId);

    public void Delete(TId identity, string tenantId)
        => _session.QueueOperation(_storage.DeleteForId(identity, tenantId));

    public void HardDelete(TDoc snapshot) => HardDelete(snapshot, TenantId);

    public void HardDelete(TDoc snapshot, string tenantId)
        => _session.QueueOperation(_storage.HardDeleteForDocument(snapshot, tenantId));

    public void UnDelete(TDoc snapshot) => UnDelete(snapshot, TenantId);

    /// <summary>
    ///     Bring a soft-deleted snapshot back, and no-op for a snapshot type whose delete removes the
    ///     row outright — where the projection would have to re-create it, which is what a subsequent
    ///     <c>Store</c> does.
    /// </summary>
    /// <remarks>
    ///     A no-op rather than a throw because this is reached through shared aggregation code that a
    ///     <c>ShouldDelete</c> convention can trigger, so a projection written against a store where
    ///     every document is soft-deleted must not fail here for saying so.
    /// </remarks>
    public void UnDelete(TDoc snapshot, string tenantId)
    {
        if (_storage is not FisherDocumentStorage<TDoc, TId> fisher)
        {
            return;
        }

        var operation = fisher.UndeleteForId((TId)_storage.IdentityFor(snapshot), tenantId);

        if (operation is not null)
        {
            _session.QueueOperation(operation);
        }
    }

    /// <summary>
    ///     Archiving a stream leaves its snapshot alone — Fisher archives events, and a projection that
    ///     wants its document removed says so with a <c>ShouldDelete</c>.
    /// </summary>
    public void ArchiveStream(TId sliceId, string tenantId)
    {
    }

    public async Task<TDoc> LoadAsync(TId id, CancellationToken cancellation)
        => (await _storage.LoadAsync(id, _session, cancellation).ConfigureAwait(false))!;

    public async Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities,
        CancellationToken cancellationToken)
    {
        var documents = await _storage.LoadManyAsync(identities, _session, cancellationToken)
            .ConfigureAwait(false);

        Fisher.Diagnostics.DaemonTrace.Record("slice.loadmany",
            $"{typeof(TDoc).Name} asked=[{string.Join(",", identities)}] got={documents.Count}",
            identities.Length, documents.Count);

        return documents.ToDictionary(x => (TId)_storage.IdentityFor(x));
    }
}

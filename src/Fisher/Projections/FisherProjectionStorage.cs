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
    ///     Archive the stream a single stream projection has just seen an <see cref="Archived" /> event
    ///     on — a stream-level operation, queued onto the same unit of work as the snapshot.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>This was an empty method until fisher#184, and the comment on it was reasoning
    ///         about the wrong question.</b> It said archiving leaves the snapshot alone and a
    ///         projection wanting its document removed says so with a <c>ShouldDelete</c> — both true,
    ///         and neither of them what the seam asks for. JasperFx's
    ///         <c>JasperFxSingleStreamProjectionBase.maybeArchiveStream</c> calls this when the slice
    ///         carries an <see cref="Archived" /> event, and what it means is <em>archive the stream</em>
    ///         — the same thing <c>session.Events.ArchiveStream(id)</c> does. Marten and Polecat both
    ///         queue their archive operation here.
    ///     </para>
    ///     <para>
    ///         So capturing <c>Archived</c> through a snapshot did nothing at all on Fisher: the
    ///         projection ran, the document was written, and <c>fi_streams.is_archived</c> stayed false.
    ///         Silent in both directions — no exception anywhere, and the aggregate looks right.
    ///         <c>StreamArchivingCompliance</c>'s three archived-event facts are what found it; Fisher's
    ///         own archiving tests all archive through the direct operation, which is the half that
    ///         always worked.
    ///     </para>
    ///     <para>
    ///         The slice id is the aggregate's identity, which for a single stream projection is the
    ///         stream's — through a strong-typed wrapper where the aggregate declares one, so the inner
    ///         value is what reaches the operation. A <see cref="Archived" /> event on an aggregate whose
    ///         identity is neither the stream identity nor a wrapper around it names no stream to
    ///         archive, and is left alone rather than guessed at.
    ///     </para>
    /// </remarks>
    public void ArchiveStream(TId sliceId, string tenantId)
    {
        if (StreamIdentityFor(sliceId) is not { } streamIdentity)
        {
            return;
        }

        _session.QueueOperation(_session.EventGraph.ArchiveStreamOperation(streamIdentity, tenantId, true));
    }

    /// <inheritdoc cref="ArchiveStream" />
    private object? StreamIdentityFor(TId sliceId)
    {
        var expected = _session.EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? typeof(Guid)
            : typeof(string);

        if (sliceId.GetType() == expected)
        {
            return sliceId;
        }

        return Fisher.Storage.StrongTypedId.TryResolve(typeof(TId), out var info)
               && info.SimpleType == expected
            ? info.ValueProperty.GetValue(sliceId)
            : null;
    }

    public async Task<TDoc> LoadAsync(TId id, CancellationToken cancellation)
        => (await _storage.LoadAsync(id, _session, cancellation).ConfigureAwait(false))!;

    public async Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities,
        CancellationToken cancellationToken)
    {
        var documents = await _storage.LoadManyAsync(identities, _session, cancellationToken)
            .ConfigureAwait(false);

        // Guarded here as well as inside Record: the interpolated detail and its string.Join are
        // evaluated at the call site, so without the guard this allocates on every load-many with
        // tracing off — the exact cost DaemonTrace's recording path promises not to have.
        if (Fisher.Diagnostics.DaemonTrace.Enabled)
        {
            Fisher.Diagnostics.DaemonTrace.Record("slice.loadmany",
                $"{typeof(TDoc).Name} asked=[{string.Join(",", identities)}] got={documents.Count}",
                identities.Length, documents.Count);
        }

        return documents.ToDictionary(x => (TId)_storage.IdentityFor(x));
    }
}

using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using Fisher.Storage.ClosedShape;
using JasperFx;
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

    /// <remarks>
    ///     The inline path — JasperFx's <c>ApplyInline</c> — comes through here rather than through
    ///     <see cref="StoreProjection" />, with no last event to hand. So the revision is read off the
    ///     snapshot, where the aggregation has already stamped the stream version (or, for a multi-stream
    ///     projection, the sequence) onto <see cref="IRevisioned.Version" />. See
    ///     <see cref="StoreProjection" /> for why it matters (fisher#369).
    /// </remarks>
    public void Store(TDoc snapshot, TId id, string tenantId)
        => _session.QueueOperation(ProjectedWrites.For(_storage, snapshot, tenantId, ProjectedWrites.RevisionCarriedBy(snapshot)));

    /// <summary>
    ///     Applying a projection is storing its snapshot — and, for a numeric-revisioned document, stamping
    ///     the revision the events say it is at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>fisher#369: a projected <c>IRevisioned</c> document's <c>revision</c> column used to
    ///         be a write count.</b> This was <c>Store(aggregate)</c>, whose projected upsert binds
    ///         revision 0 — "auto", increment whatever is stored. So after one save of two events the
    ///         column said 1, <c>LoadAsync</c> projected that 1 back onto <c>Version</c>, and the stored
    ///         body, a LINQ <c>Select(x =&gt; x.Version)</c>, Marten and Polecat all said 2. One store
    ///         giving two answers depending on how the document was read, which is exactly what a
    ///         consumer gating a reload on <c>(Id, Version)</c> cannot live with.
    ///     </para>
    ///     <para>
    ///         Marten's answer, taken whole: an <b>overwrite</b> carrying an explicit revision — the last
    ///         event's stream version for a single-stream projection, its global sequence for a
    ///         multi-stream one, which is the same rule JasperFx's <c>AggregateVersioning</c> uses to set
    ///         <c>Version</c> on the aggregate itself — with <c>IgnoreConcurrencyViolation</c>. An overwrite
    ///         because a projection writes what the events say and has no prior read to guard: an upsert
    ///         would demand the revision strictly exceed the stored one, and a replay onto a row the
    ///         previous run left would then fail on the value it is supposed to write.
    ///     </para>
    ///     <para>
    ///         Only for numeric revisions. A Guid-versioned or unversioned document has no revision column
    ///         to disagree with its body, so its write is unchanged.
    ///     </para>
    /// </remarks>
    public void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope)
        => _session.QueueOperation(ProjectedWrites.For(_storage, aggregate, TenantId, lastEvent is null
            ? ProjectedWrites.RevisionCarriedBy(aggregate)
            : scope == AggregationScope.SingleStream ? lastEvent.Version : lastEvent.Sequence));

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

/// <summary>
///     How a projected document is written — shared by the projection storage and by
///     <c>Advanced.RebuildSingleStreamAsync</c>, which is a projection write reached another way.
/// </summary>
internal static class ProjectedWrites
{
    /// <summary>The revision a snapshot carries on its own <see cref="IRevisioned.Version" />, or 0.</summary>
    internal static long RevisionCarriedBy(object snapshot) => snapshot is IRevisioned revisioned ? revisioned.Version : 0;

    /// <summary>
    ///     The projected write: an upsert, or — for a numeric-revisioned document that knows its revision —
    ///     an overwrite stamping it.
    /// </summary>
    /// <remarks>
    ///     A revision of 0 is "auto", which is also what a document configured through
    ///     <c>UseNumericRevisions()</c> without an <see cref="IRevisioned" /> member carries. Such a
    ///     document has no <c>Version</c> in its body or on a member, so there is nothing for the column to
    ///     disagree with and the upsert's write count is left alone.
    /// </remarks>
    internal static Weasel.Storage.IStorageOperation For<TDoc>(IDocumentStorage<TDoc> storage, TDoc snapshot,
        string tenantId, long revision) where TDoc : notnull
    {
        if (!storage.UseNumericRevisions || revision <= 0)
        {
            return storage.UpsertProjected(snapshot, tenantId);
        }

        var operation = storage.OverwriteProjected(snapshot, tenantId);

        if (operation is IRevisionedOperation revisioned)
        {
            revisioned.Revision = revision;
            revisioned.IgnoreConcurrencyViolation = true;
        }

        return operation;
    }
}

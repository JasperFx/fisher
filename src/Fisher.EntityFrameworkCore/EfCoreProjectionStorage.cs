using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore;

/// <summary>
///     A projection's documents as EF Core entities rather than as rows in a Fisher document table
///     (fisher#50).
/// </summary>
/// <remarks>
///     <para>
///         Registered through <c>ProjectToEfCore</c>, which is the only thing a projection needs: an
///         ordinary <c>SingleStreamProjection&lt;TDoc, TId&gt;</c> or
///         <c>MultiStreamProjection&lt;TDoc, TId&gt;</c> with conventional <c>Apply</c> methods writes
///         into EF without knowing it does. That is the divergence from Polecat, whose EF path is
///         reachable only by deriving from one of its <c>EfCore*Projection</c> base classes.
///     </para>
///     <para>
///         <b>Every mutation goes into EF's change tracker and nothing is written until the batch
///         commits</b>, which is the same discipline <c>FisherProjectionStorage</c> follows by queueing
///         storage operations onto the session. On SQLite it is not merely tidy: writing as we went
///         would mean a second connection writing while the batch holds the file's one write lock,
///         which blocks from inside the transaction it is waiting on.
///     </para>
///     <para>
///         The <see cref="SemaphoreSlim" /> is fisher#13's lesson applied one layer over. A
///         <c>DbContext</c> is explicitly not thread-safe, and JasperFx's <c>ExecutionStage</c> fans
///         its slices out with <c>Task.WhenAll</c> onto the same storage — the shape that silently lost
///         a slice's write when the session's operation queue was an unguarded <c>List&lt;T&gt;</c>.
///     </para>
/// </remarks>
internal sealed class EfCoreProjectionStorage<TDoc, TId, TContext> : IProjectionStorage<TDoc, TId>
    where TDoc : class
    where TId : notnull
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal EfCoreProjectionStorage(TContext context, string tenantId)
    {
        _context = context;
        TenantId = tenantId;
    }

    /// <summary>
    ///     The context this storage writes through, for a projection that wants it while applying.
    /// </summary>
    internal TContext Context => _context;

    public string TenantId { get; }

    public void SetIdentity(TDoc document, TId identity) => Locked(() => SetIdentityUnsafe(document, identity));

    public TId Identity(TDoc document) => Locked(() => IdentityUnsafe(document));

    public void Store(TDoc snapshot) => Locked(() => AddOrUpdateUnsafe(snapshot));

    public void Store(TDoc snapshot, TId id, string tenantId) => Locked(() =>
    {
        SetIdentityUnsafe(snapshot, id);
        AddOrUpdateUnsafe(snapshot);
    });

    public void StoreProjection(TDoc aggregate, IEvent? lastEvent, AggregationScope scope)
        => Locked(() => AddOrUpdateUnsafe(aggregate));

    public void Delete(TId identity) => Locked(() =>
    {
        var existing = _context.Find<TDoc>(identity);

        if (existing is not null)
        {
            _context.Remove(existing);
        }
    });

    public void Delete(TId identity, string tenantId) => Delete(identity);

    public void HardDelete(TDoc snapshot) => Locked(() => _context.Remove(snapshot));

    public void HardDelete(TDoc snapshot, string tenantId) => HardDelete(snapshot);

    /// <summary>
    ///     No-op: an EF entity has no soft-delete column for Fisher to flip back.
    /// </summary>
    /// <remarks>
    ///     A no-op rather than a throw for the reason <c>FisherProjectionStorage</c> gives: this is
    ///     reached through shared aggregation code that a <c>ShouldDelete</c> convention can trigger, so
    ///     a projection written against a store where every document is soft-deleted must not fail here
    ///     merely for saying so. A projection that wants the entity back stores it again.
    /// </remarks>
    public void UnDelete(TDoc snapshot)
    {
    }

    public void UnDelete(TDoc snapshot, string tenantId)
    {
    }

    /// <summary>
    ///     Archiving a stream leaves its entity alone, matching Fisher's own projection storage: Fisher
    ///     archives events, and a projection wanting its row removed says so with a <c>ShouldDelete</c>.
    /// </summary>
    public void ArchiveStream(TId sliceId, string tenantId)
    {
    }

    public async Task<TDoc> LoadAsync(TId id, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            return (await _context.FindAsync<TDoc>([id], cancellation).ConfigureAwait(false))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <remarks>
    ///     <c>FindAsync</c> per id rather than one <c>Where(x => ids.Contains(x.Id))</c>, because Find
    ///     answers from the change tracker before it goes to the database — so a slice whose entity this
    ///     batch has already loaded or created costs no query, and more importantly comes back as the
    ///     <em>same instance</em> the earlier slice mutated. A query would materialise a second one and
    ///     the two would race to overwrite each other at commit.
    /// </remarks>
    public async Task<IReadOnlyDictionary<TId, TDoc>> LoadManyAsync(TId[] identities,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var found = new Dictionary<TId, TDoc>();

            foreach (var id in identities)
            {
                var document = await _context.FindAsync<TDoc>([id], cancellationToken).ConfigureAwait(false);

                if (document is not null)
                {
                    found[id] = document;
                }
            }

            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Locked(Action action)
    {
        _gate.Wait();

        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Locked<T>(Func<T> func)
    {
        _gate.Wait();

        try
        {
            return func();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetIdentityUnsafe(TDoc document, TId identity)
    {
        var property = PrimaryKeyPropertyName(document);

        _context.Entry(document).Property(property).CurrentValue = identity;
    }

    private TId IdentityUnsafe(TDoc document)
        => (TId)_context.Entry(document).Property(PrimaryKeyPropertyName(document)).CurrentValue!;

    /// <remarks>
    ///     Named rather than assumed to be called <c>Id</c>, and refused rather than guessed when the
    ///     entity has no key or a composite one — a projection keyed on something EF does not consider
    ///     the identity would write rows that no later slice could ever find again.
    /// </remarks>
    private string PrimaryKeyPropertyName(TDoc document)
    {
        var key = _context.Entry(document).Metadata.FindPrimaryKey();

        if (key is null || key.Properties.Count != 1)
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}' needs a single-property primary key to be projected into by "
                + $"Fisher, and EF Core reports {(key is null ? "none" : $"{key.Properties.Count}")}. A "
                + "projection's identity is one value, so there is nothing to map a composite key onto.");
        }

        return key.Properties[0].Name;
    }

    /// <remarks>
    ///     A projection hands over whatever its <c>Apply</c> methods produced, which may be an instance
    ///     EF has never seen — a <c>Create</c> method's return value, or a record rebuilt by a
    ///     <c>with</c> expression. Detached means "look for the row this replaces", and copying onto the
    ///     tracked instance rather than attaching the new one is what keeps a second slice's reference
    ///     to the same entity valid.
    /// </remarks>
    private void AddOrUpdateUnsafe(TDoc entity)
    {
        var entry = _context.Entry(entity);

        switch (entry.State)
        {
            case EntityState.Detached:
                var identity = entry.Property(PrimaryKeyPropertyName(entity)).CurrentValue;
                var existing = identity is null ? null : _context.Find<TDoc>(identity);

                if (existing is not null && !ReferenceEquals(existing, entity))
                {
                    _context.Entry(existing).CurrentValues.SetValues(entity);
                }
                else
                {
                    _context.Add(entity);
                }

                break;

            case EntityState.Deleted:
                // A slice that deleted and then re-stored means the row should be there.
                entry.State = EntityState.Modified;
                break;

            case EntityState.Unchanged:
                entry.State = EntityState.Modified;
                break;

            // Added and Modified are already what they need to be.
        }
    }
}

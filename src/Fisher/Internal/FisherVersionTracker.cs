using Weasel.Storage;

namespace Fisher.Internal;

/// <summary>
///     Session-scoped document version/revision bookkeeping behind the shared
///     <see cref="IVersionTracker" /> seam of the closed-shape storage runtime. Ported from Polecat —
///     the bookkeeping is pure in-memory dictionaries with nothing dialect-specific in it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Guarded, which is the one way it diverges from Polecat's, and the reason is Fisher's
///         daemon rather than a difference of taste.</b> Polecat hands each concurrent projection
///         slice its own session and therefore its own tracker; JasperFx's <c>ExecutionStage</c> fans
///         its executions out with <c>Task.WhenAll</c> onto the <em>same</em> Fisher session, so the
///         slices share one of these — the fisher#13 shape, and the same place marten#4657 arrived
///         at from the other direction.
///     </para>
///     <para>
///         <b>The two outer dictionaries are what actually race.</b> <see cref="ForType{TDoc,TId}" />
///         and <see cref="RevisionsFor{TDoc,TId}" /> are called by the numeric and optimistic storages
///         while <em>constructing</em> an upsert — on the calling thread — so a composite projection
///         whose concurrent slices write different snapshot types has two threads adding to a
///         <c>Dictionary&lt;Type, object&gt;</c> at once. That loses an entry or spins inside a
///         resize, and the loss is silent: the operations built first keep the dictionary they were
///         handed and postprocess into an orphan nothing will read.
///     </para>
///     <para>
///         <b>The inner dictionaries are returned unguarded, deliberately.</b> They are handed to a
///         storage operation, which writes into one during postprocessing — and postprocessing happens
///         inside <c>FisherSession.ExecuteBatchAsync</c>, which is strictly sequential because SQLite
///         permits one writer per file. What the lock has to cover is every mutation reachable from a
///         <em>caller's</em> thread, which is what the members below do.
///     </para>
/// </remarks>
internal class FisherVersionTracker : IVersionTracker
{
    private readonly Dictionary<Type, object> _versions = new();
    private readonly Dictionary<Type, object> _revisions = new();
    private readonly System.Threading.Lock _lock = new();

    public Dictionary<TId, Guid> ForType<TDoc, TId>() where TId : notnull
    {
        lock (_lock)
        {
            if (_versions.TryGetValue(typeof(TDoc), out var raw) && raw is Dictionary<TId, Guid> existing)
            {
                return existing;
            }

            var fresh = new Dictionary<TId, Guid>();
            _versions[typeof(TDoc)] = fresh;
            return fresh;
        }
    }

    public Dictionary<TId, long> RevisionsFor<TDoc, TId>() where TId : notnull
    {
        lock (_lock)
        {
            if (_revisions.TryGetValue(typeof(TDoc), out var raw) && raw is Dictionary<TId, long> existing)
            {
                return existing;
            }

            var fresh = new Dictionary<TId, long>();
            _revisions[typeof(TDoc)] = fresh;
            return fresh;
        }
    }

    public Guid? VersionFor<TDoc, TId>(TId id) where TId : notnull
    {
        var versions = ForType<TDoc, TId>();

        lock (_lock)
        {
            return versions.TryGetValue(id, out var version) ? version : null;
        }
    }

    public long? RevisionFor<TDoc, TId>(TId id) where TId : notnull
    {
        var revisions = RevisionsFor<TDoc, TId>();

        lock (_lock)
        {
            return revisions.TryGetValue(id, out var revision) ? revision : null;
        }
    }

    public void StoreVersion<TDoc, TId>(TId id, Guid guid) where TId : notnull
    {
        var versions = ForType<TDoc, TId>();

        lock (_lock)
        {
            versions[id] = guid;
        }
    }

    public void StoreRevision<TDoc, TId>(TId id, long revision) where TId : notnull
    {
        var revisions = RevisionsFor<TDoc, TId>();

        lock (_lock)
        {
            revisions[id] = revision;
        }
    }

    public void ClearVersion<TDoc, TId>(TId id) where TId : notnull
    {
        var versions = ForType<TDoc, TId>();

        lock (_lock)
        {
            versions.Remove(id);
        }
    }

    public void ClearRevision<TDoc, TId>(TId id) where TId : notnull
    {
        var revisions = RevisionsFor<TDoc, TId>();

        lock (_lock)
        {
            revisions.Remove(id);
        }
    }
}

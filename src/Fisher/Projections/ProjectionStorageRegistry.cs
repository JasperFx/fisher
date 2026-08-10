using JasperFx.Events.Daemon;

namespace Fisher.Projections;

/// <summary>
///     Where a projection's documents live, when that is somewhere other than a Fisher document table
///     (fisher#50).
/// </summary>
/// <remarks>
///     <para>
///         A projection normally writes through <c>FisherProjectionStorage</c>, which queues document
///         operations onto the session so the snapshot commits with the events that produced it.
///         Registering a provider here replaces that for one document type — the shape
///         <c>Fisher.EntityFrameworkCore</c> uses to put a projection's aggregates in EF Core entities
///         instead.
///     </para>
///     <para>
///         <b>The registry is the whole seam, and that is deliberate.</b> Nothing above it changes: an
///         ordinary <c>SingleStreamProjection&lt;TDoc, TId&gt;</c> with conventional <c>Apply</c>
///         methods writes into whatever storage is registered for <c>TDoc</c>, so a projection does not
///         have to be written against a particular backing store to use one. Polecat instead requires a
///         projection to derive from an <c>EfCore*Projection</c> base class to reach EF at all; Fisher's
///         base classes exist only for projections that want the <c>DbContext</c> <em>while</em>
///         applying an event, which is a smaller and more honest claim.
///     </para>
///     <para>
///         <b>Whatever a provider returns still has to commit in Fisher's transaction.</b> On SQLite
///         that is not an optimisation — one writer per file means a provider writing on its own
///         connection is a second writer contending with the batch that is already holding the lock, so
///         it would block against Fisher from inside Fisher's own transaction.
///         <see cref="ITransactionParticipant" /> is how a provider joins instead, and the provider is
///         handed the session precisely so it can register one.
///     </para>
/// </remarks>
public sealed class ProjectionStorageRegistry
{
    private readonly Dictionary<Type, Registration> _providers = [];
    private readonly Func<Type, bool> _hasMapping;

    internal ProjectionStorageRegistry(Func<Type, bool> hasMapping) => _hasMapping = hasMapping;

    private sealed record Registration(Func<IDocumentSession, string, object> Provider, string TableName);

    /// <summary>
    ///     Store <typeparamref name="TDoc" /> through <paramref name="provider" /> rather than in a
    ///     Fisher document table.
    /// </summary>
    /// <param name="tableName">
    ///     The physical table the provider writes into, so a rebuild can clear it — see
    ///     <c>DocumentStore.TeardownExistingProjectionStateAsync</c>.
    /// </param>
    /// <param name="provider">
    ///     Given the batch's session and the tenant id, an <see cref="IProjectionStorage{TDoc,TId}" />.
    /// </param>
    /// <remarks>
    ///     <b>Register before the projection that produces <typeparamref name="TDoc" />.</b> Registering
    ///     a projection maps its document type, which is what puts a Fisher table for it in the
    ///     migration; once that has happened this can only add a second home for the same type. So the
    ///     ordering is checked rather than documented, the same way
    ///     <c>SeedInitialDataOnStartup</c> refuses to be registered before
    ///     <c>ApplyAllDatabaseChangesOnStartup</c> (fisher#39) — both are "this line has to come first"
    ///     and both fail confusingly when it does not.
    /// </remarks>
    public void Register<TDoc, TId>(string tableName,
        Func<IDocumentSession, string, IProjectionStorage<TDoc, TId>> provider)
        where TDoc : notnull where TId : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(provider);

        if (_hasMapping(typeof(TDoc)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TDoc).Name}' is already mapped as a Fisher document, so registering external "
                + "projection storage for it now would leave it with two homes — a Fisher table the "
                + "migration creates and nothing writes to, and the one registered here. Register the "
                + "storage before the projection that produces it.");
        }

        _providers[typeof(TDoc)] = new Registration((session, tenantId) => provider(session, tenantId),
            tableName);
    }

    /// <summary>
    ///     Whether <paramref name="documentType" /> is stored somewhere other than a Fisher document
    ///     table.
    /// </summary>
    internal bool HasProviderFor(Type documentType) => _providers.ContainsKey(documentType);

    /// <summary>
    ///     The table a registered provider writes <paramref name="documentType" /> into, or null when
    ///     Fisher's own document storage owns it.
    /// </summary>
    /// <remarks>
    ///     Read by rebuild teardown. Without it a rebuild replays onto the rows the previous run left —
    ///     the same gap <c>IPublishesTables</c> closes for a flat-table projection, and reached the same
    ///     way: the sweep that finds a projection's tables looks at <em>mapped</em> types, and a type
    ///     stored somewhere else is deliberately not one.
    /// </remarks>
    internal string? TableNameFor(Type documentType)
        => _providers.TryGetValue(documentType, out var registration) ? registration.TableName : null;

    /// <summary>
    ///     The registered storage for <typeparamref name="TDoc" />, or null when Fisher's own document
    ///     storage should serve it.
    /// </summary>
    internal IProjectionStorage<TDoc, TId>? TryResolve<TDoc, TId>(IDocumentSession session, string tenantId)
        where TDoc : notnull where TId : notnull
    {
        if (!_providers.TryGetValue(typeof(TDoc), out var registration))
        {
            return null;
        }

        // The cast rather than a typed dictionary: the registry is keyed by document type alone, because
        // that is what a projection's storage is looked up by. A provider registered with one identity
        // type and resolved with another is a configuration error, and an InvalidCastException naming
        // both types says so better than a silently missing provider would.
        return (IProjectionStorage<TDoc, TId>)registration.Provider(session, tenantId);
    }
}

namespace Fisher;

/// <summary>
///     How the container builds the scoped sessions <c>AddFisher</c> registers.
/// </summary>
/// <remarks>
///     The seam exists so an application can decide session policy once — a tenant id read off the
///     request, a different tracking mode — rather than at every injection site. Registering an
///     implementation of your own before or after <c>AddFisher</c> replaces the default, which is
///     what <c>TryAddSingleton</c> is for. Marten and Polecat both spell it this way.
/// </remarks>
public interface ISessionFactory
{
    /// <summary>A session that can read and write.</summary>
    IDocumentSession OpenSession();

    /// <summary>A read-only session.</summary>
    IQuerySession QuerySession();
}

/// <summary>
///     The default <see cref="ISessionFactory" />: lightweight sessions against the default tenant.
/// </summary>
internal sealed class DefaultSessionFactory : ISessionFactory
{
    private readonly IDocumentStore _store;

    public DefaultSessionFactory(IDocumentStore store) => _store = store;

    public IDocumentSession OpenSession() => _store.LightweightSession();

    /// <remarks>
    ///     Through the store's own <see cref="IDocumentStore.QuerySession(string)" />, so the decision
    ///     about what read-only means lives in one place rather than being made once here for the
    ///     container and again there for everybody else.
    /// </remarks>
    public IQuerySession QuerySession() => _store.QuerySession();
}

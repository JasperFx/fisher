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
    private readonly DocumentStore _store;

    public DefaultSessionFactory(DocumentStore store) => _store = store;

    public IDocumentSession OpenSession() => _store.LightweightSession();

    /// <remarks>
    ///     The same lightweight session, narrowed to the read interface. Fisher has no query-only
    ///     session type — <see cref="IQuerySession" /> is the read half of
    ///     <see cref="IDocumentSession" /> — so handing back a second session type would cost a
    ///     connection per scope to express a distinction the store does not make.
    /// </remarks>
    public IQuerySession QuerySession() => _store.LightweightSession();
}

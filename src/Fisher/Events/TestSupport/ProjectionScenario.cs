using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;

namespace Fisher.Events.TestSupport;

/// <summary>
///     Fisher's seam for JasperFx's projection scenario harness (fisher#42).
/// </summary>
/// <remarks>
///     Every scripting and execution behaviour lives on
///     <see cref="JasperFx.Events.TestSupport.ProjectionScenario{TOperations,TQuerySession}" />; this
///     type only closes it over Fisher's session pair, the same way <c>FisherProjectionDaemon</c>
///     closes the async daemon. Reached through
///     <see cref="AdvancedOperations.EventProjectionScenarioAsync" />.
/// </remarks>
public class ProjectionScenario : JasperFx.Events.TestSupport.ProjectionScenario<IDocumentSession, IQuerySession>
{
    private readonly DocumentStore _store;

    internal ProjectionScenario(DocumentStore store)
    {
        _store = store;
    }

    protected override bool HasAnyAsyncProjections => _store.Options.Projections.HasAnyAsyncProjections();

    /// <summary>
    ///     Wipe the event store, then exactly the document types the registered projections own.
    /// </summary>
    /// <remarks>
    ///     Not every table in the store: a scenario is entitled to seed documents its projections do
    ///     not produce, and clearing those would make the harness quietly destructive.
    /// </remarks>
    protected override async Task DeleteExistingDataAsync(CancellationToken ct)
    {
        await _store.Advanced.Clean.DeleteAllEventDataAsync(ct).ConfigureAwait(false);

        foreach (var storageType in _store.Options.Projections.All.SelectMany(x => x.Options.StorageTypes))
        {
            await _store.Advanced.Clean.CleanAsync(storageType, ct).ConfigureAwait(false);
        }
    }

    protected override async ValueTask<IProjectionDaemon> BuildDaemonAsync(string? tenantId)
        => await _store.BuildProjectionDaemonAsync(tenantId).ConfigureAwait(false);

    protected override IDocumentSession OpenSession(string? tenantId)
        => tenantId.IsNotEmpty() ? _store.LightweightSession(tenantId) : _store.LightweightSession();

    /// <remarks>No shared JasperFx interface declares <c>SaveChangesAsync</c>.</remarks>
    protected override Task SaveChangesAsync(IDocumentSession session, CancellationToken ct)
        => session.SaveChangesAsync(ct);

    protected override IEventOperations EventsFor(IDocumentSession session) => session.Events;

    /// <remarks>
    ///     The <c>object</c>-id dispatch is deliberately the same shape as
    ///     <c>FisherComplianceFixture</c>'s, so both seams are implemented the same way against the
    ///     same store.
    /// </remarks>
    protected override Task<T?> LoadDocumentAsync<T>(IQuerySession session, object id, CancellationToken ct)
        where T : class
        => id switch
        {
            Guid guidId => session.LoadAsync<T>(guidId, ct),
            int intId => session.LoadAsync<T>(intId, ct),
            long longId => session.LoadAsync<T>(longId, ct),
            string stringId => session.LoadAsync<T>(stringId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(id),
                $"Fisher cannot load documents by an identity of type {id.GetType().FullName}")
        };
}

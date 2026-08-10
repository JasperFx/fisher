using Fisher.Projections;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore;

/// <summary>
///     A per-event projection writing through EF Core, Fisher, or both, in one transaction
///     (fisher#50).
/// </summary>
/// <remarks>
///     <para>
///         The shape that needs a base class, where an aggregation projection does not. An aggregation
///         projection has storage to swap — register it with
///         <see cref="EfCoreProjectionExtensions.ProjectToEfCore{TDoc,TId,TContext}" /> and an ordinary
///         <c>SingleStreamProjection</c> writes into EF unchanged. A per-event projection has no such
///         indirection: it decides for itself what each event means, so reaching a
///         <c>DbContext</c> has to be something it is handed.
///     </para>
///     <para>
///         <b>One context per batch, enlisted once.</b> Both are the point: a context per event would
///         lose EF's change tracking between two events touching the same entity, and enlisting more
///         than once would save the same context repeatedly inside one transaction.
///     </para>
///     <para>
///         <b>Both halves commit together.</b> <paramref name="operations" /> is the batch's own
///         session, so a Fisher document stored through it and an EF entity added to the context land
///         in the same transaction as the progression row — which on SQLite is not a convenience but
///         the only way to write both at all, since two connections to one file are two writers.
///     </para>
/// </remarks>
public abstract class EfCoreEventProjection<TContext> : ProjectionBase, IProjection
    where TContext : DbContext
{
    private readonly Func<TContext> _contextFactory;

    /// <param name="contextFactory">
    ///     Builds a context for one batch. It owns its own connection and is moved onto Fisher's to
    ///     write — see
    ///     <see cref="DbContextTransactionParticipant{TContext}.MovingOntoFishersConnection" /> for why
    ///     that is forced rather than chosen.
    /// </param>
    protected EfCoreEventProjection(Func<TContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        Name = GetType().Name;
    }

    /// <summary>
    ///     Apply one event, to EF Core or to Fisher or to both.
    /// </summary>
    protected abstract Task ProjectAsync(IEvent @event, TContext context, IDocumentOperations operations,
        CancellationToken token);

    async Task IJasperFxProjection<IDocumentSession>.ApplyAsync(IDocumentSession operations,
        IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        var context = _contextFactory();

        // Enlisted before anything is applied, so a projection that throws part way still rolls back
        // through the same transaction rather than leaving the context to be discarded silently.
        //
        // Not disposed here, and that is the trap in this method: the context has to outlive this call
        // — nothing has been written yet, and BeforeCommitAsync runs later and may run twice under a
        // retried SQLITE_BUSY. The batch disposes it when the batch ends, committed or not.
        operations.AddTransactionParticipant(
            DbContextTransactionParticipant<TContext>.MovingOntoFishersConnection(context));

        foreach (var @event in events)
        {
            await ProjectAsync(@event, context, operations, cancellation).ConfigureAwait(false);
        }
    }
}

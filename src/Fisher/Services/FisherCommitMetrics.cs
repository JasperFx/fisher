using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher.Services;

/// <summary>
///     Applies the counters registered through
///     <see cref="OpenTelemetryOptions.ExportCounterOnChangeSets{T}" /> to every committed unit of work
///     (fisher#208).
/// </summary>
/// <remarks>
///     <para>
///         A session listener, which is the seam that already has the change set — so the counters cost
///         a listener rather than a hook of their own, and they see exactly what
///         <see cref="IDocumentSessionListener.AfterCommitAsync(IDocumentSession,IChangeSet,CancellationToken)" />
///         sees. Marten's <c>MartenCommitMetrics</c> is the same shape.
///     </para>
///     <para>
///         <b>Registered only when something opted in.</b> <c>DocumentStore</c> adds this to
///         <see cref="StoreOptions.Listeners" /> only if <see cref="OpenTelemetryOptions.Applications" />
///         is non-empty, so a store with no counters has no extra listener — and an empty unit of work
///         fires no listeners at all, which is what keeps a no-op <c>SaveChangesAsync</c> free.
///     </para>
///     <para>
///         <b>A failing counter must not fail the commit.</b> This runs after the transaction has
///         committed, so throwing here surfaces to the caller of <c>SaveChangesAsync</c> as though the
///         write had failed while the write is already durable — the worst possible reading. Every
///         application is therefore caught and logged.
///     </para>
/// </remarks>
internal sealed class FisherCommitMetrics: IDocumentSessionListener
{
    private readonly IReadOnlyList<Action<IChangeSet>> _applications;
    private readonly ILogger _logger;

    public FisherCommitMetrics(IReadOnlyList<Action<IChangeSet>> applications, ILogger? logger = null)
    {
        _applications = applications;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        => Task.CompletedTask;

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        for (var i = 0; i < _applications.Count; i++)
        {
            try
            {
                _applications[i](commit);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Fisher failed to record a commit metric");
            }
        }

        return Task.CompletedTask;
    }
}

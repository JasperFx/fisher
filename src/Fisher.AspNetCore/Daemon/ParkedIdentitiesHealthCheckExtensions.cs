using JasperFx.Events;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Daemon;

/// <summary>
///     An ASP.NET Core health check that reports <see cref="HealthStatus.Degraded" /> while any async projection
///     identity is parked (fisher#652, marten#6003), and <see cref="HealthStatus.Unhealthy" /> while a shard is paused
///     at the parking cap (<see cref="ShardFailureCategory.ParkedLimitReached" />, fisher#661, marten#6122).
/// </summary>
/// <remarks>
///     A parked identity's document is stuck at its last good state until someone replays or discards it, but the
///     rest of the projection keeps going, so that is Degraded. A shard that hit the cap has stopped altogether, so
///     that is Unhealthy. The cap is read from the extended progression columns
///     (<c>Events.EnableExtendedProgressionTracking</c>), so it works from any process, whichever one runs the shard.
///     Without extended tracking the check can only see what is parked.
/// </remarks>
public static class ParkedIdentitiesHealthCheckExtensions
{
    /// <summary>Register the parked identities health check for the store <c>AddFisher</c> registered.</summary>
    public static IHealthChecksBuilder AddFisherParkedIdentitiesHealthCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(nameof(ParkedIdentitiesHealthCheck),
            services => new ParkedIdentitiesHealthCheck(services.GetRequiredService<IDocumentStore>()),
            failureStatus: null,
            tags: ["Fisher", "AsyncDaemon", "Parked"]));
    }

    /// <summary>Register the parked identities health check for a store registered with <c>AddFisherStore&lt;T&gt;()</c>.</summary>
    public static IHealthChecksBuilder AddFisherParkedIdentitiesHealthCheck<T>(this IHealthChecksBuilder builder)
        where T : class, IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration($"{nameof(ParkedIdentitiesHealthCheck)}:{typeof(T).Name}",
            services => new ParkedIdentitiesHealthCheck(services.GetRequiredService<T>()),
            failureStatus: null,
            tags: ["Fisher", "AsyncDaemon", "Parked"]));
    }

    /// <summary>
    ///     Degraded while any identity is parked, Unhealthy while any shard is paused at the parking cap, in any of the
    ///     store's databases.
    /// </summary>
    public sealed class ParkedIdentitiesHealthCheck : IHealthCheck
    {
        private readonly IDocumentStore _store;

        /// <summary>Construct the check over a store.</summary>
        public ParkedIdentitiesHealthCheck(IDocumentStore store)
        {
            _store = store;
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var atCap = new List<string>();
                var parked = new List<string>();
                var total = 0;

                foreach (var database in _store.Tenancy.AllDatabases())
                {
                    var progress = await database.AllProjectionProgress(cancellationToken).ConfigureAwait(false);
                    atCap.AddRange(progress
                        .Where(x => x.Failure?.Category == ShardFailureCategory.ParkedLimitReached)
                        .Select(x => x.ShardName));

                    foreach (var count in await ((IEventDatabase)database)
                                 .FetchParkedCountsAsync(null, cancellationToken).ConfigureAwait(false))
                    {
                        total += count.Count;
                        parked.Add($"{count.ProjectionName} (tenant {count.TenantId}): {count.Count}");
                    }
                }

                if (atCap.Count > 0)
                {
                    return HealthCheckResult.Unhealthy(
                        $"Paused at the parking cap: {string.Join(", ", atCap)}. Fix the projection, replay or discard what's parked, then restart the shard.");
                }

                return total == 0
                    ? HealthCheckResult.Healthy("No projection identities are parked.")
                    : HealthCheckResult.Degraded(
                        $"{total} projection {(total == 1 ? "identity is" : "identities are")} parked: {string.Join(", ", parked)}");
            }
            catch (Exception e)
            {
                return HealthCheckResult.Unhealthy($"Unhealthy: {e.Message}", e);
            }
        }
    }
}

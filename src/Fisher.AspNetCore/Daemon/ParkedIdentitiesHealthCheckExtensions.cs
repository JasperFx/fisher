using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Daemon;

/// <summary>
///     An ASP.NET Core health check that reports <see cref="HealthStatus.Degraded" /> while any async projection
///     identity is parked (fisher#652, marten#6003).
/// </summary>
/// <remarks>
///     A parked identity's document is stuck at its last good state until someone replays or discards it, but the
///     rest of the projection keeps going, so this is Degraded rather than Unhealthy. A shard that paused because it
///     hit the parking cap is a paused shard, which the daemon reports as a failure the usual way.
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

    /// <summary>Degraded while any identity is parked, in any of the store's databases.</summary>
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
                var parked = new List<string>();
                var total = 0;

                foreach (var database in _store.Tenancy.AllDatabases())
                {
                    foreach (var count in await ((IEventDatabase)database)
                                 .FetchParkedCountsAsync(null, cancellationToken).ConfigureAwait(false))
                    {
                        total += count.Count;
                        parked.Add($"{count.ProjectionName} (tenant {count.TenantId}): {count.Count}");
                    }
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

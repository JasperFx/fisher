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
///     While Degraded, the description lists the parked identities, at most ten per projection, with the stream's
///     natural key beside its id when the aggregate has one (fisher#667).
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

                // Projection name -> how many identities are parked, and which databases have any
                var byProjection = new Dictionary<string, int>();
                var parkedIn = new List<(IEventDatabase Database, string[] Projections)>();

                foreach (var database in _store.Tenancy.AllDatabases())
                {
                    var progress = await database.AllProjectionProgress(cancellationToken).ConfigureAwait(false);
                    atCap.AddRange(progress
                        .Where(x => x.Failure?.Category == ShardFailureCategory.ParkedLimitReached)
                        .Select(x => x.ShardName));

                    var counts = await ((IEventDatabase)database)
                        .FetchParkedCountsAsync(null, cancellationToken).ConfigureAwait(false);

                    foreach (var count in counts)
                    {
                        total += count.Count;
                        parked.Add($"{count.ProjectionName} (tenant {count.TenantId}): {count.Count}");
                        byProjection[count.ProjectionName] =
                            byProjection.GetValueOrDefault(count.ProjectionName) + count.Count;
                    }

                    if (counts.Count > 0)
                    {
                        parkedIn.Add(((IEventDatabase)database,
                            counts.Select(x => x.ProjectionName).Distinct().ToArray()));
                    }
                }

                if (atCap.Count > 0)
                {
                    return HealthCheckResult.Unhealthy(
                        $"Paused at the parking cap: {string.Join(", ", atCap)}. Fix the projection, replay or discard what's parked, then restart the shard.");
                }

                if (total == 0)
                {
                    return HealthCheckResult.Healthy("No projection identities are parked.");
                }

                var identities = await describeIdentitiesAsync(byProjection, parkedIn, cancellationToken)
                    .ConfigureAwait(false);

                return HealthCheckResult.Degraded(
                    $"{total} projection {(total == 1 ? "identity is" : "identities are")} parked: {string.Join(", ", parked)}. {identities}");
            }
            catch (Exception e)
            {
                return HealthCheckResult.Unhealthy($"Unhealthy: {e.Message}", e);
            }
        }

        /// <summary>
        ///     fisher#667 (marten#6132): which identities are parked, at most <see cref="MaxIdentitiesPerProjection" />
        ///     per projection, each as <c>id (natural key)</c> when its aggregate has a natural key, e.g.
        ///     <c>Parked: Order: 3f1c... (ORD-1001), 9a2e..., and 4 more</c>.
        /// </summary>
        private static async Task<string> describeIdentitiesAsync(Dictionary<string, int> byProjection,
            List<(IEventDatabase Database, string[] Projections)> parkedIn, CancellationToken token)
        {
            var shown = byProjection.Keys.ToDictionary(x => x, _ => new List<string>());

            foreach (var (database, projections) in parkedIn)
            {
                foreach (var projection in projections)
                {
                    var room = MaxIdentitiesPerProjection - shown[projection].Count;
                    if (room <= 0) continue;

                    var identities = await database.FetchParkedIdentitiesAsync(
                        new ParkedIdentityQuery { ProjectionName = projection, Limit = room }, token).ConfigureAwait(false);

                    shown[projection].AddRange(identities.Select(x =>
                        x.NaturalKey is null ? x.IdentityText : $"{x.IdentityText} ({x.NaturalKey})"));
                }
            }

            return "Parked: " + string.Join("; ", byProjection.Select(pair =>
            {
                var list = shown[pair.Key];
                var more = pair.Value - list.Count;
                return $"{pair.Key}: {string.Join(", ", list)}{(more > 0 ? $", and {more} more" : "")}";
            }));
        }

        /// <summary>The most parked identities the check's description lists for one projection.</summary>
        public const int MaxIdentitiesPerProjection = 10;
    }
}

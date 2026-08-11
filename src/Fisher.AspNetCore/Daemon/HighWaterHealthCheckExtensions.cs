using System.Collections.Concurrent;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Fisher.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Daemon;

/// <summary>
///     An ASP.NET Core health check reporting whether the async daemon's high-water mark is keeping
///     up (fisher#49).
/// </summary>
/// <remarks>
///     <para>
///         <b>This has an argument of its own on Fisher, separate from the streaming results'.</b>
///         Fisher's daemon <em>warns</em> rather than refuses when the journal mode is not WAL,
///         because a non-WAL store projects correctly but serialises against every writer — the
///         misconfiguration presents as a slow projection rather than as an error. A health check
///         reporting the daemon falling behind is how an operator finds out that warning mattered.
///     </para>
///     <para>
///         Two staleness signals, because they fail differently:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>Poll-cycle age (primary).</b> The high-water agent re-stamps <c>last_updated</c>
///                 on its progression row on an idle cycle as well as on an advance — see
///                 <see cref="EventStoreOptions.HighWaterLivenessInterval" />. Its age therefore says
///                 the loop is <em>cycling</em>, whether or not the mark <em>advances</em>, so a quiet
///                 store never trips it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Sequence gap (secondary).</b> The mark sitting unchanged while later events pile
///                 up past it. The only signal available when the liveness touch is turned off, and it
///                 cannot tell a stopped daemon from an idle one.
///             </description>
///         </item>
///     </list>
///     <para>
///         <b>The extended-progression <c>heartbeat</c> column is deliberately not consulted</b>
///         (fisher#60, sibling of marten#5181). It is never written for a high-water row —
///         JasperFx's <c>ExtendedProgressionWriter.OnNext</c> returns early for
///         <c>ShardState.HighWaterMark</c> — so reading it made this check look like it had a signal it
///         did not have, and every real deployment silently fell through to the gap heuristic. Marten's
///         own tests passed only because they seeded the column with raw SQL, which is why the test
///         behind this one drives a running daemon instead.
///     </para>
/// </remarks>
public static class HighWaterHealthCheckExtensions
{
    /// <summary>
    ///     Register the high-water health check.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="staleThreshold">
    ///     How long the agent may go without completing a poll cycle — or, on the gap path, how long
    ///     the mark may sit unchanged while behind — before the check is unhealthy. 30 seconds by
    ///     default. Keep it comfortably above
    ///     <see cref="EventStoreOptions.HighWaterLivenessInterval" /> (five seconds by default), or a
    ///     healthy agent reports unhealthy between two of its own touches.
    /// </param>
    /// <param name="minimumGap">
    ///     How far behind the mark may sit before it counts as behind at all. One, by default:
    ///     the daemon is always at least one event behind a writer that has just committed.
    /// </param>
    public static IHealthChecksBuilder AddFisherHighWaterHealthCheck(this IHealthChecksBuilder builder,
        TimeSpan? staleThreshold = null, long minimumGap = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(new HighWaterHealthCheckSettings(
            staleThreshold ?? TimeSpan.FromSeconds(30), minimumGap));

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<HighWaterStateTracker>();

        return builder.AddCheck<HighWaterHealthCheck>(nameof(HighWaterHealthCheck),
            tags: ["Fisher", "AsyncDaemon", "HighWater"]);
    }

    /// <summary>How the check decides a mark is stale.</summary>
    public sealed record HighWaterHealthCheckSettings(TimeSpan StaleThreshold, long MinimumGap);

    /// <summary>
    ///     What the check remembers between polls, per database.
    /// </summary>
    /// <remarks>
    ///     <b>A stuck mark is only detectable across two readings</b> — one reading says the daemon is
    ///     behind, which is normal; two identical readings separated by the threshold say it has
    ///     stopped. Keyed by database identifier, so a database-per-tenant store (fisher#47) reports
    ///     per tenant once fisher#57 lands.
    /// </remarks>
    public sealed class HighWaterStateTracker
    {
        /// <summary>When each database's current mark was first seen, and what it was.</summary>
        public ConcurrentDictionary<string, (DateTimeOffset FirstObservedAt, long HighWaterMark)> Readings { get; }
            = new();
    }

    /// <inheritdoc cref="AddFisherHighWaterHealthCheck" />
    public sealed class HighWaterHealthCheck : IHealthCheck
    {
        private readonly IDocumentStore _store;
        private readonly TimeProvider _timeProvider;
        private readonly HighWaterHealthCheckSettings _settings;
        private readonly HighWaterStateTracker _tracker;
        private readonly TimeSpan _livenessInterval;

        /// <summary>Construct the check from the registered store and its settings.</summary>
        public HighWaterHealthCheck(IDocumentStore store, HighWaterHealthCheckSettings settings,
            TimeProvider timeProvider, HighWaterStateTracker tracker)
        {
            _store = store;
            _settings = settings;
            _timeProvider = timeProvider;
            _tracker = tracker;
            _livenessInterval = store.Options.Events.HighWaterLivenessInterval;
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // A store with no async projections has no high-water mark to be behind. Reporting it
                // unhealthy would make the check useless in exactly the applications that add it
                // defensively.
                if (!_store.Options.Projections.HasAnyAsyncProjections())
                {
                    return HealthCheckResult.Healthy("No async projections or subscriptions are registered.");
                }

                var mode = _store.Options.DaemonSettings.AsyncMode;

                if (mode is not DaemonMode.Solo)
                {
                    return HealthCheckResult.Healthy(
                        $"The async daemon mode is {mode}; this store does not advance the high-water mark.");
                }

                var databases = await ((IEventStore)_store).AllDatabases().ConfigureAwait(false);

                foreach (var database in databases)
                {
                    var result = await CheckAsync(database, cancellationToken).ConfigureAwait(false);

                    if (result.Status != HealthStatus.Healthy)
                    {
                        return result;
                    }
                }

                return HealthCheckResult.Healthy("Healthy.");
            }
            catch (Exception e)
            {
                return HealthCheckResult.Unhealthy($"Unhealthy: {e.Message}", e);
            }
        }

        private async Task<HealthCheckResult> CheckAsync(IEventDatabase database, CancellationToken token)
        {
            // Read straight from the progression row rather than through AllProjectionProgress: that
            // returns every shard's row to keep one, and ShardState has no field for last_updated — so
            // it cannot carry the only signal a liveness check can use. A database Fisher did not
            // create is not one this check knows how to read.
            var highWater = database is FisherDatabase fisher
                ? await fisher.FetchHighWaterStatusAsync(token).ConfigureAwait(false)
                : null;

            if (highWater is null)
            {
                // The daemon has never run against this database. Not a fault: nothing has been
                // appended, or it has not started yet.
                _tracker.Readings.TryRemove(database.Identifier, out _);
                return HealthCheckResult.Healthy("Healthy.");
            }

            var now = _timeProvider.GetUtcNow();

            // The primary signal: the agent re-stamps this on an idle poll cycle, not only when the
            // mark advances, so its age says the loop is cycling rather than that events are arriving.
            // A quiet store therefore never trips it — which is exactly what the gap heuristic below
            // cannot promise.
            if (_livenessInterval > TimeSpan.Zero)
            {
                _tracker.Readings.TryRemove(database.Identifier, out _);

                var age = now - highWater.LastUpdated;

                return age < _settings.StaleThreshold
                    ? HealthCheckResult.Healthy("Healthy.")
                    : HealthCheckResult.Unhealthy(
                        $"The high-water agent for '{database.Identifier}' last completed a poll cycle "
                        + $"{age.TotalSeconds:F0}s ago (at {highWater.LastUpdated:O}), past the "
                        + $"{_settings.StaleThreshold} threshold. Its poll loop has stopped cycling.");
            }

            var highest = await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
            var gap = highest - highWater.Sequence;

            if (gap <= _settings.MinimumGap)
            {
                _tracker.Readings.TryRemove(database.Identifier, out _);
                return HealthCheckResult.Healthy("Healthy.");
            }

            var reading = _tracker.Readings.GetOrAdd(database.Identifier, _ => (now, highWater.Sequence));

            if (reading.HighWaterMark != highWater.Sequence)
            {
                // It moved since the last poll, so it is working — behind, but working.
                _tracker.Readings[database.Identifier] = (now, highWater.Sequence);
                return HealthCheckResult.Healthy("Healthy.");
            }

            return now - reading.FirstObservedAt >= _settings.StaleThreshold
                ? HealthCheckResult.Unhealthy(
                    $"The high-water mark for '{database.Identifier}' has been stuck at {highWater.Sequence} "
                    + $"with {gap} later event(s) unprocessed (highest sequence {highest}) for at least "
                    + $"{_settings.StaleThreshold}. On SQLite the usual cause is a non-WAL journal mode, "
                    + "which serialises the daemon against every writer — the store logs a warning for it "
                    + "at startup.")
                : HealthCheckResult.Healthy("Healthy.");
        }
    }
}

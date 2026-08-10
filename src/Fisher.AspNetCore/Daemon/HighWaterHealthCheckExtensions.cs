using System.Collections.Concurrent;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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
///         Ported from Polecat's, including the two distinct staleness signals — a stalled liveness
///         heartbeat and a mark stuck while events pile up behind it — because they fail differently
///         and only one of them is visible without a heartbeat.
///     </para>
/// </remarks>
public static class HighWaterHealthCheckExtensions
{
    /// <summary>
    ///     Register the high-water health check.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="staleThreshold">
    ///     How long a stalled mark or a silent heartbeat may go before the check is unhealthy. 30
    ///     seconds by default.
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
        private const string HighWaterMarkShard = "HighWaterMark";

        private readonly IDocumentStore _store;
        private readonly TimeProvider _timeProvider;
        private readonly HighWaterHealthCheckSettings _settings;
        private readonly HighWaterStateTracker _tracker;

        /// <summary>Construct the check from the registered store and its settings.</summary>
        public HighWaterHealthCheck(IDocumentStore store, HighWaterHealthCheckSettings settings,
            TimeProvider timeProvider, HighWaterStateTracker tracker)
        {
            _store = store;
            _settings = settings;
            _timeProvider = timeProvider;
            _tracker = tracker;
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
            var progress = await database.AllProjectionProgress(token).ConfigureAwait(false);
            var highWater = progress.FirstOrDefault(x => string.Equals(HighWaterMarkShard, x.ShardName,
                StringComparison.Ordinal));

            if (highWater is null)
            {
                // The daemon has never run against this database. Not a fault: nothing has been
                // appended, or it has not started yet.
                _tracker.Readings.TryRemove(database.Identifier, out _);
                return HealthCheckResult.Healthy("Healthy.");
            }

            var now = _timeProvider.GetUtcNow();

            // A heartbeat is the stronger signal when the daemon supplies one: it says the poll loop is
            // cycling, whether or not the mark has moved. Preferred over the two-reading comparison
            // because a quiet store's mark legitimately does not move.
            if (highWater.LastHeartbeat is { } heartbeat)
            {
                _tracker.Readings.TryRemove(database.Identifier, out _);

                var age = now - heartbeat;

                return age < _settings.StaleThreshold
                    ? HealthCheckResult.Healthy("Healthy.")
                    : HealthCheckResult.Unhealthy(
                        $"The high-water agent for '{database.Identifier}' last reported {age.TotalSeconds:F0}s "
                        + $"ago (at {heartbeat:O}), past the {_settings.StaleThreshold} threshold. Its poll "
                        + "loop has stopped cycling.");
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

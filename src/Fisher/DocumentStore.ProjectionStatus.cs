using Fisher.Storage;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher;

/// <summary>
///     <see cref="IEventStore.GetProjectionStatusesAsync(CancellationToken)" /> and its two scoped
///     siblings — the snapshot a monitoring console's projections page renders before it subscribes to
///     <c>ShardStatesChanged</c> for live updates (fisher#243).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher answered none of the three until now, and that was the honest shape rather than a
///         silent one</b> — the interface's default throws <see cref="NotImplementedException" /> with
///         a message saying so. What made it worth closing is that Marten and Polecat answer it, so a
///         store-agnostic console rendered a projections page for them and an error for Fisher.
///     </para>
///     <para>
///         <b>fisher#240 deliberately left it out, and the reason is the one interesting decision in
///         this file.</b> Four of <see cref="ShardStatus" />'s five fields come off the database
///         cheaply; the fifth — <see cref="ShardStatus.State" /> — is a fact about the <em>running
///         daemon</em>, which <c>fi_event_progression</c> does not know. Polecat fills it by reporting
///         every shard as <c>"Stopped"</c> (polecat#200), which is not a partial answer but a wrong
///         one: it is indistinguishable from a daemon that really has stopped, and it is the reading an
///         operator acts on. Filling the slot that way is the failure fisher#120 records rather than a
///         fix for it.
///     </para>
///     <para>
///         So the state comes from the daemon this process hosts, through
///         <see cref="RunningDaemons" />, and is <see cref="Unknown" /> when there is no daemon here to
///         ask. <b>"Unknown" and "Stopped" are different operational situations</b> and the vocabulary
///         keeps them apart: a store under <c>DaemonMode.ExternallyManaged</c>, a console in another
///         process, or a hand-built store all genuinely cannot see the daemon, and saying so is worth
///         more than a confident guess.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    /// <summary>
    ///     No daemon in this process could be asked about this shard, so its runtime state is not
    ///     something this store can report.
    /// </summary>
    /// <remarks>
    ///     Deliberately not <c>"Stopped"</c>. The two are told apart everywhere else in this file, and
    ///     the distinction is the whole reason the state is not read off the progression table.
    /// </remarks>
    internal const string Unknown = "Unknown";

    Task<IReadOnlyList<ProjectionStatus>> IEventStore.GetProjectionStatusesAsync(CancellationToken ct)
        => ProjectionStatusesAsync(null, ct);

    Task<IReadOnlyList<ProjectionStatus>> IEventStore.GetProjectionStatusesAsync(string? tenantId,
        CancellationToken ct)
        => ProjectionStatusesAsync(tenantId, ct);

    /// <summary>
    ///     One database's projection statuses — the overload jasperfx#810 added, and the one with real
    ///     content on a store that spans more than one file.
    /// </summary>
    /// <remarks>
    ///     Progression rows live in each database's own <c>fi_event_progression</c>, so this is not a
    ///     filter over a store-global answer — it <em>is</em> the answer, once per database.
    /// </remarks>
    Task<IReadOnlyList<ProjectionStatus>> IEventStore.GetProjectionStatusesAsync(IEventDatabase database,
        string? tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);

        return ProjectionStatusesForAsync(DatabaseFrom(database), ct);
    }

    /// <summary>
    ///     Resolve the scope a caller named, and refuse a store-global answer the store cannot give.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A tenant scopes to a database rather than to a predicate, and here that is not a
    ///         dialect quirk — it is what Fisher's shard identity means.</b> Shard names are
    ///         <c>(projection, shard key)</c> and never <c>(projection, tenant)</c>, because
    ///         <c>fi_event_progression</c> lives in each tenant's own file and the file boundary already
    ///         draws the distinction (fisher#57). So under conjoined tenancy, where there is one file,
    ///         every tenant shares one set of progression rows and a tenant-scoped request is the
    ///         store-global answer — correctly, rather than by accident.
    ///     </para>
    ///     <para>
    ///         <b>The store-global read refuses on a multi-database store</b>, as fisher#240's
    ///         single-stream lookups do, and for a sharper reason than theirs.
    ///         <see cref="ShardStatus" /> has no database or tenant field, so concatenating N
    ///         databases' rows yields N entries per shard with identical <see cref="ShardStatus.ShardName" />
    ///         and different sequences, which a consumer cannot attribute. That is worse than
    ///         <c>Advanced.AllProjectionProgress</c>, which concatenates deliberately and gets away with
    ///         it because <see cref="ShardState" /> carries a <c>TenantId</c>.
    ///     </para>
    /// </remarks>
    private async Task<IReadOnlyList<ProjectionStatus>> ProjectionStatusesAsync(string? tenantId,
        CancellationToken ct)
    {
        if (tenantId is not null)
        {
            return await ProjectionStatusesForAsync(Tenancy.DatabaseFor(tenantId), ct).ConfigureAwait(false);
        }

        await RefreshTenantsAsync(ct).ConfigureAwait(false);

        var databases = Tenancy.AllDatabases();

        if (databases.Count == 1)
        {
            return await ProjectionStatusesForAsync(databases[0], ct).ConfigureAwait(false);
        }

        throw new NotSupportedException(
            $"Store-global GetProjectionStatusesAsync cannot answer on this Fisher store, whose "
            + $"DatabaseCardinality is {Tenancy.Cardinality} — it spans {databases.Count} database files, each "
            + "with its own fi_event_progression, and ShardStatus carries no database or tenant field to "
            + "attribute a merged answer with. Name the scope: GetProjectionStatusesAsync(tenantId, ...) "
            + "for one tenant's file, or GetProjectionStatusesAsync(database, ...) for a database from "
            + "AllDatabases(). See fisher#243, jasperfx#810.");
    }

    private async Task<IReadOnlyList<ProjectionStatus>> ProjectionStatusesForAsync(FisherDatabase database,
        CancellationToken ct)
    {
        var progress = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in await database.AllProjectionProgress(ct).ConfigureAwait(false))
        {
            // The high-water row is not a shard's progress. It is reported as EventStoreSequence
            // below, and from max(seq_id) rather than from this row — see HeadSequenceAsync.
            if (!string.Equals(state.ShardName, ShardState.HighWaterMark, StringComparison.OrdinalIgnoreCase))
            {
                progress[state.ShardName] = state.Sequence;
            }
        }

        var head = await HeadSequenceAsync(database, ct).ConfigureAwait(false);
        var tracker = await TrackerForAsync(database).ConfigureAwait(false);

        var statuses = new List<ProjectionStatus>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // AllShards() spans asynchronous projections AND subscriptions, which is what a projections
        // page wants: a subscription is a daemon shard with progress to report, and omitting it would
        // make a store with three subscriptions look like a store with none.
        foreach (var group in Options.Projections.AllShards()
                     .GroupBy(x => x.Name.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            named.Add(group.Key);

            var shards = group
                .OrderBy(x => x.Name.Identity, StringComparer.Ordinal)
                .Select(shard => new ShardStatus(
                    shard.Name.Identity,
                    StateFor(tracker, shard.Name, daemonVisible: tracker is not null),
                    progress.GetValueOrDefault(shard.Name.Identity),
                    head,
                    ErrorFor(tracker, shard.Name)))
                .ToList();

            statuses.Add(new ProjectionStatus(group.Key, LifecycleFor(group.Key), shards));
        }

        // Inline and Live projections have no shards, and are reported with an empty list rather than
        // omitted. The Lifecycle on the status is what says why it is empty, so a console can render
        // "Inline — no shards" instead of inferring that the projection does not exist. Polecat
        // synthesises a fake single shard for these and puts the LIFECYCLE in its State slot, which
        // makes the field mean two different things depending on the row.
        foreach (var source in Options.Projections.All
                     .Where(x => !named.Contains(x.Name))
                     .OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            statuses.Add(new ProjectionStatus(source.Name, source.Lifecycle.ToString(), []));
        }

        return statuses;
    }

    private string LifecycleFor(string projectionName)
        => Options.Projections.TryFindProjection(projectionName, out var source)
            ? source.Lifecycle.ToString()

            // A shard whose name matches no registered projection is a subscription's. Subscriptions
            // exist only as daemon shards — there is deliberately no inline equivalent (fisher#21) —
            // so Async is a fact about them rather than a guess.
            : ProjectionLifecycle.Async.ToString();

    /// <summary>
    ///     The head of the event store, from <c>max(seq_id)</c> rather than from the persisted
    ///     high-water row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two are the same number on a Fisher store whose daemon is current — one writer per
    ///         file plus <c>BEGIN IMMEDIATE</c> makes the mark simply <c>max(seq_id)</c>, which is why
    ///         <c>FisherHighWaterDetector</c> has no safe-zone reasoning in it. They differ exactly when
    ///         it matters: <b>the row is where the daemon got to, and a daemon that is not running
    ///         leaves it behind.</b> Reporting it as <see cref="ShardStatus.EventStoreSequence" /> would
    ///         make every shard on a stopped daemon look caught up, which is the opposite of what a
    ///         projections page is opened to find out.
    ///     </para>
    ///     <para>
    ///         A failure is swallowed to zero rather than propagated, the same judgement
    ///         <c>TryCreateUsage</c> makes about the same read: the likeliest reason it fails is that
    ///         the schema does not exist yet, which is precisely when a console is most likely to be
    ///         pointed at the store, and failing the whole page over one number answers nothing at all.
    ///     </para>
    /// </remarks>
    private static async Task<long> HeadSequenceAsync(FisherDatabase database, CancellationToken ct)
    {
        try
        {
            return await database.FetchHighestEventSequenceNumber(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    ///     The shard-state tracker of the daemon this process is running against
    ///     <paramref name="database" />, or <see langword="null" /> when there is none to ask.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read through <see cref="IProjectionCoordinator.AllDaemonsAsync" /> rather than
    ///         <c>DaemonForDatabase</c>, and that is deliberate.</b> The latter is contractually allowed
    ///         to go looking — Fisher's implementation starts daemons for tenants that have appeared
    ///         since the last poll — and it throws when it finds none. Neither belongs in a diagnostics
    ///         read: asking a console what is running must not <em>change</em> what is running, and
    ///         "nothing is running here" is an answer rather than an error.
    ///     </para>
    ///     <para>
    ///         Matched on <see cref="FisherDatabase.Identifier" />, the same key
    ///         <c>FisherDaemonHostedService.TryFindDaemon</c> uses, so the two cannot disagree about
    ///         which daemon belongs to which file.
    ///     </para>
    /// </remarks>
    private async Task<ShardStateTracker?> TrackerForAsync(FisherDatabase database)
    {
        if (RunningDaemons is null)
        {
            return null;
        }

        foreach (var daemon in await RunningDaemons.AllDaemonsAsync().ConfigureAwait(false))
        {
            if (daemon is Events.Daemon.FisherProjectionDaemon fisher
                && string.Equals(fisher.Database.Identifier, database.Identifier,
                    StringComparison.OrdinalIgnoreCase))
            {
                return daemon.Tracker;
            }
        }

        return null;
    }

    /// <summary>
    ///     A shard's runtime state, in <see cref="ShardStatus" />'s own vocabulary.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ShardAction" /> is a record of what last <em>happened</em> to a shard, where
    ///         this field is what the shard <em>is</em> — so the mapping collapses the four actions a
    ///         live shard publishes (started, updated, restarted, and skipped-ahead) onto one
    ///         <c>Running</c>. The vocabulary is the one <see cref="ShardStatus.State" />'s own
    ///         documentation names.
    ///     </para>
    ///     <para>
    ///         <b>A visible daemon with no state for the shard is <c>Stopped</c>, not
    ///         <see cref="Unknown" />.</b> Every agent publishes <c>Started</c> as it launches, so a
    ///         registered shard the tracker has never heard of is one this daemon is not running — which
    ///         is what an operator means by stopped. <see cref="Unknown" /> is reserved for the case
    ///         where there is no daemon to have an opinion.
    ///     </para>
    /// </remarks>
    private static string StateFor(ShardStateTracker? tracker, ShardName name, bool daemonVisible)
    {
        if (!daemonVisible)
        {
            return Unknown;
        }

        var state = tracker?.CurrentState(name);

        if (state is null)
        {
            return "Stopped";
        }

        return state.Action switch
        {
            ShardAction.Started or ShardAction.Updated or ShardAction.Restarted or ShardAction.Skipped
                => "Running",
            ShardAction.Paused => "Paused",
            ShardAction.Stopped => "Stopped",
            ShardAction.Faulted => "Failed",
            _ => state.Action.ToString()
        };
    }

    /// <remarks>
    ///     The latched exception's message, and null for a shard that is not failed. A dead letter is a
    ///     different thing and is read through <c>QueryDeadLetterEventsAsync</c> — this is the error
    ///     that stopped the shard, not one it skipped past.
    /// </remarks>
    private static string? ErrorFor(ShardStateTracker? tracker, ShardName name)
        => tracker?.CurrentState(name)?.Exception?.Message;
}

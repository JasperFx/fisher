using System.Collections.Concurrent;
using JasperFx;
using Weasel.Core.MultiTenancy;
using Weasel.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Tenants read from a control table in a <b>tenant-registry database</b> — Marten's master-table
///     tenancy, over Weasel's lifted runtime (fisher#213, over weasel#567).
/// </summary>
/// <remarks>
///     <para>
///         The fourth way to say database-per-tenant, and the first where the tenant set is
///         <em>durable, shared and editable at runtime</em>. <see cref="DirectoryTenantSource" />
///         resolves any tenant id at all, which is the point of it and also why it can never say "that
///         is not one of ours"; its connection strings are a convention rather than data.
///         <see cref="InMemoryTenantSource" /> holds the set in one process's memory, so nothing
///         survives a restart and two nodes disagree. A registry file is the analogue of Marten's
///         master table for a store whose tenants are files: one small SQLite database naming every
///         tenant and where its data lives.
///     </para>
///     <para>
///         <b>What Fisher supplies is a dialect and this adapter.</b> The control-table contract, the
///         cache, provisioning it exactly once, the seed list and the entire
///         <see cref="JasperFx.MultiTenancy.IDynamicTenantSource{T}" /> lifecycle are
///         <see cref="MasterTableTenancyBase{TDatabase,TDataSource}" />'s — the runtime Marten's
///         <c>MasterTableTenancy</c> and Polecat's each carried a copy of until weasel#567 lifted it
///         over a <see cref="System.Data.Common.DbDataSource" /> seam. <see cref="SqliteDataSource" />
///         <em>is</em> a <c>DbDataSource</c>, so the seam fits with nothing adapted.
///     </para>
///     <para>
///         ⚠️ <b>The base is closed over <see cref="TenantRegistration" />, not over
///         <see cref="FisherDatabase" />, and that is the whole design.</b> The base's own remarks say
///         everything it does with a <c>TDatabase</c> is cache it, hand it back and dispose it — so
///         closing it over the registration makes its cache Fisher's <em>synchronous snapshot of the
///         control table</em>, which is exactly what <see cref="ITenantSource.TryFind" /> needs.
///         Closing it over <see cref="FisherDatabase" /> instead would have put a second database cache
///         beside <see cref="DynamicTenancy" />'s — two <c>SqliteDataSource</c>s and two connection
///         pools per tenant — and would have forced <see cref="ITenancy.DatabaseFor" /> to resolve
///         asynchronously.
///     </para>
///     <para>
///         ⚠️ <b>Which is the one thing Marten's version does that Fisher must not copy.</b>
///         <c>MasterTableTenancy.GetTenant(string)</c> is
///         <c>tryFindTenantDatabase(...).GetAwaiter().GetResult()</c>. <c>DatabaseFor</c> is reached
///         from <c>OpenSession</c>, which has no <c>await</c> to offer, and the whole reason
///         <see cref="ITenantSource.TryFind" /> is synchronous and <see cref="ITenantSource.AllAsync" />
///         is not is to keep sync-over-async off the session path. So the trade is stated rather than
///         hidden: <b>a tenant this process has not read yet does not resolve.</b> One added through
///         <see cref="AddTenantAsync" /> is cached eagerly and usable immediately; one added to the
///         registry by <em>another</em> process appears on the next refresh — which the async daemon
///         does every minute, <c>ApplyAllConfiguredChangesToDatabaseAsync</c>, <c>db-apply</c> and
///         every other <c>DynamicTenancy.RefreshAsync</c> caller do on demand, and
///         <see cref="RefreshAsync" /> does when an application wants it now.
///     </para>
///     <para>
///         <b>Deprovisioning is disable or forget, never delete</b> — Fisher's standing rule, and the
///         lifted base already agrees with it. <see cref="DisableTenantAsync" /> flips a flag and
///         <see cref="RemoveTenantAsync" /> deletes the control-table <em>row</em>; neither touches the
///         tenant's <c>.db</c> file, which is the cheapest deprovisioning any Critter Stack store could
///         offer and the most irreversible, and which Fisher cannot know is backed up. There is
///         deliberately no member here that removes one. To erase a tenant's <em>rows</em> and keep the
///         file, use <c>store.Advanced.DeleteAllTenantDataAsync</c>.
///     </para>
/// </remarks>
public sealed class MasterTableTenantSource
    : MasterTableTenancyBase<TenantRegistration, SqliteDataSource>, ITenantSource
{
    private readonly StoreOptions _options;

    // The connection string last seen for each tenant, so a *disabled* tenant can still be described.
    // The control table's reads are scoped to enabled rows by contract (see IMasterTenantTableDialect
    // .SelectConnectionString), so once a tenant is disabled there is no supported way to read its
    // connection string back — and nothing needs it, because DatabaseFor refuses a disabled tenant
    // before it would use one. This is what keeps TryFind able to answer "disabled" rather than
    // "unknown" for a tenant this process has seen.
    private readonly ConcurrentDictionary<string, string> _lastKnown =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Action<string>? OnTenantRevoked { get; set; }

    // The seed list, readable without I/O. See TryFind for why that matters.
    private readonly Dictionary<string, string> _seeded;

    internal MasterTableTenantSource(StoreOptions options,
        MasterTableTenancyOptions<SqliteDataSource> tenancyOptions)
        : base(tenancyOptions, SqliteMasterTenantTableDialect.TableName,
            SqliteMasterTenantTableDialect.Instance, StringComparer.OrdinalIgnoreCase)
    {
        _options = options;
        _seeded = tenancyOptions.SeedDatabases.AllActiveByTenant()
            .ToDictionary(x => x.TenantId, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The registry's own connection string, or the data source it was handed.
    /// </summary>
    /// <remarks>
    ///     Its PRAGMAs are the store's, so a registry file gets the same WAL, busy timeout and foreign
    ///     key settings every other Fisher database does — it is a SQLite file taking a write lock like
    ///     any other, and an operator adding a tenant while the store is running is a writer.
    /// </remarks>
    protected override SqliteDataSource BuildDataSource(string connectionString)
        => new(connectionString, _options.PragmaSettings);

    /// <remarks>
    ///     A row, not a database. See the class remarks for why the base is closed over this type.
    /// </remarks>
    protected override TenantRegistration BuildDatabase(string tenantId, string connectionString)
    {
        _lastKnown[tenantId] = connectionString;

        return new TenantRegistration(tenantId, connectionString);
    }

    /// <summary>
    ///     Every control-plane read and write runs through the store's Polly pipeline.
    /// </summary>
    /// <remarks>
    ///     The registry is a SQLite file and SQLite takes one writer per file, so a tenant being added
    ///     while another node reads the table is exactly the <c>SQLITE_BUSY</c> the pipeline exists
    ///     for — the same reason every other database call in Fisher goes through it. Polecat overrides
    ///     this hook for the same purpose; Marten leaves it on the default.
    /// </remarks>
    protected override Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken token)
        => _options.ResiliencePipeline.ExecuteAsync(
            async ct => await operation(ct).ConfigureAwait(false), token).AsTask();

    // ---- ITenantSource ----

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Answered from the base's cache, with no I/O — which is what the session path requires.
    ///         A tenant this process has never read is reported as <b>unknown</b>; see the class
    ///         remarks for when that can happen and what refreshes it.
    ///     </para>
    ///     <para>
    ///         <b>A disabled tenant is reported as inactive rather than as unknown</b>, so
    ///         <see cref="DynamicTenancy" /> raises <see cref="DisabledTenantException" /> for it.
    ///         Weasel's base deliberately collapses the two — a disabled tenant is not routable, which
    ///         is what disabling means — and its own remarks say a caller turning that into an
    ///         exception should decide which. Fisher decided in fisher#58: "switched off" and "never
    ///         heard of it" are different operational situations and an application handling one should
    ///         not have to guess which it got.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>The seed list is the last resort, and it is what makes the default tenant work at
    ///         all.</b> <see cref="DynamicTenancy.Default" /> is read while the store is being *built*,
    ///         before anything could have read the control table — so a registry naming its tenants
    ///         only in the table cannot answer for <c>*DEFAULT*</c> and the store refuses to construct.
    ///         A seeded tenant is configuration rather than data: it is known synchronously, and it is
    ///         upserted into the table on the first refresh, so the two never disagree for long.
    ///         <c>SeedDatabases.RegisterDefault(connectionString)</c> is therefore how a registry store
    ///         gets a default tenant. A seed is checked <em>after</em> the disabled set, so suspending
    ///         a seeded tenant still suspends it.
    ///     </para>
    /// </remarks>
    public bool TryFind(string tenantId, out TenantRegistration registration)
    {
        var cached = FindCachedDatabase(tenantId);
        if (cached is not null)
        {
            // Disabling evicts from the base's cache, so reaching here means enabled — but check
            // anyway rather than relying on eviction order, since the two are refreshed separately.
            registration = _disabled.Contains(tenantId) ? cached with { IsActive = false } : cached;

            return true;
        }

        if (_disabled.Contains(tenantId))
        {
            registration = new TenantRegistration(tenantId,
                _lastKnown.GetValueOrDefault(tenantId, string.Empty), IsActive: false);

            return true;
        }

        if (_seeded.TryGetValue(tenantId, out var seeded))
        {
            registration = new TenantRegistration(tenantId, Options.CorrectConnectionString(seeded));

            return true;
        }

        registration = null!;

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Reads the control table — the enabled rows through
    ///     <see cref="MasterTableTenancyBase{TDatabase,TDataSource}.BuildDatabasesAsync" />, which also
    ///     provisions the table on first use and writes the seed list, and the disabled ids beside them
    ///     so a suspended tenant stays distinguishable from an unknown one.
    /// </remarks>
    public async ValueTask<IReadOnlyList<TenantRegistration>> AllAsync(CancellationToken token = default)
    {
        var enabled = await BuildDatabasesAsync(token).ConfigureAwait(false);
        var disabled = await AllDisabledAsync(token).ConfigureAwait(false);

        _disabled = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);

        if (disabled.Count == 0)
        {
            return enabled;
        }

        // Reported alongside, and inactive, so a caller enumerating tenants sees a suspended one exists
        // rather than concluding it was removed. DynamicTenancy.RefreshAsync skips inactive entries, so
        // no database is built for one.
        return enabled
            .Concat(disabled.Select(id => new TenantRegistration(id,
                _lastKnown.GetValueOrDefault(id, string.Empty), IsActive: false)))
            .ToList();
    }

    /// <summary>
    ///     Re-read the control table now, rather than waiting for the daemon's poll.
    /// </summary>
    /// <remarks>
    ///     For an application that added or disabled a tenant through a <em>different</em> process and
    ///     wants this one to see it immediately. A tenant added through <see cref="AddTenantAsync" />
    ///     on this instance needs nothing — it is cached as it is written.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken token = default)
        => await AllAsync(token).ConfigureAwait(false);

    // ---- the runtime lifecycle, named the way an application reads it ----

    /// <summary>
    ///     Register a tenant, or correct an existing tenant's connection string.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The tenant is cached as it is written, so the very next session for it resolves — no
    ///         refresh, no restart. Its file and schema are created the first time anything connects
    ///         to it, exactly as under <see cref="DirectoryTenantSource" />, which is what makes
    ///         provisioning a tenant a file plus a migration here rather than a <c>CREATE DATABASE</c>.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>This does not re-enable a suspended tenant.</b> The upsert leaves the disabled
    ///         flag alone — Marten's does the same and Polecat's <c>MERGE</c> does not, and the shared
    ///         dialect contract leaves the choice to the dialect for exactly that reason. Re-enabling
    ///         as a side effect of correcting a connection string would silently undo a deliberate
    ///         suspension; <see cref="EnableTenantAsync" /> is one call away and says what it means.
    ///     </para>
    /// </remarks>
    public Task AddTenantAsync(string tenantId, string connectionString, CancellationToken token = default)
        => AddDatabaseRecordAsync(tenantId, connectionString, token);

    /// <summary>
    ///     Suspend a tenant. Sessions for it are refused until it is resumed; <b>its file is
    ///     untouched.</b>
    /// </summary>
    /// <remarks>
    ///     The base evicts the tenant from its cache, which is what makes suspension take effect
    ///     without a refresh — a cached row would otherwise go on resolving a tenant that is switched
    ///     off. <see cref="DynamicTenancy" /> still holds the tenant's <see cref="FisherDatabase" />;
    ///     call <c>ForgetTenantAsync</c> to release its pooled connections as well.
    /// </remarks>
    public async Task SuspendTenantAsync(string tenantId, CancellationToken token = default)
    {
        await DisableTenantAsync(tenantId, token).ConfigureAwait(false);

        _disabled = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase) { tenantId };

        // The base evicts from its own cache; this is what evicts the tenant's FisherDatabase, so the
        // suspension takes effect for a tenant the store has already opened a session for.
        OnTenantRevoked?.Invoke(tenantId);
    }

    /// <summary>Resume a suspended tenant. It resolves again on next access.</summary>
    public async Task ResumeTenantAsync(string tenantId, CancellationToken token = default)
    {
        await EnableTenantAsync(tenantId, token).ConfigureAwait(false);

        var next = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
        next.Remove(tenantId);
        _disabled = next;

        // Enabling does not refill the cache — the base loads lazily, which is also what makes
        // enabling a tenant that was never added a no-op rather than a phantom. Refresh so the
        // synchronous TryFind can answer for it again without waiting for the daemon's poll.
        await RefreshAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Remove a tenant's <b>registry row</b>. <b>Its database file is not deleted.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fisher deprovisions and never deletes, and this is that rule at the registry: the row
    ///         goes, the tenant stops resolving, and the file stays where an operator can archive it,
    ///         copy it, or remove it themselves. Deleting it here would be the cheapest deprovisioning
    ///         of any Critter Stack store and the most irreversible, and Fisher cannot know whether it
    ///         is backed up.
    ///     </para>
    ///     <para>
    ///         The lifted base already draws the line in the same place — its
    ///         <c>DeleteDatabaseRecordAsync</c> says "the tenant's own database is left completely
    ///         alone" — so adopting it cost no guard.
    ///     </para>
    /// </remarks>
    public async Task ForgetTenantAsync(string tenantId, CancellationToken token = default)
    {
        await DeleteDatabaseRecordAsync(tenantId, token).ConfigureAwait(false);

        var next = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
        next.Remove(tenantId);
        _disabled = next;
        _lastKnown.TryRemove(tenantId, out _);

        OnTenantRevoked?.Invoke(tenantId);
    }

    /// <summary>The tenant ids currently suspended.</summary>
    public Task<IReadOnlyList<string>> AllSuspendedAsync(CancellationToken token = default)
        => AllDisabledAsync(token);
}

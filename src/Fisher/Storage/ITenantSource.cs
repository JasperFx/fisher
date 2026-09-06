using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     One tenant, and where its data lives (fisher#58).
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConnectionString">The SQLite connection string for that tenant's file.</param>
/// <param name="IsActive">
///     Whether the tenant may be used. A suspended tenant is refused rather than forgotten — see
///     <see cref="ITenantSource" />.
/// </param>
public sealed record TenantRegistration(string TenantId, string ConnectionString, bool IsActive = true);

/// <summary>
///     Where the set of tenants comes from, when it is not fixed at configuration time (fisher#58).
/// </summary>
/// <remarks>
///     <para>
///         <b>This shape is viable on SQLite in a way it is not on either sibling.</b> Provisioning a
///         tenant here is a file plus a migration — cheap enough to do on first use, which is exactly
///         what makes "a tenant appears without a restart" a reasonable thing to offer. On PostgreSQL
///         or SQL Server the same act is a CREATE DATABASE and an operational decision.
///     </para>
///     <para>
///         <b><see cref="TryFind" /> is synchronous and <see cref="AllAsync" /> is not, and the split is
///         forced rather than untidy.</b> <see cref="ITenancy.DatabaseFor" /> is reached from
///         <c>OpenSession</c>, which has no <c>await</c> to offer — so the hot path has to be answerable
///         without I/O. The convention source manages that trivially (a tenant id maps to a path);
///         a source backed by an application's own tenants table caches, and refreshes through
///         <see cref="AllAsync" />. Enumerating every tenant, by contrast, is a startup and daemon
///         concern where an <c>await</c> is available.
///     </para>
///     <para>
///         <b>Nothing here deletes anything, deliberately.</b> Deprovisioning a tenant on SQLite means
///         deleting a file, which is the cheapest and the most irreversible operation any Critter Stack
///         store could offer — and Fisher cannot know whether that file is backed up. So a tenant is
///         suspended (<see cref="TenantRegistration.IsActive" />) or dropped from the source, and its
///         file stays where an operator can archive it, copy it, or remove it themselves.
///     </para>
/// </remarks>
public interface ITenantSource
{
    /// <summary>
    ///     Resolve one tenant without I/O, for the session path.
    /// </summary>
    /// <returns>False when this source does not know the tenant.</returns>
    bool TryFind(string tenantId, out TenantRegistration registration);

    /// <summary>
    ///     Every tenant known right now — read at startup, and whenever the daemon looks for databases
    ///     that have appeared since.
    /// </summary>
    ValueTask<IReadOnlyList<TenantRegistration>> AllAsync(CancellationToken token = default);

    /// <summary>
    ///     Set by <see cref="DynamicTenancy" />, for a source to call when a tenant stops being
    ///     routable — suspended, or dropped from the source (fisher#213).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without it, suspending a tenant the store has already resolved does nothing.</b>
    ///         <see cref="DynamicTenancy" /> caches a <see cref="FisherDatabase" /> per tenant and
    ///         <see cref="ITenancy.DatabaseFor" /> answers from that cache, so a suspension used to take
    ///         effect only for a tenant nothing had opened yet — which is the opposite of what an
    ///         operator switching a tenant off means, and it made "restart the process" part of the
    ///         procedure.
    ///     </para>
    ///     <para>
    ///         <b>A callback rather than a check on the resolution path</b>, because
    ///         <see cref="TryFind" /> is on the session-open hot path and the convention source builds
    ///         a connection string on every call — consulting it per session would be a real cost to
    ///         make a rare event immediate. Revocation is rare, so it pays for itself.
    ///     </para>
    ///     <para>
    ///         Default-implemented as a no-op pair, so a source written before this — or one that
    ///         genuinely never revokes — needs no change.
    ///     </para>
    /// </remarks>
    Action<string>? OnTenantRevoked
    {
        get => null;
        set { }
    }
}

/// <summary>
///     Tenants as files in one directory, named <c>&lt;tenantId&gt;.db</c> (fisher#58).
/// </summary>
/// <remarks>
///     <para>
///         The default, and the reason <c>TenantDatabases.InDirectory</c> existed in stage 1 before
///         anything needed it: a tenant that appears at runtime has no configuration line to carry a
///         path, so a convention is the only thing that can name its file.
///     </para>
///     <para>
///         <b>Any tenant id resolves</b>, whether or not its file exists yet — which is what makes a new
///         tenant work with no registration step at all. The file and its schema are created the first
///         time something opens a connection to it. Enumeration, by contrast, reports only the files
///         that are actually there.
///     </para>
/// </remarks>
public sealed class DirectoryTenantSource : ITenantSource
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, bool> _suspended = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryTenantSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public Action<string>? OnTenantRevoked { get; set; }

    /// <summary>
    ///     Suspend a tenant. Its file is untouched; sessions for it are refused until it is resumed.
    /// </summary>
    /// <remarks>
    ///     Takes effect immediately, including for a tenant the store has already resolved — see
    ///     <see cref="ITenantSource.OnTenantRevoked" />.
    /// </remarks>
    public void Suspend(string tenantId)
    {
        _suspended[tenantId] = true;
        OnTenantRevoked?.Invoke(tenantId);
    }

    /// <inheritdoc cref="Suspend" />
    public void Resume(string tenantId) => _suspended.TryRemove(tenantId, out _);

    public bool TryFind(string tenantId, out TenantRegistration registration)
    {
        registration = new TenantRegistration(tenantId, PathFor(tenantId),
            IsActive: !_suspended.ContainsKey(tenantId));

        return true;
    }

    public ValueTask<IReadOnlyList<TenantRegistration>> AllAsync(CancellationToken token = default)
    {
        var found = Directory.Exists(_directory)
            ? Directory.EnumerateFiles(_directory, "*.db")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => new TenantRegistration(x!, PathFor(x!), !_suspended.ContainsKey(x!)))
                .ToList()
            : [];

        return ValueTask.FromResult<IReadOnlyList<TenantRegistration>>(found);
    }

    private string PathFor(string tenantId)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, $"{tenantId}.db") }
            .ToString();
}

/// <summary>
///     Tenants an application registers by hand, at any time (fisher#58).
/// </summary>
/// <remarks>
///     For the common case the issue anticipated — the application already has a tenants table of its
///     own and pushes into this as it changes — and for tests. A source reading that table directly is
///     an application's to write; the interface is small precisely so that it can be.
/// </remarks>
public sealed class InMemoryTenantSource : ITenantSource
{
    private readonly ConcurrentDictionary<string, TenantRegistration> _tenants =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryTenantSource Add(string tenantId, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _tenants[tenantId] = new TenantRegistration(tenantId, connectionString);

        return this;
    }

    /// <summary>
    ///     Suspend or resume a tenant, leaving its file alone.
    /// </summary>
    public void SetActive(string tenantId, bool isActive)
    {
        if (_tenants.TryGetValue(tenantId, out var existing))
        {
            _tenants[tenantId] = existing with { IsActive = isActive };

            if (!isActive)
            {
                OnTenantRevoked?.Invoke(tenantId);
            }
        }
    }

    /// <summary>
    ///     Stop resolving a tenant. Its file is not deleted — see <see cref="ITenantSource" />.
    /// </summary>
    public void Remove(string tenantId)
    {
        if (_tenants.TryRemove(tenantId, out _))
        {
            OnTenantRevoked?.Invoke(tenantId);
        }
    }

    /// <inheritdoc />
    public Action<string>? OnTenantRevoked { get; set; }

    public bool TryFind(string tenantId, out TenantRegistration registration)
        => _tenants.TryGetValue(tenantId, out registration!);

    public ValueTask<IReadOnlyList<TenantRegistration>> AllAsync(CancellationToken token = default)
        => ValueTask.FromResult<IReadOnlyList<TenantRegistration>>(_tenants.Values.ToList());
}

/// <summary>
///     A tenant that exists but has been suspended.
/// </summary>
/// <remarks>
///     Distinct from <see cref="UnknownTenantException" /> on purpose: "this tenant is switched off" and
///     "there is no such tenant" are different operational situations, and an application handling one
///     should not have to guess which it got.
/// </remarks>
public class DisabledTenantException : Exception
{
    internal DisabledTenantException(string tenantId)
        : base($"Tenant '{tenantId}' is registered but suspended, so this store will not open a session "
               + "for it. Resume it through the ITenantSource that owns it. Its database file is "
               + "untouched — Fisher never deletes a tenant's data.")
        => TenantId = tenantId;

    public string TenantId { get; }
}

namespace Fisher.Storage;

/// <summary>
///     Refuses a tenant id that would not be a safe file name, before it is concatenated into one
///     (fisher#318).
/// </summary>
/// <remarks>
///     <para>
///         <b>Two properties of the directory convention combine badly, and neither is wrong on its
///         own.</b> <see cref="DirectoryTenantSource" /> resolves <b>any</b> tenant id, whether or not
///         its file exists yet — that is what makes a tenant appear at runtime with no registration
///         step, and it is the whole point of the convention. And <see cref="Path.Combine(string,string)" />
///         does not constrain its result to the first argument: a relative <c>..</c> escapes it, and a
///         rooted second argument replaces it outright. So without this, a tenant id that is not a
///         plain identifier resolves to a path <em>outside</em> the configured directory, and Fisher
///         creates a SQLite database there — at whatever the process can write.
///     </para>
///     <para>
///         <b>This is Fisher's to refuse rather than the application's to sanitise</b>, because Fisher
///         is the layer that turns the string into a path and therefore the layer that knows a path is
///         being built. The reachable shape is not exotic: Wolverine and ASP.NET tenant-id detection
///         commonly forward a header, a route value or a subdomain straight into
///         <c>ForTenant(...)</c>. <c>InMemoryTenantSource.Add</c> already validated its input, so the
///         sources were inconsistent about whether anything was checked at all.
///     </para>
///     <para>
///         <b>Refused, never sanitised</b>, and that is the load-bearing choice. Stripping the unsafe
///         characters would map two different tenant ids onto one database file — which is the exact
///         failure database-per-tenant exists to make impossible, and it would be silent.
///     </para>
///     <para>
///         <b>A positive charset rather than a denylist.</b> A denylist has to anticipate every
///         separator, every reserved name and every platform's own normalisation; an allowlist of
///         <c>A-Z a-z 0-9 . _ -</c> is checkable in one pass and cannot be widened by a platform
///         Fisher was not tested on. The containment check afterwards is belt and braces for anything
///         the charset rule missed.
///     </para>
///     <para>
///         ⚠️ <b>Case is deliberately not touched here.</b> Fisher's tenant caches compare
///         <see cref="StringComparer.OrdinalIgnoreCase" /> while a case-sensitive filesystem would give
///         <c>Acme.db</c> and <c>acme.db</c> two files — a pre-existing inconsistency that this
///         refusal neither creates nor closes. Folding case here would be the sanitising this method
///         refuses to do.
///     </para>
/// </remarks>
internal static class TenantFileName
{
    /// <summary>
    ///     255 bytes is the common filesystem ceiling for one path component, and the id is not the
    ///     whole of it — <c>.db</c>, and SQLite's own <c>-wal</c> and <c>-shm</c> sidecars, are
    ///     appended to it.
    /// </summary>
    private const int MaxLength = 200;

    /// <summary>
    ///     Windows resolves these as devices whatever directory they appear in, and with any
    ///     extension — so <c>CON.db</c> is not a file. Refused on every platform, because a tenant id
    ///     that works in development and not in production is worse than one refused everywhere.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    ///     The full path of <paramref name="tenantId" />'s database file under
    ///     <paramref name="directory" />, or an <see cref="ArgumentException" /> naming what is wrong
    ///     with the id.
    /// </summary>
    public static string PathFor(string directory, string tenantId)
    {
        Assert(tenantId);

        var path = Path.Combine(directory, $"{tenantId}.db");

        // Belt and braces. The charset rule above already makes traversal unexpressible, so reaching
        // this is a bug in the rule rather than a hostile id — but the cost of checking is one
        // normalisation and the cost of being wrong is a database file at an arbitrary path.
        var resolved = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory);

        if (!resolved.StartsWith(
                root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Tenant id '{tenantId}' resolves to '{resolved}', which is outside the configured "
                + $"tenant directory '{root}'.", nameof(tenantId));
        }

        return path;
    }

    /// <summary>
    ///     Throw unless <paramref name="tenantId" /> is usable as one path component.
    /// </summary>
    public static void Assert(string tenantId)
    {
        // ⚠️ Fisher's own sentinel, and the one id here that is never caller input — DynamicTenancy
        // resolves it while the store is still being CONSTRUCTED, so refusing it would make every
        // directory-tenancy store fail to build. It is exempted by exact identity rather than by
        // widening the charset to admit '*', because '*' in a caller's id is exactly what this
        // refuses. Renaming the file it maps to would orphan every existing default-tenant database,
        // so it stays as it is; see fisher#325 for what that costs on Windows.
        if (tenantId == JasperFx.StorageConstants.DefaultTenantId)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException(
                "A tenant id cannot be null, empty or whitespace.", nameof(tenantId));
        }

        if (tenantId.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A tenant id is used as a file name and cannot be longer than {MaxLength} characters; "
                + $"this one is {tenantId.Length}.", nameof(tenantId));
        }

        foreach (var c in tenantId)
        {
            if (!IsAllowed(c))
            {
                throw new ArgumentException(
                    $"Tenant id '{tenantId}' is used as a file name under the configured tenant "
                    + $"directory, so it may contain only letters, digits, '.', '_' and '-'. "
                    + $"'{c}' is not allowed. Map the incoming identifier to a safe id rather than "
                    + "relaxing this — two tenant ids must never resolve to one database file.",
                    nameof(tenantId));
            }
        }

        // '.' is allowed because 'acme.co' is an ordinary tenant id, which is exactly what makes
        // these three cases worth naming separately: '..' traverses, a leading '.' hides the file on
        // every Unix-like system, and Windows silently strips a trailing one — so 'acme.' and 'acme'
        // would be one file there and two everywhere else.
        if (tenantId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Tenant id '{tenantId}' contains '..', which would traverse out of the configured "
                + "tenant directory.", nameof(tenantId));
        }

        if (tenantId.StartsWith('.') || tenantId.EndsWith('.'))
        {
            throw new ArgumentException(
                $"Tenant id '{tenantId}' starts or ends with '.', which names a hidden file on Unix "
                + "and is silently stripped on Windows — so two ids could resolve to one database "
                + "file.", nameof(tenantId));
        }

        if (ReservedNames.Contains(tenantId))
        {
            throw new ArgumentException(
                $"Tenant id '{tenantId}' is a reserved device name on Windows, where it does not name "
                + "a file at all. Refused on every platform, so a store does not work in development "
                + "and fail in production.", nameof(tenantId));
        }
    }

    private static bool IsAllowed(char c)
        => c is >= 'a' and <= 'z'
           || c is >= 'A' and <= 'Z'
           || c is >= '0' and <= '9'
           || c is '.' or '_' or '-';
}

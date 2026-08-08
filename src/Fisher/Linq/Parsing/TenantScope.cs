namespace Fisher.Linq.Parsing;

/// <summary>
///     Which tenants' rows a query over a conjoined document type may see.
/// </summary>
/// <remarks>
///     Three states rather than a nullable list, because "the session's tenant" and "every tenant" are
///     different answers and both are different from "these named ones". Applied once per statement —
///     see <c>FisherQueryProvider.ApplyTenantFilter</c> and fisher#51 for why that matters.
/// </remarks>
internal enum TenantScope
{
    /// <summary>The default: the session's own tenant.</summary>
    Current,

    /// <summary><c>AnyTenant()</c> — no tenant term at all.</summary>
    AnyTenant,

    /// <summary><c>TenantIsOneOf(...)</c> — an <c>in</c> over the named tenants.</summary>
    NamedTenants
}

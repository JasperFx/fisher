using JasperFx;
using Weasel.Core.SqlGeneration;

namespace Fisher.Internal;

/// <summary>
///     Which tenants a query session reads for conjoined documents and events when the query does not say
///     itself (fisher#665, the port of marten#6011 and marten#6070). Only
///     <see cref="AdvancedOperations.QueryAcrossAllDatabasesAsync{T}(System.Func{IQuerySession,System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Collections.Generic.IReadOnlyList{T}}},int,System.Threading.CancellationToken)" />
///     sets one: every tenant, or a fixed list of tenants. Without a scope a session reads its own tenant.
///     An <c>AnyTenant()</c> or <c>TenantIsOneOf()</c> in the query still wins.
/// </summary>
/// <remarks>
///     Held by the session it was opened with and not by a <c>ForTenant()</c> scope of it, which reads its
///     own tenant as it always does.
/// </remarks>
internal sealed class SessionTenantScope
{
    public static readonly SessionTenantScope AllTenants = new(null);

    private SessionTenantScope(string[]? tenantIds)
    {
        TenantIds = tenantIds;
    }

    public static SessionTenantScope For(string[] tenantIds) => new(tenantIds);

    /// <summary>
    ///     Null for every tenant.
    /// </summary>
    public string[]? TenantIds { get; }

    /// <summary>
    ///     The filter that stands in for the session's own tenant filter in a LINQ statement, or null for
    ///     every tenant.
    /// </summary>
    public ISqlFragment? BuildFilter(string column)
        => TenantIds is null
            ? null
            : new Linq.SqlGeneration.WhereInFilter(column, TenantIds.Cast<object?>().ToList());

    /// <summary>
    ///     For a statement built with a <see cref="Weasel.Sqlite.CommandBuilder" /> that would otherwise
    ///     append <c>and tenant_id = ?</c>: the predicate to use instead. Nothing for every tenant.
    /// </summary>
    public void AppendFilter(Weasel.Sqlite.CommandBuilder builder, string column = "tenant_id")
    {
        if (TenantIds is null) return;

        builder.Append($" and {column} in (");
        for (var i = 0; i < TenantIds.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.AppendParameter(TenantIds[i]);
        }

        builder.Append(")");
    }

    /// <summary>
    ///     For hand-written SQL that would otherwise say <c>and tenant_id = @tenant_id</c>: the predicate to
    ///     use instead, with its parameters added to <paramref name="command" />. Empty for every tenant.
    /// </summary>
    public string SqlFilter(Microsoft.Data.Sqlite.SqliteCommand command, string column = "tenant_id")
    {
        if (TenantIds is null) return "";

        var names = new string[TenantIds.Length];
        for (var i = 0; i < TenantIds.Length; i++)
        {
            names[i] = $"@scope_tenant{i}";
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(names[i], TenantIds[i])
            {
                SqliteType = Microsoft.Data.Sqlite.SqliteType.Text
            });
        }

        return $" and {column} in ({string.Join(", ", names)})";
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher;

/// <summary>
///     Query operators that have no standard LINQ spelling — set membership, emptiness, and the tenant
///     scope a query runs under (fisher#26).
/// </summary>
/// <remarks>
///     <para>
///         Marker methods, like the soft-delete operators: never executed, only recognised in the
///         expression tree. Names and shapes match Polecat's and Marten's exactly, so query code ports
///         between the stores unchanged.
///     </para>
///     <para>
///         <see cref="IsOneOf{T}(T,T[])" /> and <see cref="In{T}(T,T[])" /> are the same operator under
///         two names, which is Marten's doing rather than a Fisher decision — both are carried so that
///         either spelling ports.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over this class's own marker methods, which the class itself preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: LINQ expression construction requires runtime code generation in the general case.")]
public static class LinqExtensions
{
    private static readonly MethodInfo AnyTenantMethod =
        typeof(LinqExtensions).GetMethod(nameof(AnyTenant))!;

    private static readonly MethodInfo TenantIsOneOfMethod =
        typeof(LinqExtensions).GetMethod(nameof(TenantIsOneOf))!;

    /// <summary>
    ///     <c>x.Status.IsOneOf(a, b, c)</c> — membership of a fixed set, as <c>in (…)</c>.
    /// </summary>
    public static bool IsOneOf<T>(this T value, params T[] matches) => matches.Contains(value);

    /// <inheritdoc cref="IsOneOf{T}(T,T[])" />
    public static bool IsOneOf<T>(this T value, IList<T> matches) => matches.Contains(value);

    /// <inheritdoc cref="IsOneOf{T}(T,T[])" />
    public static bool In<T>(this T value, params T[] matches) => matches.Contains(value);

    /// <inheritdoc cref="IsOneOf{T}(T,T[])" />
    public static bool In<T>(this T value, IList<T> matches) => matches.Contains(value);

    /// <summary>
    ///     <c>x.Tags.IsEmpty()</c> — a JSON array with no elements, or no array at all.
    /// </summary>
    /// <remarks>
    ///     An absent member counts as empty. <c>json_extract</c> yields SQL NULL for a missing key and
    ///     <c>json_array_length(null)</c> is NULL rather than 0, so the translated predicate has to test
    ///     both — a caller asking "is this empty" means "is there anything in it", and "the key is not
    ///     there" is an honest yes.
    /// </remarks>
    public static bool IsEmpty<T>(this IEnumerable<T> enumerable) => !enumerable.Any();

    /// <summary>
    ///     Run this query across every tenant rather than the session's.
    /// </summary>
    /// <remarks>
    ///     Only meaningful for a type registered <c>MultiTenanted()</c>; against any other it throws,
    ///     because there is no <c>tenant_id</c> column for it to have an opinion about and silently
    ///     doing nothing would look like it worked.
    /// </remarks>
    public static IQueryable<T> AnyTenant<T>(this IQueryable<T> queryable)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            AnyTenantMethod.MakeGenericMethod(typeof(T)), queryable.Expression));

    /// <summary>
    ///     Run this query across the named tenants rather than the session's.
    /// </summary>
    /// <inheritdoc cref="AnyTenant{T}" />
    public static IQueryable<T> TenantIsOneOf<T>(this IQueryable<T> queryable, params string[] tenantIds)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            TenantIsOneOfMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
            Expression.Constant(tenantIds)));
}

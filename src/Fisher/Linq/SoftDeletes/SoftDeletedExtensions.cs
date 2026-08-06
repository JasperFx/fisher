using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Linq.SoftDeletes;

/// <summary>
///     The query operators that say something about a soft-deleted document's deletion, rather than
///     about the document.
/// </summary>
/// <remarks>
///     <para>
///         Marker methods: they are never executed, only recognised by
///         <see cref="Parsing.LinqQueryParser" /> in the expression tree. Calling one on a type that is
///         not soft-deleted throws rather than answering, because every answer would be a lie — there
///         is no column to have an opinion about.
///     </para>
///     <para>
///         Names and shapes match Polecat's and Marten's exactly, so query code ports between the
///         stores unchanged.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification =
        "Class-level: MakeGenericMethod over this class's own marker methods, which the class itself preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: LINQ expression construction requires runtime code generation in the general case.")]
public static class SoftDeletedExtensions
{
    private static readonly MethodInfo MaybeDeletedMethod =
        typeof(SoftDeletedExtensions).GetMethod(nameof(MaybeDeleted))!;

    private static readonly MethodInfo IsDeletedMethod =
        typeof(SoftDeletedExtensions).GetMethod(nameof(IsDeleted))!;

    private static readonly MethodInfo DeletedSinceMethod =
        typeof(SoftDeletedExtensions).GetMethod(nameof(DeletedSince))!;

    private static readonly MethodInfo DeletedBeforeMethod =
        typeof(SoftDeletedExtensions).GetMethod(nameof(DeletedBefore))!;

    /// <summary>
    ///     Include deleted documents as well as live ones, dropping the implicit
    ///     <c>is_deleted = 0</c> filter.
    /// </summary>
    public static IQueryable<T> MaybeDeleted<T>(this IQueryable<T> queryable)
        => Mark(queryable, MaybeDeletedMethod);

    /// <summary>
    ///     Only deleted documents.
    /// </summary>
    public static IQueryable<T> IsDeleted<T>(this IQueryable<T> queryable)
        => Mark(queryable, IsDeletedMethod);

    /// <summary>
    ///     Only documents deleted at or after <paramref name="timestamp" />.
    /// </summary>
    public static IQueryable<T> DeletedSince<T>(this IQueryable<T> queryable, DateTimeOffset timestamp)
        => Mark(queryable, DeletedSinceMethod, timestamp);

    /// <summary>
    ///     Only documents deleted before <paramref name="timestamp" />.
    /// </summary>
    public static IQueryable<T> DeletedBefore<T>(this IQueryable<T> queryable, DateTimeOffset timestamp)
        => Mark(queryable, DeletedBeforeMethod, timestamp);

    private static IQueryable<T> Mark<T>(IQueryable<T> queryable, MethodInfo method, DateTimeOffset? argument = null)
    {
        var arguments = argument.HasValue
            ? new Expression[] { queryable.Expression, Expression.Constant(argument.Value) }
            : [queryable.Expression];

        return queryable.Provider.CreateQuery<T>(
            Expression.Call(null, method.MakeGenericMethod(typeof(T)), arguments));
    }
}

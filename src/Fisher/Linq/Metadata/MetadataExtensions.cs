using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Linq.Metadata;

/// <summary>
///     Querying a document by when it was last written, rather than by anything in it (fisher#26).
/// </summary>
/// <remarks>
///     <para>
///         <c>last_modified</c> is on every document table already, written on every upsert, and holds
///         <c>SqliteTimestamp</c>'s fixed-width UTC form — chosen so a string comparison <em>is</em> an
///         instant comparison. So these translate to a plain comparison against the column with the
///         bound value rendered through the same formatter, and need none of the <c>strftime</c>
///         normalisation a document's own <see cref="DateTimeOffset" /> member needs (fisher#1). Same
///         asymmetry, and the same reason, as <c>DeletedSince</c> / <c>DeletedBefore</c>.
///     </para>
///     <para>
///         <b><c>CreatedSince</c> and <c>CreatedBefore</c> are deliberately absent.</b> Polecat has
///         them; Fisher has no <c>created_at</c> column to answer from, because the upsert writes
///         <c>last_modified</c> on both branches and nothing records first insertion. Adding the column
///         is <see href="https://github.com/JasperFx/fisher/issues/29">fisher#29</see>, and these two
///         operators are a few lines once it lands. Offering them now against
///         <c>last_modified</c> would answer a different question with a straight face.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over this class's own marker methods, which the class itself preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: LINQ expression construction requires runtime code generation in the general case.")]
public static class MetadataExtensions
{
    private static readonly MethodInfo ModifiedSinceMethod =
        typeof(MetadataExtensions).GetMethod(nameof(ModifiedSince))!;

    private static readonly MethodInfo ModifiedBeforeMethod =
        typeof(MetadataExtensions).GetMethod(nameof(ModifiedBefore))!;

    /// <summary>Documents written at or after <paramref name="timestamp" />.</summary>
    public static IQueryable<T> ModifiedSince<T>(this IQueryable<T> queryable, DateTimeOffset timestamp)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            ModifiedSinceMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
            Expression.Constant(timestamp)));

    /// <summary>Documents written before <paramref name="timestamp" />.</summary>
    public static IQueryable<T> ModifiedBefore<T>(this IQueryable<T> queryable, DateTimeOffset timestamp)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            ModifiedBeforeMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
            Expression.Constant(timestamp)));
}

/// <summary>
///     Waiting for the async daemon to catch up before the query runs (fisher#26).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher waits for the whole store, where Polecat waits for the projections feeding the
///         queried type.</b> That is stricter rather than weaker — the queried type's projections are a
///         subset — and it costs no type-to-shard map, which Fisher does not have. If it ever becomes
///         too coarse, narrowing it is a refinement rather than a correction.
///     </para>
///     <para>
///         The wait happens <em>before</em> the statement runs, so it is not SQL and does not belong in
///         a <c>Statement</c>. A store with no daemon running will simply time out, which is the honest
///         answer: nothing is going to make the data non-stale.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over this class's own marker methods, which the class itself preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: LINQ expression construction requires runtime code generation in the general case.")]
public static class NonStaleDataExtensions
{
    private static readonly MethodInfo QueryForNonStaleDataMethod =
        typeof(NonStaleDataExtensions).GetMethod(nameof(QueryForNonStaleData),
            [typeof(IQueryable<>).MakeGenericType(Type.MakeGenericMethodParameter(0)), typeof(TimeSpan)])!;

    /// <summary>
    ///     Wait for the async daemon to be caught up, then run the query.
    /// </summary>
    public static IQueryable<T> QueryForNonStaleData<T>(this IQueryable<T> queryable, TimeSpan timeout)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            QueryForNonStaleDataMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
            Expression.Constant(timeout)));

    /// <inheritdoc cref="QueryForNonStaleData{T}(IQueryable{T},TimeSpan)" />
    public static IQueryable<T> QueryForNonStaleData<T>(this IQueryable<T> queryable)
        => queryable.QueryForNonStaleData(TimeSpan.FromSeconds(5));
}

using System.Linq.Expressions;
using JasperFx.Events;

namespace Fisher.Patching;

/// <summary>
///     The <c>Increment</c> overloads over a nullable member, which Fisher declared on its own
///     <c>IPatchExpression&lt;T&gt;</c> before adopting the shared one (fisher#465).
/// </summary>
/// <remarks>
///     A null or absent member is the case the operation has to get right — <c>json_extract</c> returns SQL
///     NULL for both, and <c>NULL + n</c> is NULL — so it counts as zero. Extension methods rather than
///     interface members because <see cref="IPatchExpression{T}" /> belongs to JasperFx.Events; an instance
///     overload still wins for a non-nullable member.
/// </remarks>
public static class PatchExpressionExtensions
{
    /// <summary>Add to a nullable numeric member. An absent or null member counts as zero.</summary>
    public static IPatchExpression<T> Increment<T>(this IPatchExpression<T> patch,
        Expression<Func<T, int?>> expression, int increment = 1) where T : notnull
        => Fisher(patch).IncrementNullable(expression, increment);

    /// <inheritdoc cref="Increment{T}(IPatchExpression{T},Expression{Func{T,int?}},int)" />
    public static IPatchExpression<T> Increment<T>(this IPatchExpression<T> patch,
        Expression<Func<T, long?>> expression, long increment = 1) where T : notnull
        => Fisher(patch).IncrementNullable(expression, increment);

    /// <inheritdoc cref="Increment{T}(IPatchExpression{T},Expression{Func{T,int?}},int)" />
    public static IPatchExpression<T> Increment<T>(this IPatchExpression<T> patch,
        Expression<Func<T, double?>> expression, double increment = 1) where T : notnull
        => Fisher(patch).IncrementNullable(expression, increment);

    /// <inheritdoc cref="Increment{T}(IPatchExpression{T},Expression{Func{T,int?}},int)" />
    public static IPatchExpression<T> Increment<T>(this IPatchExpression<T> patch,
        Expression<Func<T, float?>> expression, float increment = 1) where T : notnull
        => Fisher(patch).IncrementNullable(expression, (double)increment);

    /// <inheritdoc cref="Increment{T}(IPatchExpression{T},Expression{Func{T,int?}},int)" />
    public static IPatchExpression<T> Increment<T>(this IPatchExpression<T> patch,
        Expression<Func<T, decimal?>> expression, decimal increment = 1) where T : notnull
        => Fisher(patch).IncrementNullable(expression, (double)increment);

    private static PatchExpression<T> Fisher<T>(IPatchExpression<T> patch) where T : notnull
        => patch as PatchExpression<T>
           ?? throw new NotSupportedException(
               $"The nullable Increment overloads are Fisher's, and this patch is a {patch.GetType().Name}.");
}

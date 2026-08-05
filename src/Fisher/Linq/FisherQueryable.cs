using System.Collections;
using System.Linq.Expressions;

namespace Fisher.Linq;

/// <summary>
///     The <see cref="IQueryable{T}" /> Fisher hands back from <c>session.Query&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
///     Declared <see cref="IOrderedQueryable{T}" /> rather than plain <see cref="IQueryable{T}" /> so
///     <c>ThenBy</c> is reachable — the BCL only offers it on the ordered interface, and there is no
///     separate ordered type worth having when the provider treats the chain uniformly.
/// </remarks>
public class FisherQueryable<T> : IOrderedQueryable<T>
{
    internal FisherQueryable(FisherQueryProvider provider)
    {
        Provider = provider;
        Expression = Expression.Constant(this);
    }

    internal FisherQueryable(FisherQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }

    /// <summary>
    ///     Not supported, deliberately.
    /// </summary>
    /// <remarks>
    ///     Every read Fisher does is asynchronous, and there is no non-blocking way to satisfy a
    ///     synchronous enumerator over one. Blocking on the async path inside <c>GetEnumerator</c> is
    ///     how a library deadlocks a caller's synchronization context, so it throws with the
    ///     replacement named instead. Polecat and Marten both refuse here too.
    /// </remarks>
    public IEnumerator<T> GetEnumerator()
        => throw new NotSupportedException(
            "Fisher does not support synchronous LINQ enumeration. Use ToListAsync() or one of the other "
            + "async terminal operators.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

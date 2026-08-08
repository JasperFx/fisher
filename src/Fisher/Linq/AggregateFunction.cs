namespace Fisher.Linq;

/// <summary>
///     The scalar aggregates <see cref="QueryableExtensions" /> can push into SQL (fisher#22).
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not represented in <c>LinqQueryParser</c>. Polecat builds a synthetic
///         <c>MethodCallExpression</c> carrying the selector and parses it back out; Fisher's terminal
///         extensions take the selector as an argument, so it never enters the expression tree and the
///         parser stays what its doc comment says it is — a description of the operator <em>chain</em>.
///     </para>
///     <para>
///         The two guard kinds are the whole reason this is an enum rather than a bare SQL string.
///         <c>Min</c> and <c>Max</c> are meaningful over anything that orders, including a string;
///         <c>Sum</c> and <c>Average</c> are meaningful only over a number, and SQLite will silently
///         return 0 for a <c>sum</c> over text rather than complaining.
///     </para>
/// </remarks>
internal enum AggregateFunction
{
    Sum,
    Min,
    Max,
    Average
}

internal static class AggregateFunctionExtensions
{
    /// <summary>The SQLite function name.</summary>
    public static string Sql(this AggregateFunction function)
        => function switch
        {
            AggregateFunction.Sum => "sum",
            AggregateFunction.Min => "min",
            AggregateFunction.Max => "max",
            AggregateFunction.Average => "avg",
            _ => throw new ArgumentOutOfRangeException(nameof(function))
        };

    /// <summary>
    ///     Whether the member has to be numeric, as opposed to merely order-preserving.
    /// </summary>
    public static bool RequiresANumber(this AggregateFunction function)
        => function is AggregateFunction.Sum or AggregateFunction.Average;
}

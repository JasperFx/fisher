using System.Globalization;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     The <c>bm25()</c> ordering locator — fisher#220.
/// </summary>
/// <remarks>
///     <para>
///         A locator string rather than an <c>ISqlFragment</c>, because <see cref="Statement" />'s
///         <c>OrderBys</c> are locators and that is what lets a rank travel through keyset paging,
///         <c>ReverseOverPage</c> and the aggregate wraps without any of them learning that full text
///         exists. The same reasoning that kept the #215 predicate an ordinary fragment.
///     </para>
///     <para>
///         <b>Weights are rendered, not parameterised.</b> Every other value in a Fisher query is a
///         bound parameter, so the exception needs a reason: SQLite will not accept a bound parameter
///         as a <c>bm25()</c> weight argument — the weights are part of the function's signature, not
///         data — and rendering a caller-supplied <see cref="double" /> through
///         <see cref="CultureInfo.InvariantCulture" /> is safe because the value has already been
///         through <see cref="double" /> parsing and cannot carry SQL. A weight that is not a finite
///         number is refused rather than rendered, since <c>NaN</c> and the infinities format as words
///         SQLite would read as identifiers.
///     </para>
/// </remarks>
internal static class FullTextRank
{
    public static string Locator(string alias, IReadOnlyList<double> weights)
    {
        if (weights.Count == 0)
        {
            return $"bm25({alias})";
        }

        foreach (var weight in weights)
        {
            if (double.IsNaN(weight) || double.IsInfinity(weight))
            {
                throw new BadLinqExpressionException(
                    $"'{weight}' is not a usable bm25 column weight. Weights are rendered into the "
                    + "SQL rather than bound, because SQLite does not accept a parameter as a bm25 "
                    + "argument, and NaN and the infinities have no numeric spelling there.");
            }
        }

        var rendered = string.Join(", ",
            weights.Select(w => w.ToString("R", CultureInfo.InvariantCulture)));

        return $"bm25({alias}, {rendered})";
    }
}

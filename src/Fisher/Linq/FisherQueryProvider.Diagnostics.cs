using System.Data.Common;
using System.Linq.Expressions;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq;

/// <summary>
///     The seam <c>IDocumentStoreDiagnostics.QueryDocumentsAsync</c> reads a criteria page through
///     (jasperfx#869).
/// </summary>
/// <remarks>
///     <para>
///         <b>The provider builds the statement; the diagnostics read chooses the columns.</b> Dynamic
///         LINQ text arrives as an ordinary expression tree over <c>Query&lt;T&gt;()</c>, so its members,
///         enums, decimals and dates are translated exactly as an application's own query is, and the
///         three implicit filters — tenant, soft delete, hierarchy — are the statement-level passes every
///         query gets (fisher#51). What a console needs back is not a materialized document but the
///         stored row: byte-exact <c>data</c> plus the metadata columns, which are not document members
///         and so cannot be selected through LINQ. So this hands the built <see cref="Statement" /> back
///         rather than running it, and the caller replaces the select list — the same move
///         <see cref="JsonRowsAsync{T}" /> makes for <c>ToJsonArrayAsync</c>.
///     </para>
/// </remarks>
public partial class FisherQueryProvider
{
    /// <summary>
    ///     The statement <paramref name="expression" /> would run, for a caller that selects its own
    ///     columns from the document table.
    /// </summary>
    /// <exception cref="BadLinqExpressionException">
    ///     The expression projects, groups, joins or includes — a diagnostics read returns stored rows of
    ///     one table, so none of those has a meaning here.
    /// </exception>
    internal Statement DiagnosticStatement(Expression expression)
    {
        RefuseIncludesOn(expression, "a diagnostics read");

        var (statement, parser, _, join) = BuildStatement(SourceTypeFor(expression), expression);

        if (RowProjection.For(parser) is not null || join is not null || statement.GroupBy is not null
            || statement.Subquery is not null)
        {
            throw new BadLinqExpressionException(
                "A diagnostics read returns stored documents, so it cannot follow a Select, a GroupBy, a "
                + "Distinct or a join.");
        }

        return statement;
    }

    /// <summary>Run a statement from <see cref="DiagnosticStatement" /> on this session's connection.</summary>
    internal Task<DbDataReader> ExecuteDiagnosticReaderAsync(Statement statement, CancellationToken token)
        => ExecuteReaderAsync(statement, token);

    /// <summary>Run a <c>count(*)</c> statement from <see cref="DiagnosticStatement" />.</summary>
    internal async Task<long> ExecuteDiagnosticCountAsync(Statement statement, CancellationToken token)
        => Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false));
}

using Fisher.Storage;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A caller-supplied SQL fragment composed into a <c>WHERE</c> — <c>MatchesSql</c> (fisher#202).
/// </summary>
/// <remarks>
///     <para>
///         ⚠️ <b>This is the one fragment whose SQL text comes from the caller rather than from the
///         translator.</b> Every other fragment in this folder is composed by the parser out of
///         locators it resolved and operators it chose, which is what the SQL-injection audit
///         (fisher#161/#162) rests on — see <see cref="LiteralSqlFragment" />, whose doc comment says
///         plainly that it is never built from a caller-supplied value. That invariant is not weakened
///         here so much as moved: the <em>text</em> is the caller's by contract, and every
///         <em>value</em> is still bound as a parameter.
///     </para>
///     <para>
///         <b>Values go through <see cref="SqliteParameterValue.ToDatabaseValue" />, the same
///         conversions <c>IAdvancedSql</c> applies.</b> Without them a Guid binds UPPERCASE against
///         lowercase canonical text, a <see cref="DateTimeOffset" /> binds space-separated with its
///         original offset against the fixed-width UTC form, and a <see cref="decimal" /> binds as
///         TEXT against a REAL — each matching nothing, silently. Raw SQL is the one path with no
///         conversion between what a caller holds and what Fisher wrote, which is why that class
///         exists at all.
///     </para>
///     <para>
///         <b>The fragment is parenthesized, where Marten's is not.</b> Fisher composes a statement's
///         <c>Wheres</c> with <c>and</c>, so an unbracketed <c>a = 1 or b = 2</c> would swallow every
///         term beside it — including the implicit tenant, soft-delete and hierarchy filters, which is
///         the one way this operator could turn into a cross-tenant read. The brackets cost nothing
///         and remove the question.
///     </para>
/// </remarks>
internal sealed class MatchesSqlFilter : ISqlFragment
{
    private readonly string _sql;
    private readonly char _placeholder;
    private readonly object?[] _parameters;

    internal MatchesSqlFilter(string sql, char placeholder, object?[] parameters)
    {
        _sql = sql;
        _placeholder = placeholder;
        _parameters = parameters;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append('(');

        var slots = builder.AppendWithDbParameters(_sql, _placeholder);

        // The arity check is not defensive tidiness. Too few values is an IndexOutOfRangeException
        // raised from inside the LINQ provider naming nothing the caller wrote; too many is *silence*,
        // with the surplus values never reaching the query — which is the shape marten#5289's
        // follow-up had to close after the fact. Checked here rather than at parse time because
        // Weasel owns what counts as a placeholder.
        if (slots.Length != _parameters.Length)
        {
            throw new BadLinqExpressionException(
                $"MatchesSql was given {_parameters.Length} parameter value(s) but the SQL has "
                + $"{slots.Length} '{_placeholder}' placeholder(s). Note that '{_placeholder}' counts "
                + "wherever it appears, including inside a string literal — use the overload taking a "
                + $"placeholder character if the SQL needs a literal '{_placeholder}'. SQL: {_sql}");
        }

        for (var i = 0; i < slots.Length; i++)
        {
            slots[i].Value = SqliteParameterValue.ToDatabaseValue(_parameters[i]);
        }

        builder.Append(')');
    }
}

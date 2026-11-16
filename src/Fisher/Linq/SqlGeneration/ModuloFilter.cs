using Fisher.Storage;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A modulo comparison with both operands bound as parameters —
///     <c>(locator % @p0) op @p1</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Parameterised as a security invariant, not a style choice</b> (the marten#4911 /
///         marten#4954 class). The divisor and the comparison operand are runtime values —
///         <c>WhereClauseParser.ExtractValue</c> evaluates captured locals — and the previous shape
///         interpolated their <c>ToString()</c> straight into the SQL text with no type guard, no
///         escaping, and the current culture's formatting. The expression tree's typing keeps the
///         ordinary API to numeric operands, but a user-defined <c>operator %</c> puts an arbitrary
///         <c>ToString()</c> into the statement, and even a plain <c>double</c> renders as
///         <c>1,5</c> under a comma-decimal culture — malformed SQL that depends on the server's
///         locale. Binding removes both.
///     </para>
///     <para>
///         A <see cref="decimal" /> operand is normalised to <see cref="double" /> before binding,
///         because Microsoft.Data.Sqlite binds a raw decimal as TEXT:
///         <c>json_extract</c> yields REAL for a JSON number and there is no column affinity inside an
///         expression to rescue the comparison. This filter carried its own copy of that conversion
///         until fisher#304 found <see cref="ComparisonFilter" /> beside it had never learned; both now
///         call <see cref="Storage.SqliteParameterValue.NormalizeDecimal" />.
///     </para>
/// </remarks>
internal class ModuloFilter : ISqlFragment, INegatableFragment
{
    private readonly string _locator;
    private readonly object _divisor;
    private readonly string _op;
    private readonly object _operand;
    private readonly bool _negated;

    public ModuloFilter(string locator, object divisor, string op, object operand)
        : this(locator, SqliteParameterValue.NormalizeDecimal(divisor)!, op,
            SqliteParameterValue.NormalizeDecimal(operand)!, negated: false)
    {
    }

    private ModuloFilter(string locator, object divisor, string op, object operand, bool negated)
    {
        _locator = locator;
        _divisor = divisor;
        _op = op;
        _operand = operand;
        _negated = negated;
    }

    /// <summary>A null member makes C#'s lifted <c>%</c> null, so this negates null-safe (fisher#608).</summary>
    public ISqlFragment Negate() => new ModuloFilter(_locator, _divisor, _op, _operand, !_negated);

    public void Apply(ICommandBuilder builder)
    {
        SqlNegation.ApplyNullSafe(builder, b =>
        {
            b.Append('(');
            b.Append(_locator);
            b.Append(" % ");
            b.AppendParameter(_divisor);
            b.Append(')');
        }, _op, _negated, b => b.AppendParameter(_operand));
    }
}

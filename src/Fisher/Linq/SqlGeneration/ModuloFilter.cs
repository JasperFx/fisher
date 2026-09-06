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
///         because Microsoft.Data.Sqlite binds a raw decimal as TEXT — see
///         <c>SqliteParameterValue</c>, which makes the identical conversion for raw SQL and records
///         why: <c>json_extract</c> yields REAL for a JSON number and there is no column affinity
///         inside an expression to rescue the comparison.
///     </para>
/// </remarks>
internal class ModuloFilter : ISqlFragment
{
    private readonly string _locator;
    private readonly object _divisor;
    private readonly string _op;
    private readonly object _operand;

    public ModuloFilter(string locator, object divisor, string op, object operand)
    {
        _locator = locator;
        _divisor = Normalize(divisor);
        _op = op;
        _operand = Normalize(operand);
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append('(');
        builder.Append(_locator);
        builder.Append(" % ");
        builder.AppendParameter(_divisor);
        builder.Append(") ");
        builder.Append(_op);
        builder.Append(' ');
        builder.AppendParameter(_operand);
    }

    private static object Normalize(object value)
        => value is decimal number ? (double)number : value;
}

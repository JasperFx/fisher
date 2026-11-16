using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Whether a projection's columns ARE the values it returns, and the small arithmetic subset that
///     can be moved into the select list when they are not (fisher#590).
/// </summary>
/// <remarks>
///     <para>
///         A projection reads columns and computes its result in .NET. That is invisible until an operator
///         acts on the columns as though they were the result: <c>Distinct()</c> renders
///         <c>select distinct &lt;columns&gt;</c> and identity <c>OrderBy</c> orders by the one column, so
///         <c>Select(x =&gt; x.Number % 3).Distinct()</c> deduplicated <c>Number</c> and returned five rows
///         where there were three values. <see cref="IsFaithful" /> is the test those operators need.
///     </para>
///     <para>
///         <b>The translation is deliberately narrow, because each piece of it was checked against .NET
///         rather than assumed.</b> Integer <c>%</c> and <c>/</c> by a constant other than 0 and -1:
///         SQLite and C# both truncate toward zero and give a remainder the dividend's sign, and those two
///         divisors are the only ones where the two disagree (an exception in .NET, NULL or a REAL in
///         SQLite). Unary minus on an <c>int</c> or <c>long</c>, guarded at the type's minimum, where
///         unchecked .NET wraps and SQLite widens. Everything else — <c>*</c>, <c>+</c>, <c>-</c> between
///         values, floating point, decimals, enums, strings — is refused by the caller rather than
///         translated, because overflow, rounding or stored form would make the SQL answer a different
///         question.
///     </para>
///     <para>
///         A non-nullable leaf is wrapped in <c>coalesce(…, 0)</c>, because that is what the projected read
///         already turns a NULL column into (an absent key reads as the type's default), so the computed
///         column and the .NET computation start from the same value. A nullable leaf is left alone: NULL
///         propagates through all three operators in both.
///     </para>
/// </remarks>
internal static class ComputedColumns
{
    /// <summary>
    ///     Whether <paramref name="body" /> — a projection body after its columns were replaced by reads
    ///     from <paramref name="values" /> — returns the columns themselves, so an operator over the
    ///     columns answers the same question as one over the results.
    /// </summary>
    /// <remarks>
    ///     A column read, a conversion that cannot merge or reorder values, and a <c>new</c> whose every
    ///     argument is one of those or a constant. Anything else computes.
    /// </remarks>
    public static bool IsFaithful(Expression body, ParameterExpression values)
    {
        switch (body)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert, Operand: BinaryExpression
            {
                NodeType: ExpressionType.ArrayIndex
            } index } when index.Left == values:
                return true;

            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return IsLossless(convert.Operand.Type, convert.Type) && IsFaithful(convert.Operand, values);

            case NewExpression @new:
                return @new.Arguments.All(x => IsFaithful(x, values) || IsConstant(x));

            case MemberInitExpression init:
                return IsFaithful(init.NewExpression, values)
                       && init.Bindings.All(x => x is MemberAssignment assignment
                                                 && (IsFaithful(assignment.Expression, values)
                                                     || IsConstant(assignment.Expression)));

            default:
                return false;
        }
    }

    /// <summary>
    ///     The SQL for an integer <c>%</c>, <c>/</c> or unary minus over translatable operands, else false.
    /// </summary>
    /// <param name="node">The arithmetic expression.</param>
    /// <param name="leaf">
    ///     The SQL for an operand that is a column on its own — a document member, or the group's key or
    ///     an aggregate — or null when it is not one.
    /// </param>
    public static bool TryTranslate(Expression node, Func<Expression, string?> leaf, out string sql)
    {
        sql = "";

        if (node.NodeType is not (ExpressionType.Modulo or ExpressionType.Divide or ExpressionType.Negate))
        {
            return false;
        }

        var translated = Operand(node, leaf);
        if (translated is null)
        {
            return false;
        }

        sql = translated;
        return true;
    }

    private static string? Operand(Expression node, Func<Expression, string?> leaf)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.Modulo or ExpressionType.Divide } binary
                when IsSignedInteger(binary.Type):
            {
                var left = Operand(binary.Left, leaf);
                var divisor = ConstantDivisor(binary.Right);

                if (left is null || divisor is null)
                {
                    return null;
                }

                var op = binary.NodeType == ExpressionType.Modulo ? "%" : "/";
                return $"({left} {op} {divisor.Value.ToString(CultureInfo.InvariantCulture)})";
            }

            // Not NegateChecked: a checked context throws at the minimum, which SQL cannot reproduce.
            case UnaryExpression { NodeType: ExpressionType.Negate } negate when IsSignedInteger(negate.Type):
            {
                var operand = Operand(negate.Operand, leaf);
                if (operand is null)
                {
                    return null;
                }

                // Unchecked .NET wraps the minimum back onto itself; SQLite would widen it to a value the
                // type cannot hold (int) or to a REAL (long). The guard keeps the wrap.
                var minimum = Underlying(negate.Type) == typeof(int)
                    ? "-2147483648"
                    : "(-9223372036854775807 - 1)";

                return $"(case when {operand} = {minimum} then {operand} else -({operand}) end)";
            }

            // The compiler's own widening (short to int, int to long) and lifting (int to int?). Neither
            // changes a value, so the operand's SQL stands for both.
            case UnaryExpression { NodeType: ExpressionType.Convert } convert
                when IsSignedInteger(convert.Type) && IsIntegralLeafType(convert.Operand.Type)
                                                  && IsLossless(convert.Operand.Type, convert.Type):
                return Operand(convert.Operand, leaf);
        }

        if (!IsIntegralLeafType(node.Type) || leaf(node) is not { } column)
        {
            return null;
        }

        return Nullable.GetUnderlyingType(node.Type) is null ? $"coalesce({column}, 0)" : column;
    }

    /// <summary>
    ///     A divisor that is a value rather than a column, and one for which SQLite and .NET agree.
    /// </summary>
    private static long? ConstantDivisor(Expression expression)
    {
        if (ParameterFinder.References(expression))
        {
            return null;
        }

        var value = WhereClauseParser.ExtractValue(expression);

        long? divisor = value switch
        {
            int i => i,
            long l => l,
            short s => s,
            sbyte b => b,
            byte b => b,
            ushort u => u,
            uint u => u,
            _ => null
        };

        // 0 throws in .NET and is NULL in SQLite; -1 throws at the minimum in .NET (OverflowException)
        // and widens in SQLite.
        return divisor is 0 or -1 ? null : divisor;
    }

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>The result types C# integer arithmetic produces that this translation handles.</summary>
    private static bool IsSignedInteger(Type type) => Underlying(type) == typeof(int) || Underlying(type) == typeof(long);

    /// <summary>
    ///     Integral CLR types whose stored form is a JSON integer. Not an enum (it may be stored as its
    ///     name), not <c>ulong</c> (beyond <c>long</c> it is a REAL in SQLite), not <c>char</c>.
    /// </summary>
    private static bool IsIntegralLeafType(Type type)
    {
        var inner = Underlying(type);

        return !inner.IsEnum && Type.GetTypeCode(inner) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16
            or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64;
    }

    /// <summary>
    ///     A conversion that can neither merge two values nor reorder them: identity, lifting, boxing, a
    ///     reference upcast, or an exact numeric widening.
    /// </summary>
    internal static bool IsLossless(Type from, Type to)
    {
        if (from == to || to == typeof(object))
        {
            return true;
        }

        var fromInner = Nullable.GetUnderlyingType(from);
        var toInner = Nullable.GetUnderlyingType(to);

        if (fromInner is not null)
        {
            // int? to int is .Value, which throws on null rather than converting it.
            if (toInner is null)
            {
                return false;
            }

            from = fromInner;
        }

        to = toInner ?? to;

        if (from == to)
        {
            return true;
        }

        if (!from.IsValueType && !to.IsValueType && to.IsAssignableFrom(from))
        {
            return true;
        }

        return !from.IsEnum && !to.IsEnum && Widenings.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>Numeric conversions that are exact for every value of the source type.</summary>
    private static readonly Dictionary<Type, Type[]> Widenings = new()
    {
        [typeof(sbyte)] = [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)],
        [typeof(byte)] =
        [
            typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float),
            typeof(double), typeof(decimal)
        ],
        [typeof(short)] = [typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)],
        [typeof(ushort)] =
            [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)],
        [typeof(int)] = [typeof(long), typeof(double), typeof(decimal)],
        [typeof(uint)] = [typeof(long), typeof(ulong), typeof(double), typeof(decimal)],
        [typeof(long)] = [typeof(decimal)],
        [typeof(ulong)] = [typeof(decimal)],
        [typeof(float)] = [typeof(double)]
    };

    /// <summary>A literal, or a value captured from the caller's scope — the same for every row.</summary>
    /// <remarks>
    ///     Fields only: a captured local is a field of the closure, where a property (<c>DateTime.Now</c>)
    ///     may answer differently for each row.
    /// </remarks>
    private static bool IsConstant(Expression expression) => expression switch
    {
        ConstantExpression => true,
        MemberExpression { Member: FieldInfo, Expression: null } => true,
        MemberExpression { Member: FieldInfo } member => IsConstant(member.Expression!),
        UnaryExpression { NodeType: ExpressionType.Convert } convert => IsConstant(convert.Operand),
        _ => false
    };

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private bool _found;

        public static bool References(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found = true;
            return node;
        }
    }
}

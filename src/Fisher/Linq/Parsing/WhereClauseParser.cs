using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing.Methods;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Turns a predicate expression tree into a WHERE fragment.
/// </summary>
/// <remarks>
///     <para>
///         Ported from Polecat's parser of the same name. The structure is deliberately unchanged —
///         same node dispatch, same reversed-operand handling, same closure-value extraction — because
///         nothing about walking an expression tree is dialect-specific and the two should stay
///         comparable.
///     </para>
///     <para>
///         What is new is the range guard. Some members are stored in a form that is correct for
///         equality but meaningless when ordered, and rather than emit a predicate that returns
///         plausible-but-wrong rows the parser refuses. See <see cref="DateMember" />.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: evaluates constant sub-expressions in predicates via Expression.Lambda + Compile — runtime code generation. Document types and their property graph are preserved at the LINQ registration boundary on the caller side per the AOT publishing guide.")]
internal class WhereClauseParser
{
    private static readonly Dictionary<ExpressionType, string> Operators = new()
    {
        { ExpressionType.Equal, "=" },
        { ExpressionType.NotEqual, "!=" },
        { ExpressionType.GreaterThan, ">" },
        { ExpressionType.GreaterThanOrEqual, ">=" },
        { ExpressionType.LessThan, "<" },
        { ExpressionType.LessThanOrEqual, "<=" }
    };

    private static readonly HashSet<string> RangeOperators = [">", ">=", "<", "<="];

    private readonly IMemberResolver _memberFactory;
    private readonly IReadOnlyList<IMethodCallParser>? _additionalParsers;

    public WhereClauseParser(IMemberResolver memberFactory,
        IReadOnlyList<IMethodCallParser>? additionalParsers = null)
    {
        _memberFactory = memberFactory;
        _additionalParsers = additionalParsers;
    }

    public ISqlFragment Parse(Expression expression)
        => expression switch
        {
            BinaryExpression binary => ParseBinary(binary),
            UnaryExpression { NodeType: ExpressionType.Not } unary => ParseNot(unary),
            UnaryExpression { NodeType: ExpressionType.Convert } unary => Parse(unary.Operand),
            MethodCallExpression methodCall => ParseMethodCall(methodCall),
            MemberExpression member when IsBooleanMember(member) => ParseBooleanMember(member, true),
            ConstantExpression { Value: bool boolValue } => new WhereFragment(boolValue ? "1=1" : "1=0"),
            TypeBinaryExpression { NodeType: ExpressionType.TypeIs } typeIs => ParseTypeIs(typeIs),
            _ => throw new BadLinqExpressionException(
                $"Unsupported expression in a where clause: {expression.NodeType} ({expression.GetType().Name}). "
                + Alternatives)
        };

    /// <summary>
    ///     The ways forward, named in every generic refusal (fisher#399, marten#5481).
    /// </summary>
    /// <remarks>
    ///     A refusal that names only what failed leaves the caller to guess whether anything would
    ///     work. These three always do: <c>MatchesSql</c> composes a raw fragment into the same
    ///     statement, so the implicit tenant, soft-delete and hierarchy filters still apply;
    ///     <c>AdvancedSql</c> is the whole statement in the caller's hands; and filtering in memory is
    ///     correct for any predicate at the cost of reading the rows.
    /// </remarks>
    internal const string Alternatives =
        "To express it anyway, use MatchesSql(\"<sql fragment>\", values) for a raw SQL condition, "
        + "query with session.AdvancedSql.QueryAsync(...), or materialize the results and filter them in memory.";

    private ISqlFragment ParseMethodCall(MethodCallExpression expression)
    {
        if (_additionalParsers != null)
        {
            foreach (var additional in _additionalParsers)
            {
                if (additional.Matches(expression))
                {
                    return additional.Parse(_memberFactory, expression);
                }
            }
        }

        var parser = MethodCallParserRegistry.FindParser(expression)
                     ?? throw new BadLinqExpressionException(
                         $"Unsupported method call in a where clause: "
                         + $"{expression.Method.DeclaringType?.Name}.{expression.Method.Name}. " + Alternatives);

        return parser.Parse(_memberFactory, expression);
    }

    private ISqlFragment ParseBinary(BinaryExpression binary)
    {
        if (binary.NodeType == ExpressionType.AndAlso)
        {
            return new CompoundWhereFragment("and", Parse(binary.Left), Parse(binary.Right));
        }

        if (binary.NodeType == ExpressionType.OrElse)
        {
            return new CompoundWhereFragment("or", Parse(binary.Left), Parse(binary.Right));
        }

        if (Operators.TryGetValue(binary.NodeType, out var op))
        {
            return ParseComparison(binary, op);
        }

        throw new BadLinqExpressionException(
            $"Unsupported binary operator in a where clause: {binary.NodeType}. " + Alternatives);
    }

    /// <summary>
    ///     <c>x.Payment.Value is Card</c>: the only type test an expression tree can carry over a union,
    ///     because C# refuses a union pattern there (fisher#466).
    /// </summary>
    private ISqlFragment ParseTypeIs(TypeBinaryExpression typeIs)
    {
        if (Members.UnionMemberAccess.TryMatchValueAccess(typeIs.Expression, out var unionMember)
            && IsDocumentMember(unionMember))
        {
            return _memberFactory.ResolveUnionTypeTest(unionMember, typeIs.TypeOperand);
        }

        throw new BadLinqExpressionException(
            $"Cannot translate the type test '{typeIs}'. A type test is supported only against the Value of "
            + "a union-typed member, as in 'x.Payment.Value is Card'; any other stored JSON carries nothing "
            + "to test a CLR type against. " + Alternatives);
    }

    /// <summary>
    ///     <c>x.Payment.Value == null</c>: the default union, or no value at all (fisher#466).
    /// </summary>
    private bool TryParseUnionNullTest(Expression memberSide, Expression valueSide, string op,
        out ISqlFragment? fragment)
    {
        fragment = null;

        if (op is not ("=" or "!=")
            || !Members.UnionMemberAccess.TryMatchValueAccess(StripConvert(memberSide), out var unionMember)
            || !IsDocumentMember(unionMember)
            || StripConvert(valueSide) is not ConstantExpression { Value: null })
        {
            return false;
        }

        var isNull = _memberFactory.ResolveUnionTypeTest(unionMember, null);
        fragment = op == "=" ? isNull : new NotFragment(isNull);
        return true;
    }

    private ISqlFragment ParseComparison(BinaryExpression binary, string op)
    {
        // x.A < x.B: both sides read the row, so neither is a value to bind (fisher#595).
        if (ReferencesFreeParameter(binary.Left) && ReferencesFreeParameter(binary.Right))
        {
            return ParseMemberToMember(binary, op);
        }

        if (TryParseUnionNullTest(binary.Left, binary.Right, op, out var unionNull)
            || TryParseUnionNullTest(binary.Right, binary.Left, op, out unionNull))
        {
            return unionNull!;
        }

        // x.Number % 2 == 0
        if (TryParseModulo(binary, op, out var moduloFragment))
        {
            return moduloFragment!;
        }

        // x.Price - 0.1m < 0.2m: decimal arithmetic is folded in .NET, never run in SQLite (fisher#603)
        if (TryFoldDecimalArithmetic(binary.Left, binary.Right, op, out var foldedFragment)
            || TryFoldDecimalArithmetic(binary.Right, binary.Left, ReverseOperator(op), out foldedFragment))
        {
            return foldedFragment!;
        }

        // x.Version - x.CompactedVersion > 3, either way round
        if (TryParseArithmetic(binary.Left, binary.Right, op, out var arithmeticFragment)
            || TryParseArithmetic(binary.Right, binary.Left, ReverseOperator(op), out arithmeticFragment))
        {
            return arithmeticFragment!;
        }

        // x.Name.ToLower() == "value", either way round
        if (TryParseMethodTransform(binary.Left, binary.Right, op, out var transformFragment)
            || TryParseMethodTransform(binary.Right, binary.Left, ReverseOperator(op), out transformFragment))
        {
            return transformFragment!;
        }

        // x.Name.Length == 5, either way round
        if (TryParseLength(binary.Left, binary.Right, op, out var lengthFragment)
            || TryParseLength(binary.Right, binary.Left, ReverseOperator(op), out lengthFragment))
        {
            return lengthFragment!;
        }

        // x.Tags.Count() > 2 / x.Tags.Count > 2 / x.Depths.Length > 2, either way round
        if (TryParseCollectionCount(binary.Left, binary.Right, op, out var countFragment)
            || TryParseCollectionCount(binary.Right, binary.Left, ReverseOperator(op), out countFragment))
        {
            return countFragment!;
        }

        if (TryResolveMemberAndValue(binary.Left, binary.Right, out var member, out var value))
        {
            return BuildComparisonFilter(member!, value, op);
        }

        if (TryResolveMemberAndValue(binary.Right, binary.Left, out member, out value))
        {
            return BuildComparisonFilter(member!, value, ReverseOperator(op));
        }

        throw new BadLinqExpressionException($"Cannot translate the comparison '{binary}' to SQL. " + Alternatives);
    }

    /// <summary>
    ///     <c>x.A &lt; x.B</c>, <c>x.A == x.B</c>, <c>x.A - 1 &lt; x.B</c>: a comparison whose two sides
    ///     both read the row, rendered as two locators (fisher#595).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each side is a document member's <see cref="IQueryableMember.TypedLocator" />, so a
    ///         timestamp compares through <c>strftime</c> and an integer-stored enum as a number, exactly
    ///         as against a constant; or an Add/Subtract over integral or <c>double</c> members, through
    ///         the same rendering <see cref="TryParseArithmetic" /> uses. Anything else is refused by
    ///         name, never compiled: compiling one side as a value is the internal exception this
    ///         replaced.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>Equality is <c>is</c> / <c>is not</c>, never <c>=</c> / <c>!=</c>.</b> C#'s
    ///         <c>x.A == x.B</c> is true when both are null, and SQL's <c>=</c> is NULL there, so the
    ///         row would be dropped. A range comparison is wrapped in <c>coalesce(…, 0)</c> for the
    ///         other direction: C# says <c>!(x.A &lt; x.B)</c> is true when either is null, and
    ///         <c>not (NULL)</c> is NULL.
    ///     </para>
    ///     <para>
    ///         Refused rather than guessed: sides of different kinds (SQLite would compare them by its
    ///         own cross-type rules, where every number sorts below every string), a range over a member
    ///         whose stored form does not order (<see cref="AssertRangeIsMeaningful" />'s rule), a
    ///         conversion that changes the value (<c>(int)x.Price</c> truncates in C# and not in SQL),
    ///         and <c>decimal</c> or <c>float</c> arithmetic, which SQLite would run in <c>double</c>.
    ///     </para>
    /// </remarks>
    private ISqlFragment ParseMemberToMember(BinaryExpression binary, string op)
    {
        var left = ComparisonOperand(binary.Left, binary);
        var right = ComparisonOperand(binary.Right, binary);

        if (!(left.Numeric && right.Numeric) && left.Type != right.Type)
        {
            throw RefuseMemberComparison(binary,
                $"'{binary.Left}' is {left.Describe()} while '{binary.Right}' is {right.Describe()}. SQLite "
                + "would compare the two by its own cross-type rules rather than C#'s");
        }

        if (RangeOperators.Contains(op))
        {
            foreach (var side in new[] { left, right })
            {
                if (!side.Orders)
                {
                    throw RefuseMemberComparison(binary,
                        $"{side.Describe()} does not order in SQLite: its stored form is not order-preserving, so "
                        + "a range comparison would return plausible but wrong rows. Equality is supported");
                }
            }

            return new WhereFragment($"coalesce({left.Sql} {op} {right.Sql}, 0)");
        }

        return new WhereFragment($"{left.Sql} {(op == "=" ? "is" : "is not")} {right.Sql}");
    }

    /// <summary>One side of a member-to-member comparison.</summary>
    /// <param name="Type">The non-nullable type the side compares as; meaningless when numeric.</param>
    private readonly record struct ComparisonSide(string Sql, Type Type, bool Numeric, bool Orders)
    {
        public string Describe() => Numeric ? "a number" : $"a {Type.Name}";
    }

    private ComparisonSide ComparisonOperand(Expression side, BinaryExpression binary)
    {
        AssertConversionsKeepTheValue(side, binary);

        if (StripConvert(side) is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract } arithmetic)
        {
            var resultType = Nullable.GetUnderlyingType(arithmetic.Type) ?? arithmetic.Type;
            if (resultType == typeof(decimal) || resultType == typeof(float))
            {
                throw RefuseMemberComparison(binary,
                    $"'{arithmetic}' is {resultType.Name} arithmetic, which SQLite would compute in double "
                    + "precision and so round differently than C# does");
            }

            AssertConversionsKeepTheValue(arithmetic.Left, binary);
            AssertConversionsKeepTheValue(arithmetic.Right, binary);

            if (TryRenderArithmetic(arithmetic, out var sql, out _))
            {
                return new ComparisonSide(sql, typeof(double), Numeric: true, Orders: true);
            }

            throw RefuseMemberComparison(binary,
                $"'{arithmetic}' is not an Add or Subtract over numeric document members and constants");
        }

        if (!TryResolveDocumentMember(side, out var member))
        {
            throw RefuseMemberComparison(binary,
                $"'{side}' is not a document member. Only a member, or an Add or Subtract over numeric members, "
                + "can be compared with another member");
        }

        // A registered strong-typed wrapper is stored as its primitive (fisher#356).
        var classified = member! is ValueTypeMember wrapper ? wrapper.Inner : member!;
        var type = Nullable.GetUnderlyingType(classified.MemberType) ?? classified.MemberType;

        if (IsNumeric(type))
        {
            return new ComparisonSide(member!.TypedLocator, type, Numeric: true, Orders: true);
        }

        if (type.IsEnum)
        {
            // An integer-stored enum is a JSON number; a string-stored one is its name, which neither
            // orders nor equals a number (EnumMember.AllowsRangeComparison).
            return classified.AllowsRangeComparison
                ? new ComparisonSide(member!.TypedLocator, type, Numeric: true, Orders: true)
                : new ComparisonSide(member!.TypedLocator, type, Numeric: false, Orders: false);
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(bool) || type == typeof(Guid))
        {
            return new ComparisonSide(member!.TypedLocator, type, Numeric: false, Orders: false);
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(DateOnly)
            || type == typeof(TimeOnly))
        {
            return new ComparisonSide(member!.TypedLocator, type, Numeric: false, Orders: true);
        }

        throw RefuseMemberComparison(binary,
            $"'{side}' is a {type.Name}, which has no single stored value Fisher can compare with another member");
    }

    /// <summary>
    ///     Refuses a conversion on a side of a member-to-member comparison that changes the value, where
    ///     C# and SQLite would then disagree: <c>(int)x.Price</c> truncates in C# and not in SQL, and a
    ///     cast to <c>object</c> makes <c>==</c> a reference comparison.
    /// </summary>
    private static void AssertConversionsKeepTheValue(Expression side, BinaryExpression binary)
    {
        while (side is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            var source = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
            var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;

            if (!KeepsTheValue(source, target)
                && !Members.UnionMemberAccess.TryMatchCaseConversion(convert, out _, out _))
            {
                throw RefuseMemberComparison(binary,
                    $"'{convert}' converts {source.Name} to {target.Name}, which C# and SQLite would not compare "
                    + "the same way");
            }

            side = convert.Operand;
        }

        if (side is UnaryExpression { NodeType: ExpressionType.TypeAs } typeAs)
        {
            throw RefuseMemberComparison(binary, $"'{typeAs}' is a type conversion with no SQL equivalent");
        }
    }

    private static bool KeepsTheValue(Type source, Type target)
    {
        if (source == target)
        {
            return true;
        }

        if (source.IsEnum)
        {
            return IntegralRank(target) >= IntegralRank(Enum.GetUnderlyingType(source)) && IntegralRank(target) > 0;
        }

        var sourceRank = IntegralRank(source);
        if (sourceRank > 0)
        {
            // Integral to a wider integral, to double or to decimal is exact; to float is not.
            return IntegralRank(target) >= sourceRank || target == typeof(double) || target == typeof(decimal);
        }

        // decimal and double are both compared as REAL already, so moving between them changes nothing
        // SQLite sees. A float is not: C# compares it in single precision.
        return (source == typeof(decimal) && target == typeof(double))
               || (source == typeof(double) && target == typeof(decimal));
    }

    private static int IntegralRank(Type type)
        => type == typeof(byte) || type == typeof(sbyte) ? 1
            : type == typeof(short) || type == typeof(ushort) ? 2
            : type == typeof(int) || type == typeof(uint) ? 3
            : type == typeof(long) ? 4
            : 0;

    private static BadLinqExpressionException RefuseMemberComparison(BinaryExpression binary, string reason)
        => new($"Cannot translate the comparison '{binary}' to SQL: both sides read the document, and {reason}. "
               + Alternatives);

    private bool TryParseMethodTransform(Expression methodSide, Expression valueSide, string op,
        out ISqlFragment? fragment)
    {
        fragment = null;

        if (StripConvert(methodSide) is not MethodCallExpression methodCall)
        {
            return false;
        }

        var parser = MethodCallParserRegistry.FindParser(methodCall);
        if (parser == null)
        {
            return false;
        }

        if (parser.Parse(_memberFactory, methodCall) is not SqlFunctionLocator funcLocator)
        {
            return false;
        }

        var value = ExtractValue(valueSide);
        fragment = value == null
            ? new WhereFragment($"{funcLocator.FullLocator} is {(op == "=" ? "null" : "not null")}")
            : new ComparisonFilter(funcLocator.FullLocator, op, value);

        return true;
    }

    /// <summary>
    ///     <c>x.Name.Length == 5</c> → <c>length(locator) = @p0</c>. SQL Server spells the function
    ///     <c>LEN</c>, which also ignores trailing spaces; SQLite's <c>length</c> does not, and matches
    ///     <see cref="string.Length" /> exactly.
    /// </summary>
    private bool TryParseLength(Expression memberSide, Expression valueSide, string op, out ISqlFragment? fragment)
    {
        fragment = null;

        if (StripConvert(memberSide) is not MemberExpression { Member.Name: "Length" } lengthExpr)
        {
            return false;
        }

        if (lengthExpr.Expression is not MemberExpression innerMember
            || !IsDocumentMember(innerMember)
            || innerMember.Type != typeof(string))
        {
            return false;
        }

        var member = _memberFactory.ResolveMember(innerMember);
        fragment = new FunctionComparisonFilter("length", member.RawLocator, op, ExtractValue(valueSide)!);
        return true;
    }

    /// <summary>
    ///     <c>x.Version - x.CompactedVersion &gt; 3</c> → <c>(version - compacted_version) &gt; @p0</c>:
    ///     an Add/Subtract over numeric members (or a numeric member and a numeric constant), compared
    ///     to a value. The jasperfx#740 compaction-policy selector is exactly this shape — un-compacted
    ///     growth is a difference of two columns, and shipping both to the client to subtract there
    ///     would stop the predicate composing with everything else in the WHERE.
    /// </summary>
    /// <remarks>
    ///     Numeric members only, deliberately: SQLite's <c>-</c> over the ISO-8601 TEXT timestamps
    ///     coerces the text to a number at the first non-numeric character, so a timestamp difference
    ///     would "work" and be numerically meaningless — refusing is the honest answer, per the same
    ///     rule as <see cref="AssertRangeIsMeaningful" />.
    /// </remarks>
    private bool TryParseArithmetic(Expression arithmeticSide, Expression valueSide, string op,
        out ISqlFragment? fragment)
    {
        fragment = null;

        if (!TryRenderArithmetic(arithmeticSide, out var rendered, out _))
        {
            return false;
        }

        fragment = new ComparisonFilter(rendered, op, ExtractValue(valueSide)!);
        return true;
    }

    /// <summary>
    ///     An Add/Subtract over numeric document members and numeric constants, rendered as
    ///     <c>(left op right)</c>. Shared by <see cref="TryParseArithmetic" /> and the member-to-member
    ///     comparison (fisher#595), so the two agree about which arithmetic translates.
    /// </summary>
    /// <param name="nullable">Whether a member operand can be null, which makes the result null too.</param>
    private bool TryRenderArithmetic(Expression arithmeticSide, out string rendered, out bool nullable)
    {
        rendered = string.Empty;
        nullable = false;

        if (StripConvert(arithmeticSide) is not BinaryExpression
            {
                NodeType: ExpressionType.Add or ExpressionType.Subtract
            } arithmetic)
        {
            return false;
        }

        // fisher#603: SQLite has no decimal or single-precision arithmetic, only double, so either would
        // round differently than C#. decimal against constants was folded before this point
        // (TryFoldDecimalArithmetic); what reaches here is a shape folding cannot express.
        var resultType = Nullable.GetUnderlyingType(arithmetic.Type) ?? arithmetic.Type;
        if (resultType == typeof(decimal) || resultType == typeof(float))
        {
            throw RefuseArithmetic(arithmetic,
                $"it is {resultType.Name} arithmetic, which SQLite would compute in double precision and so "
                + "round differently than C# does. Only a decimal member plus or minus constants is folded "
                + "into a single constant");
        }

        // At least one operand must be a document member, or this is a constant expression the value
        // side's ExtractValue would have folded already. fisher#591: a document member, not merely a
        // member. `Statics.Value - 4` is a member expression too, and accepting it here sent the other
        // side (`x.Number`) to ExtractValue, which cannot compile a lambda parameter.
        var renderedMember = false;
        var anyNullable = false;

        if (!TryRenderOperand(arithmetic.Left, out var left)
            || !TryRenderOperand(arithmetic.Right, out var right)
            || !renderedMember)
        {
            return false;
        }

        var sqlOperator = arithmetic.NodeType == ExpressionType.Add ? "+" : "-";

        rendered = $"({left} {sqlOperator} {right})";
        nullable = anyNullable;
        return true;

        bool TryRenderOperand(Expression operand, out string operandSql)
        {
            operandSql = string.Empty;

            if (StripConvert(operand) is MemberExpression member && IsDocumentMember(member))
            {
                // The numeric check runs on the expression's own type BEFORE resolving: string
                // concatenation is also ExpressionType.Add, and asking the member factory to resolve
                // a member this parse is about to decline anyway lets the factory's own failure
                // preempt the "cannot translate" refusal the caller should see.
                if (!IsNumeric(member.Type))
                {
                    return false;
                }

                // A float is stored as its shortest round-trip text, which reads back as a double that
                // is not the double C# widens the float to: 0.3f is 0.3 in SQLite and
                // 0.30000001192092896 in C# (fisher#603).
                if ((Nullable.GetUnderlyingType(member.Type) ?? member.Type) == typeof(float))
                {
                    throw RefuseArithmetic(arithmetic,
                        $"'{member}' is a float, which SQLite reads back as a different double than the one "
                        + "C# widens it to");
                }

                operandSql = _memberFactory.ResolveMember(member).TypedLocator;
                renderedMember = true;
                anyNullable |= Nullable.GetUnderlyingType(member.Type) != null;
                return true;
            }

            // Only a parameter-free operand can be evaluated to a constant — ExtractValue compiles
            // the expression, and a lambda parameter inside it (a computed member of a join result,
            // say) is not a value to fold but a shape this parse must decline.
            // EvaluateValue keeps the operand's conversions, so a float constant widened to double is
            // rendered as the double C# computes with rather than as the float's shorter text.
            if (!ReferencesFreeParameter(operand)
                && EvaluateValue(operand) is { } value && IsNumeric(value.GetType()))
            {
                operandSql = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                return true;
            }

            return false;
        }
    }

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(int) || type == typeof(long) || type == typeof(short)
               || type == typeof(byte) || type == typeof(double) || type == typeof(float)
               || type == typeof(decimal);
    }

    /// <summary>
    ///     Whether an expression reads a lambda parameter it does not bind itself: the row, so it cannot
    ///     be folded to a value. A parameter of a lambda nested inside it (<c>list.Count(t =&gt; t &gt; 2)</c>)
    ///     is bound there and does not count.
    /// </summary>
    internal static bool ReferencesFreeParameter(Expression expression)
    {
        var finder = new ParameterFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    /// <summary>
    ///     Finds a lambda parameter the expression does not bind itself — the test for "cannot be folded
    ///     to a constant".
    /// </summary>
    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly HashSet<ParameterExpression> _bound = [];

        internal bool Found { get; private set; }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            foreach (var parameter in node.Parameters) _bound.Add(parameter);
            return base.VisitLambda(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!_bound.Contains(node))
            {
                Found = true;
            }

            return node;
        }
    }

    /// <summary>
    ///     <c>x.Tags.Count() &gt; 2</c> — also the <c>List.Count</c> property, an array's
    ///     <c>Length</c>, and the filtered <c>Count(c =&gt; …)</c> overload — compared as a
    ///     <c>count(*)</c> sub-query over <c>json_each</c>.
    /// </summary>
    /// <remarks>
    ///     An absent member and one stored as JSON null both count as zero — the same "the key is not
    ///     there is an honest empty" reading <c>IsEmpty()</c> documents — where in-memory LINQ over a
    ///     null collection would throw. String <c>Length</c> is not claimed here (the element-type test
    ///     rejects strings), so it keeps its <c>length()</c> translation.
    /// </remarks>
    private bool TryParseCollectionCount(Expression memberSide, Expression valueSide, string op,
        out ISqlFragment? fragment)
    {
        fragment = null;

        var stripped = StripConvert(memberSide);
        Expression? collectionExpr = null;
        LambdaExpression? filter = null;

        if (stripped is MethodCallExpression { Method.Name: "Count", Object: null } call
            && call.Method.DeclaringType == typeof(Enumerable)
            && call.Arguments.Count is 1 or 2)
        {
            collectionExpr = call.Arguments[0];
            filter = call.Arguments.Count == 2 ? Methods.ChildCollections.LambdaOf(call.Arguments[1]) : null;
        }
        else if (stripped is MemberExpression { Member.Name: "Count" or "Length" } property
                 && property.Expression is MemberExpression inner)
        {
            collectionExpr = inner;
        }
        // An array's Length is not a member access in an expression tree — the compiler emits a
        // dedicated ArrayLength unary node.
        else if (stripped is UnaryExpression { NodeType: ExpressionType.ArrayLength } arrayLength)
        {
            collectionExpr = arrayLength.Operand;
        }

        if (collectionExpr == null
            || !Methods.ChildCollections.IsCollectionDocumentMember(collectionExpr))
        {
            return false;
        }

        var collection = Methods.ChildCollections.ResolveCollection(_memberFactory, collectionExpr, "Count");

        var where = filter == null
            ? null
            : Methods.ChildCollections.ParseElementPredicate(collection, filter, "Count");

        fragment = new CollectionCountFilter(collection.JsonEachSource, collection.Alias, where, op,
            ExtractValue(valueSide)!);
        return true;
    }

    /// <summary>
    ///     <c>x.Number % 2 == 0</c> → <c>(locator % @p0) = @p1</c>, both operands bound as
    ///     parameters.
    /// </summary>
    /// <remarks>
    ///     The divisor and the comparison operand used to be interpolated into the SQL text as
    ///     <c>ToString()</c> — unparameterised runtime values, the marten#4911/#4954 class. See
    ///     <see cref="ModuloFilter" /> for why binding is a security invariant here. A null operand
    ///     declines rather than rendering, so the caller's "cannot translate" refusal names the
    ///     expression instead of SQLite reporting malformed SQL.
    /// </remarks>
    private bool TryParseModulo(BinaryExpression binary, string op, out ISqlFragment? fragment)
    {
        fragment = null;

        if (binary.Left is BinaryExpression { NodeType: ExpressionType.Modulo } modulo
            && StripConvert(modulo.Left) is MemberExpression left && IsDocumentMember(left))
        {
            AssertModuloIsIntegral(modulo);

            if (ExtractValue(modulo.Right) is not { } divisor || ExtractValue(binary.Right) is not { } operand)
            {
                return false;
            }

            var member = _memberFactory.ResolveMember(left);
            fragment = new ModuloFilter(member.TypedLocator, divisor, op, operand);
            return true;
        }

        if (binary.Right is BinaryExpression { NodeType: ExpressionType.Modulo } moduloRight
            && StripConvert(moduloRight.Left) is MemberExpression right && IsDocumentMember(right))
        {
            AssertModuloIsIntegral(moduloRight);

            if (ExtractValue(moduloRight.Right) is not { } divisor || ExtractValue(binary.Left) is not { } operand)
            {
                return false;
            }

            var member = _memberFactory.ResolveMember(right);
            fragment = new ModuloFilter(member.TypedLocator, divisor, ReverseOperator(op), operand);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Refuses a <c>%</c> that is not integral (fisher#603). SQLite's <c>%</c> casts both operands to
    ///     INTEGER first, so <c>3.5 % 2</c> is 1 there and 1.5 in C#, and <c>0.5m % 0.25m</c> is a
    ///     division by zero (NULL) there and 0 in C#.
    /// </summary>
    private static void AssertModuloIsIntegral(BinaryExpression modulo)
    {
        var type = Nullable.GetUnderlyingType(modulo.Type) ?? modulo.Type;
        if (IntegralRank(type) > 0 || type == typeof(ulong))
        {
            return;
        }

        throw RefuseArithmetic(modulo,
            $"it is {type.Name} modulo, and SQLite's % truncates both operands to integers first, so it "
            + "would return different rows than C#");
    }

    private static BadLinqExpressionException RefuseArithmetic(Expression arithmetic, string reason)
        => new($"Cannot translate '{arithmetic}' to SQL: {reason}. " + Alternatives);

    /// <summary>
    ///     <c>x.Price - 0.1m &lt; 0.2m</c> → <c>x.Price &lt; 0.3m</c>: <c>decimal</c> Add/Subtract between a
    ///     document member and constants, folded into one constant in <c>decimal</c> arithmetic on the .NET
    ///     side (fisher#603).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQLite has no decimal type: the stored value reads back as REAL, so <c>(price - 0.1)</c>
    ///         would be computed in double, and <c>0.3 - 0.1</c> is <c>0.19999999999999998</c> there and
    ///         exactly <c>0.2</c> in C#. Folding leaves SQLite comparing the stored value with one exactly
    ///         computed constant, which is what a plain decimal comparison already does (fisher#304).
    ///     </para>
    ///     <para>
    ///         The rewritten comparison goes back through <see cref="ParseComparison" />, so a nested
    ///         <c>(x.P - 1m) - 2m</c> folds one level per pass and the plain path's rules (nullability,
    ///         conversions, a null constant) apply unchanged. <c>c - x.P &lt; v</c> negates the member and
    ///         therefore reverses the operator: <c>x.P &gt; c - v</c>.
    ///     </para>
    ///     <para>
    ///         Declines (returns false) for anything that is not exactly one member side and one constant
    ///         side, which then reaches <see cref="TryRenderArithmetic" /> and is refused there by name.
    ///     </para>
    /// </remarks>
    private bool TryFoldDecimalArithmetic(Expression arithmeticSide, Expression valueSide, string op,
        out ISqlFragment? fragment)
    {
        fragment = null;

        if (StripLifting(arithmeticSide) is not BinaryExpression
            {
                NodeType: ExpressionType.Add or ExpressionType.Subtract
            } arithmetic
            || (Nullable.GetUnderlyingType(arithmetic.Type) ?? arithmetic.Type) != typeof(decimal)
            || ReferencesFreeParameter(valueSide))
        {
            return false;
        }

        var leftReadsTheRow = ReferencesFreeParameter(arithmetic.Left);
        if (leftReadsTheRow == ReferencesFreeParameter(arithmetic.Right))
        {
            return false;
        }

        var memberOperand = leftReadsTheRow ? arithmetic.Left : arithmetic.Right;
        var constantOperand = leftReadsTheRow ? arithmetic.Right : arithmetic.Left;

        if (EvaluateValue(constantOperand) is not { } constantValue)
        {
            throw RefuseArithmetic(arithmetic, "a null operand makes the result null on every row");
        }

        var operand = Convert.ToDecimal(constantValue, CultureInfo.InvariantCulture);
        var compared = EvaluateValue(valueSide);

        if (compared == null && RangeOperators.Contains(op))
        {
            throw RefuseArithmetic(arithmetic, $"it is range-compared ({op}) with null");
        }

        decimal? folded;
        var foldedOperator = op;

        try
        {
            var value = compared == null ? (decimal?)null : Convert.ToDecimal(compared, CultureInfo.InvariantCulture);

            if (arithmetic.NodeType == ExpressionType.Add)
            {
                // x + c OP v and c + x OP v are both x OP v - c
                folded = value - operand;
            }
            else if (leftReadsTheRow)
            {
                // x - c OP v is x OP v + c
                folded = value + operand;
            }
            else
            {
                // c - x OP v is -x OP v - c, which is x OP' c - v with the operator reversed
                folded = operand - value;
                foldedOperator = ReverseOperator(op);
            }
        }
        catch (OverflowException)
        {
            throw RefuseArithmetic(arithmetic,
                "folding its constants overflows decimal, so there is no single value to compare the member with");
        }

        var member = memberOperand.Type == typeof(decimal?)
            ? memberOperand
            : Expression.Convert(memberOperand, typeof(decimal?));

        var rewritten = Expression.MakeBinary(ExpressionTypeOf(foldedOperator), member,
            Expression.Constant(folded, typeof(decimal?)));

        fragment = ParseComparison(rewritten, foldedOperator);
        return true;
    }

    /// <summary>Strips conversions that only add or remove <see cref="Nullable{T}" />.</summary>
    private static Expression StripLifting(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert
               && (Nullable.GetUnderlyingType(convert.Type) ?? convert.Type)
               == (Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type))
        {
            expression = convert.Operand;
        }

        return expression;
    }

    /// <summary>
    ///     Evaluates a parameter-free expression to its value, keeping its conversions: a float widened to
    ///     double is the double C# would compute with, not the float.
    /// </summary>
    private static object? EvaluateValue(Expression expression)
        => expression is ConstantExpression constant ? constant.Value : CompileAndInvoke(expression);

    private static ExpressionType ExpressionTypeOf(string op) => op switch
    {
        "=" => ExpressionType.Equal,
        "!=" => ExpressionType.NotEqual,
        ">" => ExpressionType.GreaterThan,
        ">=" => ExpressionType.GreaterThanOrEqual,
        "<" => ExpressionType.LessThan,
        "<=" => ExpressionType.LessThanOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };

    private ISqlFragment BuildComparisonFilter(IQueryableMember member, object? value, string op)
    {
        if (value == null)
        {
            return new WhereFragment($"{member.RawLocator} is {(op == "=" ? "null" : "not null")}");
        }

        AssertRangeIsMeaningful(member, op);

        return new ComparisonFilter(member.TypedLocator, op, member.ConvertValue(value)!);
    }

    /// <summary>
    ///     Refuses an ordering comparison against a member whose stored form does not sort.
    /// </summary>
    /// <remarks>
    ///     This has no Polecat counterpart, because SQL Server can cast a JSON string to whatever type
    ///     the comparison wants. Where SQLite offers an equivalent the member wraps its locator in one
    ///     — <see cref="Members.TimestampMember" /> normalises through <c>strftime</c> — and this guard
    ///     never fires. Where it does not, the choice is between refusing and being quietly wrong;
    ///     today that is a string-stored enum, whose stored form is a name and therefore sorts
    ///     alphabetically rather than by the enum's declared order.
    /// </remarks>
    private static void AssertRangeIsMeaningful(IQueryableMember member, string op)
    {
        if (member.AllowsRangeComparison || !RangeOperators.Contains(op))
        {
            return;
        }

        throw new BadLinqExpressionException(
            $"Cannot order or range-compare the {member.MemberType.Name} member in SQLite: its stored form "
            + "is not order-preserving, so any range predicate would return plausible but wrong rows. "
            + "Equality is supported. For an enum, storing it as an integer "
            + "(StoreOptions.Serializer.EnumStorage) makes ordering meaningful.");
    }

    private ISqlFragment ParseNot(UnaryExpression unary)
    {
        if (unary.Operand is MemberExpression member && IsBooleanMember(member))
        {
            return ParseBooleanMember(member, false);
        }

        return new NotFragment(Parse(unary.Operand));
    }

    private ISqlFragment ParseBooleanMember(MemberExpression memberExpr, bool expectedValue)
    {
        // Nullable<T>.HasValue is a null test, not a boolean column.
        if (memberExpr.Member.Name == "HasValue"
            && memberExpr.Expression is MemberExpression nullableExpr
            && Nullable.GetUnderlyingType(nullableExpr.Type) != null)
        {
            var nullable = _memberFactory.ResolveMember(nullableExpr);
            return new WhereFragment($"{nullable.RawLocator} is {(expectedValue ? "not null" : "null")}");
        }

        var member = _memberFactory.ResolveMember(memberExpr);
        return new ComparisonFilter(member.TypedLocator, "=", member.ConvertValue(expectedValue)!);
    }

    private bool TryResolveMemberAndValue(Expression memberExpr, Expression valueExpr,
        out IQueryableMember? member, out object? value)
    {
        value = null;

        if (!TryResolveDocumentMember(memberExpr, out member))
        {
            return false;
        }

        value = ExtractValue(valueExpr);
        return true;
    }

    /// <summary>
    ///     The document member one side of a comparison names, through any conversions.
    /// </summary>
    private bool TryResolveDocumentMember(Expression memberExpr, out IQueryableMember? member)
    {
        member = null;

        // (int)x.Payment.Value > 3: a primitive case of a union, possibly widened again by the compiler
        // (fisher#466). The cast is the case, so it is found before StripConvert throws it away.
        for (var current = memberExpr;
             current is UnaryExpression { NodeType: ExpressionType.Convert } convert;
             current = convert.Operand)
        {
            if (Members.UnionMemberAccess.TryMatchCaseConversion(current, out var unionMember, out var caseType)
                && IsDocumentMember(unionMember))
            {
                member = _memberFactory.ResolveUnionCase(unionMember, caseType);
                return true;
            }
        }

        if (StripConvert(memberExpr) is not MemberExpression me || !IsDocumentMember(me))
        {
            return false;
        }

        member = _memberFactory.ResolveMember(me);
        return true;
    }

    /// <summary>
    ///     Evaluates the non-member side of a comparison to a CLR value, including closure captures.
    /// </summary>
    internal static object? ExtractValue(Expression expression)
    {
        expression = StripConvert(expression);

        // fisher#595: an expression that reads the row has no single value, and compiling it fails with
        // an internal "variable 'x' ... is not defined". Every caller that reaches here with one is a
        // shape no parser translated, so it is refused by name.
        if (ReferencesFreeParameter(expression))
        {
            throw new BadLinqExpressionException(
                $"Cannot translate '{expression}' as a value: it reads the document, so it has a different "
                + "value on every row and there is nothing to bind. " + Alternatives);
        }

        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        // A captured local is a field on the compiler-generated closure object.
        if (expression is MemberExpression { Expression: ConstantExpression closure } memberExpr)
        {
            return memberExpr.Member switch
            {
                FieldInfo field => field.GetValue(closure.Value),
                PropertyInfo prop => prop.GetValue(closure.Value),
                _ => CompileAndInvoke(expression)
            };
        }

        return CompileAndInvoke(expression);
    }

    private static object? CompileAndInvoke(Expression expression)
        => Expression.Lambda(expression).Compile().DynamicInvoke();

    private static bool IsDocumentMember(MemberExpression expression)
    {
        var current = expression;
        while (current != null)
        {
            if (current.Expression is ParameterExpression)
            {
                return true;
            }

            // A member of a union case is read through a cast of the union's Value (fisher#466).
            current = Members.UnionMemberAccess.ParentOf(current) as MemberExpression;
        }

        return false;
    }

    private static bool IsBooleanMember(MemberExpression expression)
    {
        var memberType = expression.Member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => null
        };

        return memberType == typeof(bool) && IsDocumentMember(expression);
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static string ReverseOperator(string op)
        => op switch
        {
            ">" => "<",
            ">=" => "<=",
            "<" => ">",
            "<=" => ">=",
            _ => op
        };
}

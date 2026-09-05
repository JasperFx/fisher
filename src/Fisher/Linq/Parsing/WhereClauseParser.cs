using System.Diagnostics.CodeAnalysis;
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
            _ => throw new BadLinqExpressionException(
                $"Unsupported expression in a where clause: {expression.NodeType} ({expression.GetType().Name})")
        };

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
                         + $"{expression.Method.DeclaringType?.Name}.{expression.Method.Name}");

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

        throw new BadLinqExpressionException($"Unsupported binary operator in a where clause: {binary.NodeType}");
    }

    private ISqlFragment ParseComparison(BinaryExpression binary, string op)
    {
        // x.Number % 2 == 0
        if (TryParseModulo(binary, op, out var moduloFragment))
        {
            return moduloFragment!;
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

        throw new BadLinqExpressionException($"Cannot translate the comparison '{binary}' to SQL.");
    }

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

        if (StripConvert(arithmeticSide) is not BinaryExpression
            {
                NodeType: ExpressionType.Add or ExpressionType.Subtract
            } arithmetic)
        {
            return false;
        }

        // At least one operand must be a document member, or this is a constant expression the value
        // side's ExtractValue would have folded already.
        if (!TryRenderOperand(arithmetic.Left, out var left)
            || !TryRenderOperand(arithmetic.Right, out var right)
            || (StripConvert(arithmetic.Left) is not MemberExpression
                && StripConvert(arithmetic.Right) is not MemberExpression))
        {
            return false;
        }

        var sqlOperator = arithmetic.NodeType == ExpressionType.Add ? "+" : "-";

        fragment = new ComparisonFilter($"({left} {sqlOperator} {right})", op, ExtractValue(valueSide)!);
        return true;

        bool TryRenderOperand(Expression operand, out string rendered)
        {
            rendered = string.Empty;

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

                rendered = _memberFactory.ResolveMember(member).TypedLocator;
                return true;
            }

            // Only a parameter-free operand can be evaluated to a constant — ExtractValue compiles
            // the expression, and a lambda parameter inside it (a computed member of a join result,
            // say) is not a value to fold but a shape this parse must decline.
            if (!ReferencesParameter(operand)
                && ExtractValue(operand) is { } value && IsNumeric(value.GetType()))
            {
                rendered = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
                return true;
            }

            return false;
        }

        static bool ReferencesParameter(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder.Found;
        }

        static bool IsNumeric(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(int) || type == typeof(long) || type == typeof(short)
                   || type == typeof(byte) || type == typeof(double) || type == typeof(float)
                   || type == typeof(decimal);
        }
    }

    /// <summary>
    ///     Whether an expression references a lambda parameter anywhere — the test for "cannot be
    ///     folded to a constant" in <see cref="TryParseArithmetic" />.
    /// </summary>
    private sealed class ParameterFinder : ExpressionVisitor
    {
        internal bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
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

    private bool TryParseModulo(BinaryExpression binary, string op, out ISqlFragment? fragment)
    {
        fragment = null;

        if (binary.Left is BinaryExpression { NodeType: ExpressionType.Modulo } modulo
            && StripConvert(modulo.Left) is MemberExpression left && IsDocumentMember(left))
        {
            var member = _memberFactory.ResolveMember(left);
            fragment = new WhereFragment(
                $"({member.TypedLocator} % {ExtractValue(modulo.Right)}) {op} {ExtractValue(binary.Right)}");
            return true;
        }

        if (binary.Right is BinaryExpression { NodeType: ExpressionType.Modulo } moduloRight
            && StripConvert(moduloRight.Left) is MemberExpression right && IsDocumentMember(right))
        {
            var member = _memberFactory.ResolveMember(right);
            fragment = new WhereFragment(
                $"({member.TypedLocator} % {ExtractValue(moduloRight.Right)}) "
                + $"{ReverseOperator(op)} {ExtractValue(binary.Left)}");
            return true;
        }

        return false;
    }

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
        member = null;
        value = null;

        if (StripConvert(memberExpr) is not MemberExpression me || !IsDocumentMember(me))
        {
            return false;
        }

        member = _memberFactory.ResolveMember(me);
        value = ExtractValue(valueExpr);
        return true;
    }

    /// <summary>
    ///     Evaluates the non-member side of a comparison to a CLR value, including closure captures.
    /// </summary>
    internal static object? ExtractValue(Expression expression)
    {
        expression = StripConvert(expression);

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

            current = current.Expression as MemberExpression;
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

using System.Globalization;
using System.Linq.Expressions;
using Fisher.Linq.Members;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Ordering keys that compute their value from the row rather than naming a member — fisher#666, the
///     port of marten#5957 and marten#5968.
/// </summary>
/// <remarks>
///     <para>
///         <c>OrderBy(x =&gt; -x.Number)</c>, <c>OrderBy(x =&gt; x.Number % 3)</c> and
///         <c>OrderByDescending(x =&gt; x.Name.ToUpper())</c> are translated into the <c>order by</c>. Before
///         this every one of them was refused by name, because a key had to be a member.
///     </para>
///     <para>
///         <b>Only what SQLite computes exactly as .NET does is translated</b>, the rule fisher#590 set for
///         ordering after a <c>Select</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             integer <c>%</c> and <c>/</c> by a constant other than 0 and -1, and unary minus guarded at the
///             type's minimum, through <see cref="ComputedColumns" />;
///         </item>
///         <item>
///             <c>int</c> <c>+</c>, <c>-</c> and <c>*</c>, wrapped back into 32 bits as unchecked .NET wraps
///             them. SQLite computes them in 64 bits, where two <c>int</c>s cannot overflow, so the wrap is
///             exact. <c>long</c> arithmetic is refused: SQLite turns an overflowing result into a REAL;
///         </item>
///         <item>
///             a <c>double</c>'s negation, which is exact in both;
///         </item>
///         <item>
///             string concatenation, with a null read as empty as .NET reads it, <c>ToUpper()</c> and
///             <c>ToLower()</c> as SQLite's <c>upper()</c> and <c>lower()</c> (ASCII only, as in a <c>Where</c>),
///             a string's <c>Length</c>, and <c>ToString()</c> of an integer or a Guid;
///         </item>
///         <item>
///             a conversion that keeps every value and its order.
///         </item>
///     </list>
///     <para>
///         Everything else is refused by name rather than ordered by the member underneath it, which is what
///         marten#5957 found Marten doing with <c>-x.Number</c>.
///     </para>
/// </remarks>
internal static class OrderingKeys
{
    /// <summary>
    ///     The SQL for a computed ordering key, or a <see cref="BadLinqExpressionException" /> naming it.
    /// </summary>
    /// <param name="body">The key's body, already rewritten onto the row's documents where it needed to be.</param>
    /// <param name="key">The key as the caller wrote it, for the refusal.</param>
    /// <param name="members">Resolves the members, indexes and element values the key reads.</param>
    /// <param name="subject">How a refusal starts, when it is not "Fisher cannot order by '&lt;key&gt;'".</param>
    public static string Translate(Expression body, LambdaExpression key, IMemberResolver members,
        string? subject = null)
        => new Translator(subject ?? SubjectFor(key), members).Render(body);

    /// <summary>
    ///     Refuses a conversion around an ordering key's member that would reorder the rows: a narrowing
    ///     cast, a decimal to an integer, a long to a double. Called once the member has resolved, so a
    ///     refusal the member itself gives comes first.
    /// </summary>
    public static void AssertConversionsKeepOrder(LambdaExpression key, string? subject = null)
    {
        for (var node = key.Body;
             node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert;
             node = convert.Operand)
        {
            if (!KeepsOrder(convert.Operand.Type, convert.Type))
            {
                throw Refuse(subject ?? SubjectFor(key), ChangesTheValue(convert));
            }
        }
    }

    /// <summary>
    ///     A conversion that keeps every value distinct and in order: <see cref="ComputedColumns.IsLossless" />,
    ///     or an enum to or from its own underlying type, which is the same number.
    /// </summary>
    internal static bool KeepsOrder(Type from, Type to)
    {
        if (ComputedColumns.IsLossless(from, to))
        {
            return true;
        }

        // int? to int is .Value, which throws on null rather than converting it.
        if (Nullable.GetUnderlyingType(from) is not null && Nullable.GetUnderlyingType(to) is null)
        {
            return false;
        }

        var fromInner = Underlying(from);
        var toInner = Underlying(to);

        return (fromInner.IsEnum && Enum.GetUnderlyingType(fromInner) == toInner)
               || (toInner.IsEnum && Enum.GetUnderlyingType(toInner) == fromInner);
    }

    private static string ChangesTheValue(UnaryExpression convert)
        => $"'{convert}' converts a {convert.Operand.Type.Name} to a {convert.Type.Name}, which can change the values "
           + "or their order, so SQL would sort on something else";

    private static string SubjectFor(LambdaExpression key) => $"Fisher cannot order by '{key}'";

    private static BadLinqExpressionException Refuse(string subject, string reason)
        => new($"{subject}: {reason}. An ordering key can be a member, an index into an array or list, or one of "
               + "these computed from them: negation, integer % or / by a constant other than 0 and -1, int +, - and "
               + "*, string concatenation, ToUpper(), ToLower(), a string's Length, ToString() of an integer or a "
               + "Guid, or a widening cast. Order by a member, or sort in memory after the query.");

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsInt32(Type type) => Underlying(type) == typeof(int);

    private sealed class Translator(string subject, IMemberResolver members)
    {
        public string Render(Expression node)
        {
            // Integer %, / and unary minus, exactly as fisher#590 computes them in a select list.
            if (ComputedColumns.TryTranslate(node, Leaf, out var arithmetic))
            {
                return arithmetic;
            }

            switch (node)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                    if (!KeepsOrder(convert.Operand.Type, convert.Type))
                    {
                        throw Refuse(subject, ChangesTheValue(convert));
                    }

                    return Render(convert.Operand);

                // A double negates exactly in both, and has no minimum to wrap.
                case UnaryExpression { NodeType: ExpressionType.Negate } negate
                    when Underlying(negate.Type) == typeof(double):
                    return $"(-{Number(negate.Operand)})";

                case UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate:
                    throw Refuse(subject, $"'{negate}' negates a {Underlying(negate.Type).Name}; only an int, a "
                                          + "long or a double negation is computed in SQL");

                case BinaryExpression { NodeType: ExpressionType.Add } concat when concat.Type == typeof(string):
                    return Concatenation(concat.Left, concat.Right, concat);

                case MethodCallExpression { Method.Name: nameof(string.Concat), Object: null } concat
                    when concat.Method.DeclaringType == typeof(string):
                    return Concatenation(concat);

                case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                    or ExpressionType.Multiply } binary when IsInt32(binary.Type):
                    return Int32Arithmetic(binary);

                case BinaryExpression { NodeType: ExpressionType.AddChecked or ExpressionType.SubtractChecked
                    or ExpressionType.MultiplyChecked } binary:
                    throw Refuse(subject, $"'{binary}' is checked arithmetic, which throws on overflow in .NET where "
                                          + "SQL would return a value");

                case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                    or ExpressionType.Multiply } binary:
                    throw Refuse(subject, $"'{binary}' is {Underlying(binary.Type).Name} arithmetic, which SQLite "
                                          + "computes differently than .NET: it turns a long that overflows into a "
                                          + "REAL where .NET wraps, and has no decimal");

                case BinaryExpression { NodeType: ExpressionType.Modulo or ExpressionType.Divide } binary:
                    throw Refuse(subject, $"'{binary}' is {Underlying(binary.Type).Name} {binary.NodeType}; only "
                                          + "integer % and / by a constant other than 0 and -1 are computed in SQL, "
                                          + "where they truncate as .NET does");

                case MethodCallExpression { Object: { } text, Arguments.Count: 0 } call
                    when call.Method.DeclaringType == typeof(string)
                         && call.Method.Name is "ToUpper" or "ToUpperInvariant" or "ToLower" or "ToLowerInvariant":
                    return $"{(call.Method.Name.StartsWith("ToUpper", StringComparison.Ordinal) ? "upper" : "lower")}"
                           + $"({Text(text)})";

                case MemberExpression { Member.Name: nameof(string.Length), Expression: { } text }
                    when text.Type == typeof(string):
                    return $"length({Text(text)})";

                case MethodCallExpression { Method.Name: nameof(ToString), Object: { } value, Arguments.Count: 0 }:
                    return TextOf(value, node);

                case ConditionalExpression conditional:
                    throw Refuse(subject, $"'{conditional}' is a conditional, which is not translated in an "
                                          + "ordering key");
            }

            if (Leaf(node) is { } leaf)
            {
                return leaf;
            }

            throw Refuse(subject, $"'{node}' is not translated in an ordering key");
        }

        /// <summary>
        ///     <c>int</c> <c>+</c>, <c>-</c> or <c>*</c>: computed in SQLite's 64 bits, where two <c>int</c>s
        ///     cannot overflow, then wrapped into 32 bits as unchecked .NET wraps it.
        /// </summary>
        private string Int32Arithmetic(BinaryExpression binary)
        {
            var op = binary.NodeType switch
            {
                ExpressionType.Add => "+",
                ExpressionType.Subtract => "-",
                _ => "*"
            };

            var sum = $"{Int32Operand(binary.Left)} {op} {Int32Operand(binary.Right)}";

            return $"(((({sum}) + 2147483648) & 4294967295) - 2147483648)";
        }

        private string Int32Operand(Expression node)
        {
            if (!WhereClauseParser.ReferencesFreeParameter(node))
            {
                var value = WhereClauseParser.ExtractValue(node);
                return value is null
                    ? "null"
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }

            // The compiler's own widening of a short or a byte, which changes no value.
            if (node is UnaryExpression { NodeType: ExpressionType.Convert } convert
                && IsInt32(convert.Type) && IsIntegral(convert.Operand.Type)
                && ComputedColumns.IsLossless(convert.Operand.Type, convert.Type))
            {
                return Int32Operand(convert.Operand);
            }

            if (Resolve(node) is { } member && IsIntegral(node.Type))
            {
                return IntegerLeaf(member, node.Type);
            }

            return Render(node);
        }

        /// <summary>
        ///     <c>a + b</c> over strings: each piece read as empty when it is null, and an integer or a Guid piece
        ///     written as text, as .NET's concatenation does.
        /// </summary>
        private string Concatenation(Expression left, Expression right, BinaryExpression concat)
            => $"({Piece(left, concat)} || {Piece(right, concat)})";

        private string Concatenation(MethodCallExpression concat)
        {
            var pieces = concat.Arguments.Count == 1 && concat.Arguments[0] is NewArrayExpression array
                ? array.Expressions
                : concat.Arguments;

            if (pieces.Count == 0)
            {
                throw Refuse(subject, $"'{concat}' is not a concatenation Fisher translates");
            }

            return "(" + string.Join(" || ", pieces.Select(x => Piece(x, concat))) + ")";
        }

        private string Piece(Expression node, Expression concat)
        {
            // string + int is Concat(object, object) over a boxed int.
            while (node is UnaryExpression { NodeType: ExpressionType.Convert } box && box.Type == typeof(object))
            {
                node = box.Operand;
            }

            if (!WhereClauseParser.ReferencesFreeParameter(node))
            {
                var value = Convert.ToString(WhereClauseParser.ExtractValue(node)) ?? "";

                // Inlined as a literal, because an ordering key is text. Quotes are doubled, which is the whole
                // of SQLite's string-literal escaping; a NUL would end the literal early, so it is refused.
                if (value.Contains('\0'))
                {
                    throw Refuse(subject, $"'{node}' holds a NUL character, which cannot be written into SQL text");
                }

                return "'" + value.Replace("'", "''") + "'";
            }

            if (node.Type == typeof(string))
            {
                return Resolve(node) is { } member ? $"coalesce({member.RawLocator}, '')" : Render(node);
            }

            var type = Underlying(node.Type);
            if ((IsIntegral(type) && type != typeof(ulong)) || type == typeof(Guid))
            {
                return TextOf(node, concat);
            }

            throw Refuse(subject, $"'{concat}' concatenates a {type.Name}, which SQLite would write as text "
                                  + "differently than .NET does");
        }

        /// <summary><c>ToString()</c> of an integer or a Guid, as .NET writes them.</summary>
        private string TextOf(Expression value, Expression call)
        {
            var type = Underlying(value.Type);
            var nullable = Nullable.GetUnderlyingType(value.Type) is not null;

            if (IsIntegral(type) && type != typeof(ulong))
            {
                var number = Resolve(value) is { } member ? member.TypedLocator : Render(value);
                return nullable ? $"coalesce(cast({number} as text), '')" : $"cast(coalesce({number}, 0) as text)";
            }

            // A Guid is stored as its lowercase "D" form, which is what ToString() writes.
            if (type == typeof(Guid) && Resolve(value) is { } guid)
            {
                return nullable
                    ? $"coalesce({guid.RawLocator}, '')"
                    : $"coalesce({guid.RawLocator}, '{Guid.Empty}')";
            }

            throw Refuse(subject, $"'{call}' writes a {type.Name} as text, which SQLite would format differently");
        }

        /// <summary>An operand of a double negation: a member, or another expression over one.</summary>
        private string Number(Expression node)
        {
            if (Resolve(node) is { } member)
            {
                // An absent member deserializes as 0, as fisher#590's integer leaves do.
                return Nullable.GetUnderlyingType(node.Type) is null
                    ? $"coalesce({member.TypedLocator}, 0)"
                    : member.TypedLocator;
            }

            return Render(node);
        }

        /// <summary>The text a string function reads: a string member or element, or another string expression.</summary>
        private string Text(Expression node)
            => Resolve(node) is { } member ? member.RawLocator : Render(node);

        /// <summary>
        ///     A member, an index or an element value, as fisher#590's arithmetic reads it — or <c>int</c>
        ///     arithmetic, so <c>-(x.A + x.B)</c> and <c>(x.A * 2) % 3</c> nest.
        /// </summary>
        private string? Leaf(Expression node)
            => node is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                   or ExpressionType.Multiply } binary && IsInt32(binary.Type)
                ? Int32Arithmetic(binary)
                : Resolve(node)?.TypedLocator;

        private static string IntegerLeaf(IQueryableMember member, Type type)
            => Nullable.GetUnderlyingType(type) is null ? $"coalesce({member.TypedLocator}, 0)" : member.TypedLocator;

        private IQueryableMember? Resolve(Expression node) => node switch
        {
            MemberExpression member when IndexedAccess.RootOf(member) is not null => Orderable(members.ResolveMember(member)),
            ParameterExpression parameter => members.ResolveParameter(parameter) is { } element ? Orderable(element) : null,
            _ when IndexedAccess.IsIndex(node) && IndexedAccess.RootOf(node) is not null
                => Orderable(members.ResolveIndexed(node)),
            _ => null
        };

        private IQueryableMember Orderable(IQueryableMember member)
            => member.AllowsRangeComparison
                ? member
                : throw Refuse(subject, $"its {member.MemberType.Name} member's stored form is not order-preserving");

        /// <summary>Integral CLR types stored as a JSON integer: not an enum, not a char.</summary>
        private static bool IsIntegral(Type type)
        {
            var inner = Underlying(type);

            return !inner.IsEnum && Type.GetTypeCode(inner) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16
                or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;
        }
    }
}

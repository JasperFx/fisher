using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Fisher.Projections.Flattened;

/// <summary>
///     The reflection and naming odds and ends the flat-table mapping API needs.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: compiles member-access lambdas over event types supplied at projection registration, which are preserved per the AOT publishing guide.")]
internal static class FlatTableExpressions
{
    /// <summary>The member a single-member-access lambda reads.</summary>
    public static MemberInfo MemberOf<TSource, TValue>(Expression<Func<TSource, TValue>> expression)
    {
        var body = Unwrap(expression.Body);

        return body is MemberExpression member
            ? member.Member
            : throw new ArgumentException(
                $"'{expression}' is not a member access. A flat table mapping has to name a property or "
                + "field of the event, such as x => x.Amount.", nameof(expression));
    }

    /// <summary>The chain of members a possibly-nested member-access lambda reads.</summary>
    public static MemberInfo[]? MemberPath<TSource>(Expression<Func<TSource, object>>? expression)
    {
        if (expression is null)
        {
            return null;
        }

        var body = Unwrap(expression.Body);
        var members = new List<MemberInfo>();

        while (body is MemberExpression member)
        {
            members.Insert(0, member.Member);
            body = member.Expression!;
        }

        return members.Count > 0 ? members.ToArray() : null;
    }

    /// <summary>
    ///     A parameter setter that walks <paramref name="members" /> from the event body.
    /// </summary>
    public static IParameterSetter SetterForMembers<TSource>(MemberInfo[] members)
    {
        var parameter = Expression.Parameter(typeof(TSource), "x");
        Expression body = parameter;

        foreach (var member in members)
        {
            body = Expression.MakeMemberAccess(body, member);
        }

        var setterType = typeof(EventDataParameterSetter<,>).MakeGenericType(typeof(TSource), body.Type);

        return (IParameterSetter)Activator.CreateInstance(setterType,
            Expression.Lambda(body, parameter).Compile())!;
    }

    /// <summary>
    ///     <c>MemberCount</c> to <c>member_count</c>, <c>A</c> to <c>a</c>.
    /// </summary>
    public static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    ///     The declared column type for a mapped member.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQLite has five storage classes and no other types, so this is far shorter than the
    ///         sibling stores' maps — there is no width to pick and no separate date type. What the
    ///         declared name buys is <em>affinity</em>: an <c>INTEGER</c> column stores 3 as an
    ///         integer rather than as the text "3", which is what lets a caller read it back without
    ///         a cast.
    ///     </para>
    ///     <para>
    ///         Guids, timestamps and booleans follow the same conversions the rest of Fisher uses —
    ///         lowercase canonical text, ISO-8601 text, and 0/1 — see
    ///         <see cref="FlatTableValue.ToDatabaseValue" />. Anything unrecognised is declared TEXT,
    ///         which is the honest default when the value will be handed over as-is.
    ///     </para>
    /// </remarks>
    public static string ColumnTypeFor(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            return "INTEGER";
        }

        return underlying switch
        {
            _ when underlying == typeof(int) => "INTEGER",
            _ when underlying == typeof(long) => "INTEGER",
            _ when underlying == typeof(short) => "INTEGER",
            _ when underlying == typeof(byte) => "INTEGER",
            _ when underlying == typeof(bool) => "INTEGER",
            _ when underlying == typeof(double) => "REAL",
            _ when underlying == typeof(float) => "REAL",
            // Not REAL: a decimal round-tripped through a double is no longer the value that went in.
            _ when underlying == typeof(decimal) => "TEXT",
            _ when underlying == typeof(byte[]) => "BLOB",
            _ => "TEXT"
        };
    }

    private static Expression Unwrap(Expression body)
        => body is UnaryExpression unary ? unary.Operand : body;

    /// <summary>Invariant rendering, so a column literal never picks up a locale's separators.</summary>
    public static string Literal(int value) => value.ToString(CultureInfo.InvariantCulture);
}

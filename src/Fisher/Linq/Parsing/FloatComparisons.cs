using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     A <see cref="float" /> member compared with a <see cref="double" /> value (fisher#605).
/// </summary>
/// <remarks>
///     <para>
///         C# widens the member: <c>x.Weight == 0.3</c> compares <c>(double)0.3f</c>, which is
///         <c>0.30000001192092896</c>, with <c>0.3</c>, and is false. The stored member reads back as the
///         float's shortest text, <c>0.3</c>, so binding the double as it is would match. The double is
///         therefore moved onto the float it bounds, and the comparison made against that float's stored
///         value, which <c>ComparisonFilter</c> normalises.
///     </para>
///     <para>
///         A double that is exactly a float compares as that float with the same operator. Any other
///         double lies strictly between two floats, so no float equals it, and a range comparison is one
///         against the neighbouring float on the right side.
///     </para>
/// </remarks>
internal static class FloatComparisons
{
    public static bool IsFloatMember(IQueryableMember member)
        => (Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType) == typeof(float);

    public static ISqlFragment Compare(IQueryableMember member, double value, string op)
    {
        if (double.IsNaN(value))
        {
            // Every comparison with NaN is false in C#, except !=, which is true.
            return new WhereFragment(op == "!=" ? "1" : "0");
        }

        var nearest = (float)value;
        if (nearest == value)
        {
            return new ComparisonFilter(member.TypedLocator, op, nearest);
        }

        return op switch
        {
            "=" => new WhereFragment("0"),
            "!=" => new WhereFragment("1"),
            // No float equals the value, so > and >= both mean "at least the next float up".
            ">" or ">=" => new ComparisonFilter(member.TypedLocator, ">=",
                nearest > value ? nearest : MathF.BitIncrement(nearest)),
            "<" or "<=" => new ComparisonFilter(member.TypedLocator, "<=",
                nearest < value ? nearest : MathF.BitDecrement(nearest)),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
        };
    }

    /// <summary>
    ///     The values of an <c>IsOneOf</c> / <c>Contains</c> against a float member: a double that is
    ///     exactly a float becomes that float, and one that is not is dropped, because no float equals it.
    /// </summary>
    public static IEnumerable<object?> MembershipValues(IQueryableMember member, IEnumerable<object?> values)
    {
        if (!IsFloatMember(member))
        {
            return values;
        }

        return values
            .Where(x => x is not double d || (float)d == d)
            .Select(x => x is double d ? (object)(float)d : x);
    }
}

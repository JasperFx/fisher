using System.Linq.Expressions;

namespace Fisher.Linq.Members;

/// <summary>
///     A member read off a value that is stored as one JSON scalar: <c>x.Count.Value</c>,
///     <c>x.PlacedAt.Year</c>, <c>x.Name.Length</c> (fisher#483).
/// </summary>
/// <remarks>
///     <para>
///         ⚠️ <b>There is nothing below a scalar for a JSON path to reach.</b> Rendered as a path, each of
///         these is valid SQL that yields NULL: a <c>Where</c> matches no row, a <c>Select</c> returns the
///         type's default, and an <c>OrderBy</c> does not order. So every such member is translated here or
///         refused by name, and none falls through to the path.
///     </para>
///     <para>
///         <b>Date parts read the stored text rather than <c>strftime</c>.</b> .NET's
///         <c>DateTimeOffset.Year</c> is the year at the value's own offset, which is what
///         System.Text.Json wrote. <c>strftime</c> folds to UTC first, so 23:30 at -02:00 on New Year's Eve
///         would land in the wrong year. The positions are fixed in the ISO 8601 form the serializer
///         writes. <c>RawLocator</c> is the input for the same reason: a duplicated column holds the
///         UTC-normalised form, and the JSON holds the original.
///     </para>
/// </remarks>
internal static class ScalarMemberAccess
{
    /// <summary>Whether a value of this type is stored as one JSON scalar.</summary>
    internal static bool IsScalar(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        return target.IsPrimitive || target.IsEnum
                                  || target == typeof(string) || target == typeof(decimal) || target == typeof(Guid)
                                  || target == typeof(DateTime) || target == typeof(DateTimeOffset)
                                  || target == typeof(DateOnly) || target == typeof(TimeOnly)
                                  || target == typeof(TimeSpan) || target == typeof(Uri);
    }

    /// <summary>Translate <paramref name="access" />, whose owner resolved to <paramref name="owner" />.</summary>
    internal static IQueryableMember Resolve(MemberExpression access, IQueryableMember owner)
    {
        var ownerType = access.Expression!.Type;
        var scalar = Nullable.GetUnderlyingType(ownerType) ?? ownerType;
        var name = access.Member.Name;

        if (name == nameof(Nullable<int>.HasValue) && scalar != ownerType)
        {
            return new QueryableMember($"({owner.RawLocator} is not null)", typeof(bool), isBoolean: true);
        }

        if (name == nameof(string.Length) && scalar == typeof(string))
        {
            return new QueryableMember($"length({owner.RawLocator})", typeof(int));
        }

        if (TextPosition(scalar, name) is var (start, length))
        {
            return new QueryableMember($"cast(substr({owner.RawLocator}, {start}, {length}) as integer)", typeof(int));
        }

        throw new BadLinqExpressionException(
            $"'{scalar.Name}.{name}' cannot be translated: a {scalar.Name} is stored as one JSON value, so "
            + "nothing below it can be read from the stored document. Compare the whole value against a range "
            + "instead, or read the member and compute this in .NET.");
    }

    /// <summary>Where a date part sits in the text System.Text.Json writes for the type.</summary>
    private static (int Start, int Length)? TextPosition(Type scalar, string name)
    {
        if (scalar == typeof(TimeOnly))
        {
            return name switch
            {
                nameof(TimeOnly.Hour) => (1, 2),
                nameof(TimeOnly.Minute) => (4, 2),
                nameof(TimeOnly.Second) => (7, 2),
                _ => null
            };
        }

        if (scalar == typeof(DateOnly))
        {
            return DatePosition(name);
        }

        if (scalar != typeof(DateTime) && scalar != typeof(DateTimeOffset))
        {
            return null;
        }

        return name switch
        {
            nameof(DateTime.Hour) => (12, 2),
            nameof(DateTime.Minute) => (15, 2),
            nameof(DateTime.Second) => (18, 2),
            _ => DatePosition(name)
        };
    }

    private static (int Start, int Length)? DatePosition(string name) => name switch
    {
        nameof(DateTime.Year) => (1, 4),
        nameof(DateTime.Month) => (6, 2),
        nameof(DateTime.Day) => (9, 2),
        _ => null
    };
}

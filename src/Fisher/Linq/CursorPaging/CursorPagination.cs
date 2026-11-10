using System.Text.Json;
using JasperFx.Pagination;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.CursorPaging;

/// <summary>
///     Keyset (seek) pagination — the cursor and the seek predicate it becomes (fisher#27).
/// </summary>
/// <remarks>
///     <para>
///         The complement to <c>ToPagedListAsync</c> rather than a replacement. Offset paging can jump
///         to an arbitrary page and report a total; keyset paging can do neither, but is stable under
///         concurrent writes and does not degrade as the offset grows.
///     </para>
///     <para>
///         <b>The cursor's encoding is JasperFx's <see cref="CursorCodec" /></b> (jasperfx#1054,
///         fisher#465), lifted from this class and Polecat's. Fisher supplies the two things that are its
///         own — what a key read back from a row becomes, and what a decoded slot binds as — in
///         <see cref="FisherCursorCodec" />. The seek predicate stays here, being SQL over Fisher's own
///         locators. ⚠️ <b>The bytes are the contract</b>: <c>cursor_encoding</c> pins the exact string, and
///         a change of spelling invalidates every cursor in flight.
///     </para>
///     <para>
///         <b>Values are typed on decode by the query's ordering members, never by the cursor.</b> Every
///         value then enters the SQL as a bound parameter.
///     </para>
/// </remarks>
internal static class CursorPagination
{
    /// <summary>
    ///     Refuses an ordering that cannot support a seek: none, one over something that is not a
    ///     member, or one whose terminal key is not the identity (so not a total order).
    /// </summary>
    public static void ValidateOrdering(IReadOnlyList<IQueryableMember?> members)
        => FisherCursorCodec.Instance.ValidateOrdering(members, member => member is IdMember);

    public static string Encode(IReadOnlyList<object?> keyValues) => FisherCursorCodec.Instance.Encode(keyValues);

    public static object?[] Decode(string cursor, IReadOnlyList<IQueryableMember?> members)
        => FisherCursorCodec.Instance.Decode(cursor, members.Select(x => x!.MemberType).ToArray());

    /// <summary>
    ///     The composite seek: <c>(k0 op v0) or (k0 = v0 and k1 op v1) or …</c>, where <c>op</c> is
    ///     <c>&gt;</c> for an ascending key and <c>&lt;</c> for a descending one.
    /// </summary>
    /// <remarks>
    ///     <b>The expanded form rather than SQLite's row-value comparison</b>, which has been available
    ///     since 3.15 and would be one comparison the planner can serve from a composite index. Row
    ///     values only express a seek when every key runs the same direction, and mixed direction is the
    ///     common case — <c>OrderByDescending(x => x.Landed).ThenBy(x => x.Id)</c>. Special-casing the
    ///     uniform ordering is a possible optimisation, not a correctness matter.
    /// </remarks>
    public static ISqlFragment BuildSeekPredicate(
        IReadOnlyList<(string Locator, bool Descending)> orderBy, object?[] values)
    {
        var clauses = new List<ISqlFragment>();

        for (var i = 0; i < orderBy.Count; i++)
        {
            var terms = new List<ISqlFragment>();

            for (var j = 0; j <= i; j++)
            {
                var op = j < i ? "=" : orderBy[j].Descending ? "<" : ">";

                terms.Add(values[j] is null
                    ? new LiteralSqlFragment($"{orderBy[j].Locator} is null")
                    : new ComparisonFilter(orderBy[j].Locator, op, values[j]!));
            }

            clauses.Add(CompoundWhereFragment.And(terms));
        }

        return CompoundWhereFragment.Or(clauses);
    }
}

/// <summary>
///     What a Fisher cursor carries and binds, over JasperFx's shared format.
/// </summary>
/// <remarks>
///     The format is shared with Polecat; the representation is not. Fisher compares against what SQLite
///     holds, so a Guid travels and binds as its lowercase text and a timestamp as
///     <see cref="SqliteTimestamp" />'s fixed-width UTC text, where Polecat binds typed values.
/// </remarks>
internal sealed class FisherCursorCodec : CursorCodec
{
    public static FisherCursorCodec Instance { get; } = new();

    /// <summary>
    ///     The value to put in the cursor for a key read back out of the row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A Guid and a timestamp become strings — the same encodings Fisher stores — so the round
    ///         trip through JSON is lossless and the decoded value compares against the column as written.
    ///     </para>
    ///     <para>
    ///         A strong-typed identity is carried as the value it wraps (fisher#412). The terminal key is
    ///         the identity, so this is the ordinary case for a document keyed by a wrapper — and the
    ///         reflection-based serializer wrote the wrapper as an object (<c>{"Value":…}</c>) that
    ///         <c>ConvertSlot</c> could never bind back, so the second page of such a walk
    ///         always failed. The id column holds the inner value, which is what the seek compares
    ///         against.
    ///     </para>
    /// </remarks>
    protected override object? NormalizeKey(object? value)
        => value switch
        {
            DBNull => null,
            Guid guid => guid.ToString(),
            DateTimeOffset timestamp => SqliteTimestamp.ToDatabaseValue(timestamp),
            not null when StrongTypedId.TryResolve(value.GetType(), out var wrapper)
                => NormalizeKey(wrapper.ValueProperty.GetValue(value)),
            _ => value
        };

    protected override object? ConvertSlot(JsonElement slot, Type memberType)
    {
        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (StrongTypedId.TryResolve(target, out var wrapper))
        {
            target = wrapper.SimpleType;
        }

        if (target == typeof(Guid))
        {
            return slot.GetString();
        }

        if (target == typeof(DateTimeOffset) || target == typeof(DateTime))
        {
            // Already in SqliteTimestamp's fixed-width form; it goes back into the comparison as the
            // text the column holds, not as a DateTimeOffset that would be re-rendered.
            return slot.GetString();
        }

        if (target.IsEnum)
        {
            return slot.GetInt64();
        }

        return Type.GetTypeCode(target) switch
        {
            TypeCode.String => slot.GetString(),
            TypeCode.Boolean => slot.GetBoolean() ? 1L : 0L,
            TypeCode.Int32 or TypeCode.Int16 or TypeCode.Byte or TypeCode.SByte => slot.GetInt64(),
            TypeCode.Int64 => slot.GetInt64(),
            TypeCode.Double or TypeCode.Single or TypeCode.Decimal => slot.GetDouble(),
            _ => slot.GetString()
        };
    }

    /// <summary>JasperFx's <see cref="JasperFx.BadLinqExpressionException" />, thrown directly since fisher#530.</summary>
    protected override Exception OrderingRefused(string message) => new JasperFx.BadLinqExpressionException(message);
}

using System.Text;
using System.Text.Json;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.CursorPaging;

/// <summary>
///     Keyset (seek) pagination — the cursor's encoding and the seek predicate it becomes (fisher#27).
/// </summary>
/// <remarks>
///     <para>
///         The complement to <c>ToPagedListAsync</c> rather than a replacement. Offset paging can jump
///         to an arbitrary page and report a total; keyset paging can do neither, but is stable under
///         concurrent writes and does not degrade as the offset grows. Polecat and Marten carry both
///         for the same reason.
///     </para>
///     <para>
///         The cursor is an opaque versioned base64-JSON value carrying the previous page's last row's
///         sort keys, and <b>the format is byte-identical to Polecat's</b> so a cursor is portable
///         between the stores.
///     </para>
///     <para>
///         <b>Values are typed on decode by the query's ordering members, never by the cursor.</b> The
///         payload carries no type information, so a hand-edited cursor can change values but not what
///         they are read as — which is what keeps this from being a type-confusion or injection seam.
///         Every value then enters the SQL as a bound parameter.
///     </para>
/// </remarks>
internal static class CursorPagination
{
    private const string Version = "v1:";

    /// <summary>
    ///     Refuses an ordering that cannot support a seek.
    /// </summary>
    /// <remarks>
    ///     The terminal key must be the identity, so the ordering is a <em>total</em> order. Without
    ///     that, rows tied on the sort key have no defined order between them and a seek boundary
    ///     lands in the middle of the tie — skipping some and repeating others, silently and only when
    ///     there are ties. This is the check that makes the rest of the mechanism honest.
    /// </remarks>
    public static void ValidateOrdering(IReadOnlyList<IQueryableMember?> members)
    {
        if (members.Count == 0)
        {
            throw new BadLinqExpressionException(
                "Keyset pagination requires an OrderBy. Add one whose terminal key is the document "
                + "identity — for example OrderBy(x => x.Landed).ThenBy(x => x.Id).");
        }

        if (members.Any(x => x is null))
        {
            throw new BadLinqExpressionException(
                "Keyset pagination needs every ordering key to be a document member, so its value can "
                + "be carried in the cursor. An ordering over a projection or a group aggregate cannot.");
        }

        if (members[^1] is not IdMember)
        {
            throw new BadLinqExpressionException(
                "Keyset pagination requires the terminal ordering key to be the document identity, so "
                + "the ordering is a total order — otherwise rows tied on the sort key would be skipped "
                + "or repeated across pages. End the ordering with ThenBy(x => x.Id).");
        }
    }

    public static string Encode(IReadOnlyList<object?> keyValues)
    {
        var json = JsonSerializer.Serialize(keyValues.Select(Normalize).ToArray());

        return Version + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static object?[] Decode(string cursor, IReadOnlyList<IQueryableMember?> members)
    {
        if (!cursor.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unrecognized or unversioned cursor; expected a '{Version}' prefix.", nameof(cursor));
        }

        JsonElement[] slots;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor[Version.Length..]));
            slots = JsonSerializer.Deserialize<JsonElement[]>(json) ?? [];
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw new ArgumentException("Malformed cursor payload.", nameof(cursor), e);
        }

        if (slots.Length != members.Count)
        {
            throw new ArgumentException(
                $"This cursor carries {slots.Length} key(s) but the query orders by {members.Count}. It "
                + "was issued against a different ordering.", nameof(cursor));
        }

        var values = new object?[slots.Length];

        for (var i = 0; i < slots.Length; i++)
        {
            // fisher#62, the marten#5029 class. The payload's *shape* is checked above; binding each
            // slot to its ordering key's type is a second way a client-supplied cursor can be wrong,
            // and JsonElement reports that as an InvalidOperationException — which an endpoint has no
            // reason to read as anything but a fault of its own. A cursor is request input, so every
            // way of malforming it has to arrive as the same kind of error.
            try
            {
                values[i] = ConvertSlot(slots[i], members[i]!.MemberType);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
            {
                throw new ArgumentException(
                    $"This cursor's key {i} does not bind to '{members[i]!.MemberType.Name}'. It was issued "
                    + "against a different ordering, or has been tampered with.", nameof(cursor), e);
            }
        }

        return values;
    }

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

    /// <summary>
    ///     The value to put in the cursor for a key read back out of the row.
    /// </summary>
    /// <remarks>
    ///     A Guid and a timestamp become strings — the same encodings Fisher stores — so the round trip
    ///     through JSON is lossless and the decoded value compares against the column as written.
    /// </remarks>
    private static object? Normalize(object? value)
        => value switch
        {
            DBNull => null,
            Guid guid => guid.ToString(),
            DateTimeOffset timestamp => SqliteTimestamp.ToDatabaseValue(timestamp),
            _ => value
        };

    private static object? ConvertSlot(JsonElement slot, Type memberType)
    {
        if (slot.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;

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
}

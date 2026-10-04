using System.Buffers;
using System.Globalization;
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

    /// <remarks>
    ///     <para>
    ///         <b>A fixed-shape <see cref="Utf8JsonWriter" />, not <c>JsonSerializer</c></b> (fisher#412).
    ///         The payload is a heterogeneous array of scalars, which the reflection-based serializer
    ///         handled by inspecting each element's runtime type — and Native AOT disables that, so the
    ///         first full page of a keyset query threw in a native image. Each value is written with the
    ///         same <see cref="Utf8JsonWriter" /> call System.Text.Json's own converter for its type
    ///         makes, under the same default encoder.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>The output is byte-identical to what <c>JsonSerializer.Serialize(object?[])</c>
    ///         wrote</b>, and <c>cursor_encoding</c> pins the exact string. A cursor is handed to a client
    ///         and comes back on a later request, possibly to a newer deployment, and the format is
    ///         shared with Polecat — so a change of spelling here invalidates every cursor in flight.
    ///     </para>
    /// </remarks>
    public static string Encode(IReadOnlyList<object?> keyValues)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var value in keyValues)
            {
                WriteKey(writer, Normalize(value));
            }

            writer.WriteEndArray();
        }

        return Version + Convert.ToBase64String(buffer.WrittenSpan);
    }

    /// <summary>
    ///     One key, written exactly as System.Text.Json's built-in converter for its type writes it.
    /// </summary>
    /// <remarks>
    ///     An enum is its number, as STJ's default <c>EnumConverter</c> writes it. <see cref="TimeOnly" />
    ///     and <see cref="TimeSpan" /> are the TimeSpan constant (<c>"c"</c>) format, which is what STJ's
    ///     converters for both emit. Anything else is refused by name: no ordering key the provider reads
    ///     back is another type, and a guess at its spelling would be a cursor nobody could decode.
    /// </remarks>
    private static void WriteKey(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case Enum enumValue
                when Type.GetTypeCode(Enum.GetUnderlyingType(enumValue.GetType())) == TypeCode.UInt64:
                writer.WriteNumberValue(Convert.ToUInt64(enumValue, CultureInfo.InvariantCulture));
                break;
            case Enum enumValue:
                writer.WriteNumberValue(Convert.ToInt64(enumValue, CultureInfo.InvariantCulture));
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case char character:
                writer.WriteStringValue(character.ToString());
                break;
            case DateTime timestamp:
                writer.WriteStringValue(timestamp);
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture));
                break;
            case TimeSpan span:
                writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture));
                break;
            default:
                throw new NotSupportedException(
                    $"Keyset pagination cannot carry an ordering key of type '{value.GetType().Name}' in a "
                    + "cursor. Order by members holding a string, a number, a Guid, a timestamp, a date or "
                    + "a time, ending with the document identity.");
        }
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
            // JsonDocument rather than JsonSerializer.Deserialize<JsonElement[]> (fisher#412): the same
            // parse, with nothing for Native AOT to refuse. A JSON null reads as no keys, as the
            // serializer read it, and anything else that is not an array is malformed.
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor[Version.Length..]));
            using var document = JsonDocument.Parse(json);

            slots = document.RootElement.ValueKind switch
            {
                JsonValueKind.Null => [],
                JsonValueKind.Array => document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray(),
                _ => throw new JsonException("A cursor payload is a JSON array.")
            };
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
    ///     <para>
    ///         A Guid and a timestamp become strings — the same encodings Fisher stores — so the round
    ///         trip through JSON is lossless and the decoded value compares against the column as written.
    ///     </para>
    ///     <para>
    ///         A strong-typed identity is carried as the value it wraps (fisher#412). The terminal key is
    ///         the identity, so this is the ordinary case for a document keyed by a wrapper — and the
    ///         reflection-based serializer wrote the wrapper as an object (<c>{"Value":…}</c>) that
    ///         <see cref="ConvertSlot" /> could never bind back, so the second page of such a walk
    ///         always failed. The id column holds the inner value, which is what the seek compares
    ///         against.
    ///     </para>
    /// </remarks>
    private static object? Normalize(object? value)
        => value switch
        {
            DBNull => null,
            Guid guid => guid.ToString(),
            DateTimeOffset timestamp => SqliteTimestamp.ToDatabaseValue(timestamp),
            not null when StrongTypedId.TryResolve(value.GetType(), out var wrapper)
                => Normalize(wrapper.ValueProperty.GetValue(value)),
            _ => value
        };

    private static object? ConvertSlot(JsonElement slot, Type memberType)
    {
        if (slot.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

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
}

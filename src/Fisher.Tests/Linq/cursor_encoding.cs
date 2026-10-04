using System.Text;
using Fisher.Linq.CursorPaging;

namespace Fisher.Tests.Linq;

/// <summary>
///     The keyset cursor's wire format — fisher#412.
/// </summary>
/// <remarks>
///     The cursor used to be written by the reflection-based <c>JsonSerializer</c>, which Native AOT
///     disables, and is now a fixed-shape <c>Utf8JsonWriter</c>. A cursor is handed to a client and comes
///     back on a later request, possibly to a newer deployment, and the format is shared with Polecat — so
///     the exact bytes are the contract. <see cref="Expected" /> was captured from the
///     <c>JsonSerializer.Serialize(object?[])</c> implementation immediately before it was replaced, over
///     every shape of key the provider reads back off a row.
/// </remarks>
public class cursor_encoding
{
    public enum Grade { Pass = 1, Merit = 2 }

    private static readonly object?[] EveryKeyShape =
    [
        "Rock \"Pool\" <café> & 'quay' + \\ \u0001",
        42,
        9_000_000_000L,
        (short)7,
        1.5d,
        3.0d,
        0.1f,
        12.50m,
        true,
        null,
        DBNull.Value,
        Guid.Parse("8D2F6C1E-3A4B-4C5D-9E8F-0A1B2C3D4E5F"),
        new DateTimeOffset(2026, 8, 8, 18, 45, 30, 123, TimeSpan.FromHours(-5)),
        new DateTime(2026, 8, 8, 18, 45, 30, 120, DateTimeKind.Utc),
        Grade.Merit,
        new DateOnly(2026, 8, 8),
        new TimeOnly(18, 45, 30, 500),
        new TimeOnly(9, 5),
        TimeSpan.FromMinutes(90.5),
        'x',
        7u
    ];

    private const string Expected =
        "v1:WyJSb2NrIFx1MDAyMlBvb2xcdTAwMjIgXHUwMDNDY2FmXHUwMEU5XHUwMDNFIFx1MDAyNiBcdTAwMjdxdWF5XHUwMDI3IFx1MDAyQiBcXCBcdTAwMDEiLDQyLDkwMDAwMDAwMDAsNywxLjUsMywwLjEsMTIuNTAsdHJ1ZSxudWxsLG51bGwsIjhkMmY2YzFlLTNhNGItNGM1ZC05ZThmLTBhMWIyYzNkNGU1ZiIsIjIwMjYtMDgtMDhUMjM6NDU6MzAuMTIzWiIsIjIwMjYtMDgtMDhUMTg6NDU6MzAuMTJaIiwyLCIyMDI2LTA4LTA4IiwiMTg6NDU6MzAuNTAwMDAwMCIsIjA5OjA1OjAwIiwiMDE6MzA6MzAiLCJ4Iiw3XQ==";

    [Fact]
    public void the_encoding_is_byte_identical_to_the_serializer_it_replaced()
        => CursorPagination.Encode(EveryKeyShape).ShouldBe(Expected);

    /// <summary>
    ///     The decoded form, for whoever has to read a failure of the test above.
    /// </summary>
    [Fact]
    public void the_payload_is_the_json_array_it_always_was()
        => Encoding.UTF8.GetString(Convert.FromBase64String(Expected["v1:".Length..])).ShouldBe(
            "[\"Rock \\u0022Pool\\u0022 \\u003Ccaf\\u00E9\\u003E \\u0026 \\u0027quay\\u0027 \\u002B \\\\ \\u0001\","
            + "42,9000000000,7,1.5,3,0.1,12.50,true,null,null,\"8d2f6c1e-3a4b-4c5d-9e8f-0a1b2c3d4e5f\","
            + "\"2026-08-08T23:45:30.123Z\",\"2026-08-08T18:45:30.12Z\",2,\"2026-08-08\",\"18:45:30.5000000\","
            + "\"09:05:00\",\"01:30:30\",\"x\",7]");

    [Fact]
    public void a_payload_that_is_not_an_array_is_malformed()
    {
        var cursor = "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":1}"));

        Should.Throw<ArgumentException>(() => CursorPagination.Decode(cursor, []))
            .Message.ShouldContain("Malformed cursor payload");
    }
}

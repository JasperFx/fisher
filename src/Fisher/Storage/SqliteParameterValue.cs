namespace Fisher.Storage;

/// <summary>
///     Converts a caller-supplied CLR value into the form SQLite should bind for it to match what
///     Fisher stores (fisher#34).
/// </summary>
/// <remarks>
///     <para>
///         Fisher's own write paths convert explicitly on the way in — a Guid through
///         <see cref="ClosedShape.SqliteGuidIdentification" />, a timestamp through
///         <see cref="SqliteTimestamp" />, a document member through its serializer. Raw SQL has no
///         such path: the value goes from the caller's hand to <c>SqliteParameter.Value</c>, and
///         Microsoft.Data.Sqlite then binds it by its CLR type. For three types that produces something
///         that matches nothing Fisher has ever written, silently and with no error.
///     </para>
///     <para>
///         <b>None of this is needed on Marten or Polecat</b>, which is why there is no sibling to port
///         it from. PostgreSQL has <c>uuid</c> and <c>timestamptz</c>, SQL Server has
///         <c>uniqueidentifier</c> and <c>datetimeoffset</c>, and both have a real <c>decimal</c>; the
///         provider hands each straight across. SQLite has none of the three, so Fisher chose a text
///         encoding for each — and a raw-SQL caller has to be given the same encoding or their
///         predicate compares two different renderings of the same value.
///     </para>
///     <para>
///         Verified against Microsoft.Data.Sqlite 10.0.9 before this was written, and pinned by
///         <c>raw_sql_parameter_binding</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>A <see cref="Guid" /> binds as UPPERCASE text.</b> Fisher stores the lowercase
///                 canonical form and SQLite's default collation is case-sensitive, so an unconverted
///                 Guid matches zero rows. The recurring trap — see
///                 <see cref="ClosedShape.SqliteGuidIdentification" /> for the same thing on the
///                 document write path, and the tag writer for it on the event path.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>A <see cref="DateTimeOffset" /> binds as <c>2026-08-08 13:45:30.123-05:00</c></b> —
///                 a space rather than <c>T</c>, and the original offset rather than UTC. Fisher stores
///                 <see cref="SqliteTimestamp.Format" />, chosen so a string comparison is an instant
///                 comparison. The two never compare equal, and worse, they do not order together
///                 either, so a range predicate returns a plausible wrong answer rather than nothing.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>A <see cref="decimal" /> binds as text</b>, where <c>json_extract</c> yields a
///                 REAL for any JSON number. Against a *declared* column SQLite's affinity rules
///                 rescue the comparison; against <c>json_extract</c> there is no affinity to apply and
///                 the match fails. Since <c>json_extract</c> is how every undeclared document member is
///                 read, that is the case a raw-SQL caller is most likely to hit. Converted to
///                 <see cref="double" />, matching the REAL that <c>SqliteTypeFor</c> already declares
///                 for a duplicated decimal column, and carrying the same precision caveat.
///             </description>
///         </item>
///     </list>
///     <para>
///         Everything else binds correctly as it stands and is passed through untouched — a
///         <see cref="bool" /> becomes INTEGER 1/0, an enum becomes its integer value, and
///         <see cref="double" />, <see cref="int" />, <see cref="long" />, <see cref="string" /> and
///         <c>byte[]</c> are already what Fisher stores. Notably a declared <c>SqliteType</c> on the
///         parameter does <b>not</b> coerce the value — the provider binds by the CLR type regardless —
///         which is why Weasel's <c>AppendWithDbParameters</c> stamping every placeholder as TEXT is
///         harmless here rather than a fourth problem.
///     </para>
/// </remarks>
internal static class SqliteParameterValue
{
    /// <summary>
    ///     The value to bind for <paramref name="value" />, or <see cref="DBNull.Value" /> for null.
    /// </summary>
    public static object ToDatabaseValue(object? value)
        => value switch
        {
            null => DBNull.Value,
            Guid guid => guid.ToString(),
            DateTimeOffset timestamp => SqliteTimestamp.ToDatabaseValue(timestamp),
            DateTime dateTime => SqliteTimestamp.ToDatabaseValue(ToOffset(dateTime)),
            decimal money => (double)money,
            _ => value
        };

    /// <summary>
    ///     A <see cref="DateTime" /> carries no offset, so one has to be assumed.
    /// </summary>
    /// <remarks>
    ///     <see cref="DateTimeKind.Utc" /> is taken at face value; <see cref="DateTimeKind.Local" /> and
    ///     <see cref="DateTimeKind.Unspecified" /> are read as local, which is what
    ///     <c>new DateTimeOffset(DateTime)</c> does and therefore what a caller who has read any .NET
    ///     documentation expects. Stated rather than left implicit, because "unspecified means UTC" is
    ///     the other defensible choice and picking it silently would shift every such value by the
    ///     machine's offset.
    /// </remarks>
    private static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : new DateTimeOffset(value);
}

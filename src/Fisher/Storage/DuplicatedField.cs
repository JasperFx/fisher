using System.Reflection;
using Weasel.Storage;

namespace Fisher.Storage;

/// <summary>
///     One document member lifted out of the JSON body into a column of its own, so a query can use an
///     index instead of computing <c>json_extract</c> for every row.
/// </summary>
/// <remarks>
///     <para>
///         <b>The column is a SQLite <c>VIRTUAL</c> generated column, not a written one.</b> Marten and
///         Polecat write theirs on every upsert and refresh them in patch SQL; SQLite can derive the
///         value from <c>data</c> itself, and indexes a virtual generated column exactly as it indexes
///         a real one (verified against 3.51 — the query plan reads
///         <c>SEARCH … USING INDEX</c>). Three consequences follow, and they are the reason for the
///         choice:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>It cannot drift from <c>data</c>.</b> There is no write path to get wrong, so
///             <c>Duplicate</c> can be added to a type that already has rows and every existing row is
///             correct immediately. A written column would need a backfill.
///         </item>
///         <item>
///             <b>It costs index space, not row space.</b> <c>VIRTUAL</c> computes on read; only the
///             index materialises the value.
///         </item>
///         <item>
///             <b>Nothing binds it.</b> The write SQL is untouched — no extra parameter, no shift in
///             the positional contract <c>SqliteDocumentStorageDescriptorBuilder</c> maintains — which
///             is why this arrives without disturbing document writes at all.
///         </item>
///     </list>
///     <para>
///         The generated expression is the member's own <c>TypedLocator</c>, not a hand-written
///         <c>json_extract</c>. That is what makes a duplicated timestamp mean the same thing as an
///         unduplicated one: the locator for a <see cref="Linq.Members.TimestampMember" /> is
///         <c>strftime(…)</c>, so the column holds the normalised, sortable form fisher#1 introduced —
///         and it is the one member type where per-row computation was expensive enough to want the
///         index.
///     </para>
/// </remarks>
internal sealed class DuplicatedField : IDuplicatedField
{
    /// <summary>
    ///     The column names Fisher owns on a document table. A duplicated field may not take one of
    ///     them: the collision would be reported by SQLite as a duplicate column at CREATE TABLE, long
    ///     after the configuration that caused it.
    /// </summary>
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "data", "tenant_id", "guid_version", "dotnet_type", "last_modified",
        SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn
    };

    internal DuplicatedField(MemberInfo[] members, string columnName, string? columnType, bool shouldIndex)
    {
        Members = members;
        ColumnName = columnName;
        ColumnType = columnType;
        ShouldIndex = shouldIndex;
    }

    public MemberInfo[] Members { get; }

    public string ColumnName { get; }

    /// <summary>An explicit SQLite type, or null to derive one from the member's CLR type.</summary>
    public string? ColumnType { get; }

    public bool ShouldIndex { get; }

    /// <summary>
    ///     The member names the column is derived from, which is how a resolved member expression is
    ///     matched back to it.
    /// </summary>
    /// <remarks>
    ///     Compared by name rather than by <see cref="MemberInfo" /> identity, because the same property
    ///     reached through a derived type and through the type that declares it produces two
    ///     <c>MemberInfo</c>s that need not be equal — while the JSON path, which is all either one is
    ///     used for, is the same.
    /// </remarks>
    public string[] MemberNames => Array.ConvertAll(Members, x => x.Name);

    /// <summary>
    ///     Never called. Fisher's duplicated columns are generated, so nothing ever assigns one — and a
    ///     plausible-looking fragment here would be an invitation to wire up a write path that SQLite
    ///     would then reject.
    /// </summary>
    public string UpdateSqlFragment()
        => throw new NotSupportedException(
            $"'{ColumnName}' is a generated column derived from data; it is never written, so there is "
            + "no update fragment for it.");

    /// <summary>
    ///     The default column name for a member chain — <c>Name</c> becomes <c>name</c>,
    ///     <c>LandedAt</c> becomes <c>landed_at</c>, and <c>Water.Name</c> becomes <c>water_name</c>.
    /// </summary>
    /// <remarks>
    ///     Snake case rather than Marten's plain lowercasing, which would give <c>landedat</c>. Every
    ///     other column on a Fisher document table is snake case — <c>last_modified</c>,
    ///     <c>dotnet_type</c>, <c>is_deleted</c> — and a duplicated column sits among them.
    /// </remarks>
    internal static string DefaultColumnNameFor(MemberInfo[] members)
        => string.Join("_", members.Select(x => ToSnakeCase(x.Name)));

    private static string ToSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];

            if (!char.IsUpper(character))
            {
                builder.Append(character);
                continue;
            }

            // A separator before an upper-case letter, except at the start and inside a run of them —
            // so HttpCode and HTTPCode both become http_code rather than h_t_t_p_code.
            var startsWord = i > 0 &&
                             (!char.IsUpper(name[i - 1]) ||
                              (i + 1 < name.Length && char.IsLower(name[i + 1])));

            if (startsWord)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    internal static void AssertColumnNameIsAvailable(string columnName, Type documentType)
    {
        if (ReservedColumns.Contains(columnName))
        {
            throw new InvalidOperationException(
                $"Cannot duplicate a member of '{documentType.Name}' into column '{columnName}': that "
                + "is one of the columns Fisher owns on a document table. Supply an explicit column "
                + "name instead.");
        }
    }

    /// <summary>
    ///     The SQLite type to declare, from the member's CLR type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A two-way-plus-one split rather than a type map, because SQLite's typing is affinity
    ///         based: the declared type decides how values <em>compare</em>, and getting it wrong is how
    ///         a numeric column starts sorting as text.
    ///     </para>
    ///     <para>
    ///         A timestamp lands in TEXT and is correct there, because the generated expression is the
    ///         <c>strftime</c> locator whose output is fixed-width UTC — the same property that lets the
    ///         event tables' timestamps sort as text. A string-stored enum also lands in TEXT and is
    ///         correct for equality only, exactly as it is unduplicated; <c>AllowsRangeComparison</c>
    ///         still refuses to order by it.
    ///     </para>
    /// </remarks>
    internal static string SqliteTypeFor(Type memberType)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (type.IsEnum)
        {
            // Under EnumStorage.AsInteger json_extract yields a number and under AsString a name;
            // the locator is the same either way, so the declared affinity has to suit both. TEXT
            // holds an integer's digits without reordering them for equality, which is all a
            // string-stored enum supports anyway.
            return "TEXT";
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "INTEGER",

            // decimal included: json_extract hands back a REAL for any JSON number, so declaring
            // anything else would make the column's affinity disagree with its own expression.
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "REAL",

            _ => "TEXT"
        };
    }
}

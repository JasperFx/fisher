using System.Data.Common;
using Weasel.Core.Identity;
using Weasel.Core.Sequences;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     A Guid identity strategy that binds and reads the id as Fisher's canonical lowercase text.
/// </summary>
/// <remarks>
///     <para>
///         Wraps <see cref="SequentialGuidIdentification{TDoc}" /> for generation and assignment and
///         changes only how the value crosses the ADO.NET boundary — <c>RawSqlType</c> becomes
///         <c>string</c>, so nothing downstream ever hands Microsoft.Data.Sqlite a raw
///         <see cref="Guid" />.
///     </para>
///     <para>
///         <b>Why this exists.</b> The shared write operations bind
///         <c>Identification.ToRawSqlValue(id)</c> directly, bypassing the dialect's value
///         conversion. Given a raw Guid and a TEXT parameter type, Microsoft.Data.Sqlite writes the
///         <b>uppercase</b> form, while <see cref="SqliteStorageDialect{T}.ToDatabaseValue" /> — which
///         the load path uses — produces the lowercase canonical form. SQLite's default collation is
///         case-sensitive, so a document written that way could never be read back: every load
///         returned null, and every <c>json_each</c> id match failed, while string-identified
///         documents worked perfectly.
///     </para>
///     <para>
///         Converting here rather than teaching the dialect to emit uppercase keeps one Guid
///         representation across the whole store — the event tables already hold the lowercase form.
///     </para>
/// </remarks>
internal sealed class SqliteGuidIdentification<TDoc> : IIdentification<TDoc, Guid>
    where TDoc : notnull
{
    private readonly SequentialGuidIdentification<TDoc> _inner;

    public SqliteGuidIdentification(SequentialGuidIdentification<TDoc> inner)
    {
        _inner = inner;
    }

    public Guid Identity(TDoc document) => _inner.Identity(document);

    public Guid AssignIfMissing(TDoc document, ISequenceSource sequences)
        => _inner.AssignIfMissing(document, sequences);

    public object ToRawSqlValue(Guid id) => SqliteStorageDialect<Guid>.ToDatabaseValue(id);

    public Type RawSqlType => typeof(string);

    /// <summary>
    ///     Read explicitly rather than through <c>GetGuid</c>, so the round trip depends on Fisher's
    ///     own storage decision instead of the provider's coercion rules — the same reason the event
    ///     row readers convert by hand.
    /// </summary>
    public Guid ReadIdFromReader(DbDataReader reader, int columnOrdinal)
        => Guid.Parse(reader.GetString(columnOrdinal));
}

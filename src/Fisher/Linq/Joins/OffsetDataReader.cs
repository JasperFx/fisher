using System.Collections;
using System.Data.Common;

namespace Fisher.Linq.Joins;

/// <summary>
///     A read-only view of another <see cref="DbDataReader" /> whose columns start at an offset.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is what lets a join's inner side be materialized by its own storage's selector.</b>
///         A closed-shape selector reads from <em>fixed</em> positions — id at 0, data at 1, metadata
///         from 2 — which is a contract with the select list rather than a convenience, and a joined
///         row has the inner document's columns after the outer document's. Shifting the reader is the
///         only way to satisfy that contract without either side re-implementing what the selector does.
///     </para>
///     <para>
///         What it buys is exactness rather than tidiness. Polecat's join handler deserializes both
///         sides by calling the serializer on the <c>data</c> column directly, which silently loses
///         everything a selector does on top of that: a hierarchy's <c>doc_type</c> resolution — so an
///         inner document that is a sub-class comes back as its base, missing whatever the sub-class
///         added — and the metadata binders that populate a mapped version, deletion flag or timestamp.
///         This is the same trap <c>AdvancedSql</c> documents, answered the other way round: there the
///         restriction is that a document may only be the query's first result type, and here the reader
///         moves instead.
///     </para>
///     <para>
///         Only the members a selector reaches are meaningfully overridden; the rest delegate. Nothing
///         here advances the underlying reader — <see cref="Read" /> and <see cref="NextResult" /> are
///         the outer loop's job, and calling them through this view would consume a row the caller has
///         not finished with.
///     </para>
/// </remarks>
internal sealed class OffsetDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly int _offset;
    private readonly int _count;

    public OffsetDataReader(DbDataReader inner, int offset, int count)
    {
        _inner = inner;
        _offset = offset;
        _count = count;
    }

    private int Shift(int ordinal) => ordinal + _offset;

    public override int FieldCount => _count;

    public override object this[int ordinal] => _inner[Shift(ordinal)];

    public override object this[string name] => _inner[GetOrdinal(name)];

    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(Shift(ordinal));

    public override byte GetByte(int ordinal) => _inner.GetByte(Shift(ordinal));

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => _inner.GetBytes(Shift(ordinal), dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => _inner.GetChar(Shift(ordinal));

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => _inner.GetChars(Shift(ordinal), dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(Shift(ordinal));

    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(Shift(ordinal));

    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(Shift(ordinal));

    public override double GetDouble(int ordinal) => _inner.GetDouble(Shift(ordinal));

    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(Shift(ordinal));

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(Shift(ordinal));

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken token)
        => _inner.GetFieldValueAsync<T>(Shift(ordinal), token);

    public override float GetFloat(int ordinal) => _inner.GetFloat(Shift(ordinal));

    public override Guid GetGuid(int ordinal) => _inner.GetGuid(Shift(ordinal));

    public override short GetInt16(int ordinal) => _inner.GetInt16(Shift(ordinal));

    public override int GetInt32(int ordinal) => _inner.GetInt32(Shift(ordinal));

    public override long GetInt64(int ordinal) => _inner.GetInt64(Shift(ordinal));

    public override string GetName(int ordinal) => _inner.GetName(Shift(ordinal));

    /// <summary>
    ///     The first column of <em>this view</em> whose name matches.
    /// </summary>
    /// <remarks>
    ///     Not the underlying reader's answer minus the offset: a join selects <c>data</c> from both
    ///     sides, so the underlying reader would resolve the name to the outer side's column every
    ///     time. Nothing in the read path calls this — selectors read by position — but answering it
    ///     wrongly would be worse than not answering it.
    /// </remarks>
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _count; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException(name);
    }

    public override Stream GetStream(int ordinal) => _inner.GetStream(Shift(ordinal));

    public override string GetString(int ordinal) => _inner.GetString(Shift(ordinal));

    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(Shift(ordinal));

    public override object GetValue(int ordinal) => _inner.GetValue(Shift(ordinal));

    public override int GetValues(object[] values)
    {
        var read = Math.Min(values.Length, _count);

        for (var i = 0; i < read; i++)
        {
            values[i] = GetValue(i);
        }

        return read;
    }

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(Shift(ordinal));

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken token)
        => _inner.IsDBNullAsync(Shift(ordinal), token);

    public override int Depth => _inner.Depth;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override IEnumerator GetEnumerator() => throw NotAReader();

    public override bool NextResult() => throw NotAReader();

    public override bool Read() => throw NotAReader();

    private static NotSupportedException NotAReader()
        => new("An OffsetDataReader is a view of the current row of another reader; advance that one.");
}

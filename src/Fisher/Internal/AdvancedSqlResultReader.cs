using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Fisher.Serialization;
using Weasel.Storage;

namespace Fisher.Internal;

/// <summary>
///     Reads one result type out of a row for <see cref="IAdvancedSql" /> (fisher#34).
/// </summary>
/// <remarks>
///     <para>
///         Ported from Polecat's type of the same name, with two deliberate differences, both
///         SQLite-shaped.
///     </para>
///     <para>
///         <b>A scalar is read with <c>GetFieldValue&lt;T&gt;</c>, not <c>GetValue</c> plus
///         <c>Convert.ChangeType</c>.</b> Polecat can use the latter because SQL Server hands back the
///         CLR type already. Fisher stores a Guid as text, a timestamp as text and a bool as INTEGER,
///         so <c>GetValue</c> returns a <see cref="string" /> or a <see cref="long" /> — and
///         <c>Convert.ChangeType</c> to <see cref="Guid" /> throws outright, because
///         <see cref="Guid" /> is not <see cref="IConvertible" />. The typed accessor handles all three,
///         which <c>metadata_column_coercions</c> already pins against the exact shapes Fisher writes.
///     </para>
///     <para>
///         <b>A document is materialized by its own storage selector, not by deserializing a column.</b>
///         Polecat's reads <c>data</c> at an offset and hand-syncs metadata with a try/catch per
///         column. That would be wrong here in a way that is silent: Fisher's selectors resolve a
///         hierarchy's <c>doc_type</c> discriminator to the real sub-class, so hand-deserializing to
///         the declared type would quietly return the base type for every row of a hierarchy, missing
///         whatever the sub-class added. Going through the selector is also what keeps this read layout
///         from drifting from <c>LoadAsync</c>'s.
///     </para>
///     <para>
///         The price is that <see cref="ISelector{T}" /> resolves from fixed positions starting at
///         column 0, so a document type can only be read at the start of a row. That restriction is
///         enforced with a message naming it rather than left to produce a confusing cast error.
///     </para>
/// </remarks>
internal abstract class AdvancedSqlResultReader
{
    /// <summary>How many columns of the row this reader consumes.</summary>
    public abstract int ColumnCount { get; }

    /// <summary>Whether this reader must sit at column 0. True only for a document.</summary>
    public virtual bool MustLeadTheRow => false;

    public abstract object? ReadValue(DbDataReader reader, int startColumn);

    public static AdvancedSqlResultReader ForType(Type type, FisherSession session)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;

        if (ScalarReader.Handles(inner))
        {
            return new ScalarReader(type);
        }

        // A registered document type is one the schema has a mapping for. Asking rather than mapping
        // matters: MappingFor would *create* a mapping as a side effect of the question, which would
        // make every JSON result type accidentally become a document type.
        if (session.Options.Schema.HasMappingFor(type))
        {
            return DocumentReader.For(type, session);
        }

        return new JsonReader(type, session.FisherSerializer);
    }
}

/// <summary>
///     One column, read through the provider's typed accessor.
/// </summary>
/// <remarks>
///     <para>
///         Every accessor is <c>GetFieldValue&lt;T&gt;</c> rather than a <c>GetValue</c> the caller
///         casts. That is the provider's typed path, and <c>metadata_column_coercions</c> already pins
///         it against the exact shapes Fisher stores — a Guid as lowercase canonical text, a bool as
///         INTEGER 0/1, a timestamp in <c>SqliteTimestamp</c>'s fixed-width UTC form.
///     </para>
///     <para>
///         Note this is the one Fisher read path that leans on provider coercion by choice, where
///         <c>FisherEventsRowReader</c> converts explicitly. The reason the row readers do not is
///         round-trip symmetry: Fisher writes those columns explicitly, so reading them through a
///         convenience method would leave the round trip depending on rules Fisher does not own. Raw
///         SQL has no such symmetry to protect — the caller names arbitrary columns, including ones
///         Fisher never wrote — so the permissive path is the correct one here.
///     </para>
/// </remarks>
internal sealed class ScalarReader : AdvancedSqlResultReader
{
    private static readonly Dictionary<Type, Func<DbDataReader, int, object>> Accessors = new()
    {
        [typeof(string)] = (r, i) => r.GetString(i),
        [typeof(int)] = (r, i) => r.GetFieldValue<int>(i),
        [typeof(long)] = (r, i) => r.GetFieldValue<long>(i),
        [typeof(short)] = (r, i) => r.GetFieldValue<short>(i),
        [typeof(byte)] = (r, i) => r.GetFieldValue<byte>(i),
        [typeof(bool)] = (r, i) => r.GetFieldValue<bool>(i),
        [typeof(decimal)] = (r, i) => r.GetFieldValue<decimal>(i),
        [typeof(double)] = (r, i) => r.GetFieldValue<double>(i),
        [typeof(float)] = (r, i) => r.GetFieldValue<float>(i),
        [typeof(Guid)] = (r, i) => r.GetFieldValue<Guid>(i),
        [typeof(DateTime)] = (r, i) => r.GetFieldValue<DateTime>(i),
        [typeof(DateTimeOffset)] = (r, i) => r.GetFieldValue<DateTimeOffset>(i),
        [typeof(byte[])] = (r, i) => r.GetFieldValue<byte[]>(i)
    };

    private readonly Func<DbDataReader, int, object> _accessor;

    internal ScalarReader(Type type)
        => _accessor = Accessors[Nullable.GetUnderlyingType(type) ?? type];

    public static bool Handles(Type type) => Accessors.ContainsKey(type);

    public override int ColumnCount => 1;

    public override object? ReadValue(DbDataReader reader, int startColumn)
        => reader.IsDBNull(startColumn) ? null : _accessor(reader, startColumn);
}

/// <summary>
///     One column of JSON, deserialized to an arbitrary type.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "ISerializer.FromJson(Type, string) for raw-SQL projections. Result types flow in from the caller's QueryAsync<T>() and are preserved by the consumer per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "ISerializer.FromJson is annotated RDC. AOT consumers supply a source-generator-backed serializer.")]
internal sealed class JsonReader : AdvancedSqlResultReader
{
    private readonly Type _type;
    private readonly ISerializer _serializer;

    internal JsonReader(Type type, ISerializer serializer)
    {
        _type = type;
        _serializer = serializer;
    }

    public override int ColumnCount => 1;

    public override object? ReadValue(DbDataReader reader, int startColumn)
        => reader.IsDBNull(startColumn) ? null : _serializer.FromJson(_type, reader.GetString(startColumn));
}

/// <summary>
///     A registered document type, through the same query-only selector <c>session.Query&lt;T&gt;()</c>
///     materializes with.
/// </summary>
internal sealed class DocumentReader : AdvancedSqlResultReader
{
    private readonly Func<DbDataReader, object?> _resolve;

    private DocumentReader(int columnCount, Func<DbDataReader, object?> resolve)
    {
        ColumnCount = columnCount;
        _resolve = resolve;
    }

    public override int ColumnCount { get; }

    public override bool MustLeadTheRow => true;

    public override object? ReadValue(DbDataReader reader, int startColumn) => _resolve(reader);

    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification = "Closes the private Build<T> over a registered document type, which the consumer has already rooted by registering it.")]
    public static DocumentReader For(Type documentType, FisherSession session)
        => (DocumentReader)typeof(DocumentReader)
            .GetMethod(nameof(Build), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(documentType)
            .Invoke(null, [session])!;

    private static DocumentReader Build<T>(FisherSession session) where T : notnull
    {
        var storage = session.FisherDatabase.Providers.StorageFor<T>().QueryOnly;

        if (storage is not ISelectClause selectClause)
        {
            throw new InvalidOperationException(
                $"The storage for '{typeof(T).Name}' cannot produce a select clause, so it cannot be a "
                + "raw SQL result type.");
        }

        var selector = (ISelector<T>)selectClause.BuildSelector(session);

        return new DocumentReader(selectClause.SelectFields().Length, reader => selector.Resolve(reader));
    }
}

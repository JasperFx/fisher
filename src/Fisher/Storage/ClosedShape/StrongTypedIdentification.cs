using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using Weasel.Core.Identity;
using Weasel.Core.Sequences;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     Identity strategy for a strong-typed identifier — a wrapper standing in for one of Fisher's four
///     canonical identity types.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IIdentification{TDoc,TId}" /> was designed with these in mind: <c>ToRawSqlValue</c>,
///         <c>RawSqlType</c> and <c>ReadIdFromReader</c> exist precisely so a wrapper can present its
///         inner value at the ADO.NET boundary while the document keeps the wrapper. So this adds no
///         seam — it fills in the three the interface already reserves.
///     </para>
///     <para>
///         <b>Nothing downstream of <c>ToRawSqlValue</c> knows the id was wrapped.</b> The column holds
///         the inner value in exactly the representation an unwrapped id would have, which is what lets
///         the table shape, the write SQL and the positional <c>?</c> contract stay untouched.
///     </para>
///     <para>
///         <b>A Guid-backed wrapper goes through the same lowercase-canonical conversion as a raw one</b>
///         — see <see cref="SqliteGuidIdentification{TDoc}" /> for what happens without it. This is the
///         one place the wrapper could have quietly reintroduced that bug, because the conversion lives
///         in the identity strategy rather than in the dialect.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: compiles wrap/unwrap delegates from ValueTypeInfo. Strong-typed id types are preserved at the aggregate/document registration boundary on the caller side.")]
internal sealed class StrongTypedIdentification<TDoc, TId, TInner> : IIdentification<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
    where TInner : notnull
{
    private readonly Func<TDoc, TId> _getter;
    private readonly Action<TDoc, TId>? _setter;
    private readonly Func<TInner, TId> _wrap;
    private readonly Func<TId, TInner> _unwrap;
    private readonly Func<ISequenceSource, TInner>? _generate;

    /// <remarks>
    ///     Public on an internal type, matching the other strategies: <c>Activator.CreateInstance</c>
    ///     binds only public constructors, and the registry closes this generic by reflection.
    /// </remarks>
    public StrongTypedIdentification(ValueTypeInfo info, MemberInfo idMember,
        Func<ISequenceSource, TInner>? generate)
    {
        _getter = LambdaBuilder.Getter<TDoc, TId>(idMember)!;
        _setter = TrySetter(idMember);
        _wrap = info.CreateWrapper<TId, TInner>();
        _unwrap = info.UnWrapper<TId, TInner>();
        _generate = generate;
    }

    public TId Identity(TDoc document) => _getter(document);

    /// <summary>
    ///     Assign an id when the document has none.
    /// </summary>
    /// <remarks>
    ///     A default wrapper is the "unassigned" signal, exactly as <c>Guid.Empty</c> or a null string
    ///     is for a raw id — a <c>readonly record struct</c> over a default inner value compares equal
    ///     to <c>default</c>, so the same test works for both. When there is no way to generate one (a
    ///     string-backed wrapper, whose raw counterpart is externally assigned too), the document is
    ///     left alone and the caller finds out at the write rather than getting a fabricated key.
    /// </remarks>
    public TId AssignIfMissing(TDoc document, ISequenceSource sequences)
    {
        var current = _getter(document);

        if (!EqualityComparer<TId>.Default.Equals(current, default!))
        {
            return current;
        }

        if (_generate is null || _setter is null)
        {
            return current;
        }

        var assigned = _wrap(_generate(sequences));
        _setter(document, assigned);

        return assigned;
    }

    public object ToRawSqlValue(TId id)
    {
        var inner = _unwrap(id);

        // The Guid trap, one layer in. SqliteStorageDialect<Guid>.ToDatabaseValue passes a non-Guid
        // through untouched, so this is safe for every backing type.
        return SqliteStorageDialect<Guid>.ToDatabaseValue(inner);
    }

    /// <summary>
    ///     The inner type — except for a Guid, which is stored and bound as TEXT.
    /// </summary>
    public Type RawSqlType => typeof(TInner) == typeof(Guid) ? typeof(string) : typeof(TInner);

    /// <summary>
    ///     Read the inner value and wrap it — the provider cannot materialise the wrapper itself.
    /// </summary>
    /// <remarks>
    ///     A Guid is parsed from its text rather than read through <c>GetGuid</c>, for the same reason
    ///     <see cref="SqliteGuidIdentification{TDoc}" /> does: the round trip should depend on Fisher's
    ///     storage decision, not the provider's coercion rules.
    /// </remarks>
    public TId ReadIdFromReader(DbDataReader reader, int columnOrdinal)
    {
        object inner = typeof(TInner) == typeof(Guid)
            ? Guid.Parse(reader.GetString(columnOrdinal))
            : reader.GetFieldValue<TInner>(columnOrdinal);

        return _wrap((TInner)inner);
    }

    private static Action<TDoc, TId>? TrySetter(MemberInfo member)
    {
        var settable = member switch
        {
            PropertyInfo property => property.GetSetMethod(nonPublic: true) is not null,
            FieldInfo field => !field.IsInitOnly,
            _ => false
        };

        return settable ? LambdaBuilder.Setter<TDoc, TId>(member) : null;
    }
}

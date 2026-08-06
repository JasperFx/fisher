using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.Events;

namespace Fisher.Storage;

/// <summary>
///     Resolves an aggregate type's identity member and identity type.
/// </summary>
/// <remarks>
///     <para>
///         Marten and Polecat answer this question through <c>DocumentMapping</c>, which they need
///         anyway for document storage. Fisher has no document storage yet, and live aggregation only
///         needs the two facts below, so this stands alone until <c>DocumentMapping</c> lands — at
///         which point that type should resolve identity <em>through</em> here rather than beside it,
///         so the live-aggregation and snapshot paths cannot drift into disagreeing about what
///         <c>TId</c> is for a given aggregate.
///     </para>
///     <para>
///         Resolution itself is delegated to <see cref="DocumentIdentity" />, the same shared JasperFx
///         helper Polecat uses: an <c>[Identity]</c>-marked member first, then a case-insensitive
///         <c>Id</c> property, then an <c>Id</c> field.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
    Justification =
        "Class-level: reflects over the aggregate type's public members to find its identity. Aggregate types are preserved at the AggregateStreamAsync<T> / projection registration boundary on the caller side.")]
internal static class AggregateIdentity
{
    private static readonly ConcurrentDictionary<Type, MemberInfo?> IdMembers = new();

    /// <summary>
    ///     The identity member of an aggregate type, or null when it has none.
    /// </summary>
    /// <remarks>
    ///     The predicate overload, not the default one: <c>DocumentIdentity</c>'s default accepts only
    ///     the four canonical scalars, and a strong-typed id is a wrapper around one. The overload
    ///     exists so a store can widen exactly this — see <see cref="StrongTypedId.IsSupportedIdType" />.
    /// </remarks>
    internal static MemberInfo? FindIdMember(Type aggregateType)
        => IdMembers.GetOrAdd(aggregateType,
            static type => DocumentIdentity.FindIdMember(type, StrongTypedId.IsSupportedIdType));

    /// <summary>
    ///     The identity type to close <see cref="Projections.SingleStreamProjection{TDoc,TId}" /> over
    ///     for <paramref name="aggregateType" />.
    /// </summary>
    /// <remarks>
    ///     An identity member is required even though live aggregation never reads one — it takes
    ///     <c>TId</c> from the stream. What needs it is JasperFx's source generator, which keys the
    ///     dispatcher it emits on <c>(TDoc, TId)</c> and skips a type it cannot resolve an identity for.
    ///     There is no runtime fallback for conventional <c>Apply</c> / <c>Create</c> dispatch, so
    ///     defaulting to the stream identity primitive here would only push the failure to
    ///     <c>AssembleAndAssertValidity</c> with a message about a missing generated dispatcher rather
    ///     than about the missing <c>Id</c> that actually caused it.
    /// </remarks>
    internal static Type ResolveIdType(Type aggregateType, StreamIdentity streamIdentity)
    {
        var streamIdType = streamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

        var member = FindIdMember(aggregateType)
            ?? throw new InvalidOperationException(
                $"Aggregate type '{aggregateType.FullName}' has no identity member. Single stream " +
                $"aggregates need a public {streamIdType.Name} member named 'Id', or one marked with " +
                "[Identity].");

        // Unwrap Nullable<T> so a `PublicId? Id` closes the projection over PublicId. The source
        // generator unwraps the same way; a mismatch here means the generated evolver would never be
        // matched to the runtime projection.
        var idType = MemberType(member);
        idType = Nullable.GetUnderlyingType(idType) ?? idType;

        // A wrapper is checked through the type it wraps, so a Guid-backed id on a string-identity
        // store fails here rather than at the first write. The wrapper itself is still what the
        // projection closes over — the generated dispatcher is keyed on it.
        var comparable = StrongTypedId.StoredTypeFor(idType);

        if ((comparable == typeof(Guid) || comparable == typeof(string)) && comparable != streamIdType)
        {
            throw new InvalidOperationException(
                $"Aggregate type '{aggregateType.FullName}' has an identity member '{member.Name}' of type " +
                $"{idType.Name}, but this event store is configured for {streamIdentity} stream identity. " +
                $"Single stream aggregates take their identity from the stream, so the two must agree.");
        }

        return idType;
    }

    /// <summary>
    ///     Assign the stream's identity to a freshly aggregated document, when the aggregate has a
    ///     settable identity member of a compatible type.
    /// </summary>
    /// <remarks>
    ///     Live aggregation folds events without ever consulting the aggregate's own id, so an
    ///     aggregate whose <c>Create</c> method does not set <c>Id</c> would otherwise come back with a
    ///     default one. Marten and Polecat both backfill it here rather than in the aggregator.
    /// </remarks>
    internal static void TrySetIdentity(object aggregate, object streamId)
    {
        var member = FindIdMember(aggregate.GetType());

        switch (member)
        {
            case PropertyInfo { CanWrite: true } property when property.PropertyType.IsInstanceOfType(streamId):
                property.SetValue(aggregate, streamId);
                break;

            case FieldInfo { IsInitOnly: false } field when field.FieldType.IsInstanceOfType(streamId):
                field.SetValue(aggregate, streamId);
                break;
        }
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new ArgumentOutOfRangeException(nameof(member),
            $"'{member.Name}' is neither a property nor a field.")
    };
}

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;

namespace Fisher.Storage;

/// <summary>
///     Recognising a strong-typed identifier — a wrapper struct or class standing in for one of
///     Fisher's four canonical identity types.
/// </summary>
/// <remarks>
///     <para>
///         The shape is JasperFx's, described by <see cref="ValueTypeInfo.ForType" />: exactly one
///         public gettable instance property, plus either a constructor taking that property's type or
///         a public static builder taking it. A <c>readonly record struct Foo(Guid Value)</c> satisfies
///         both clauses.
///     </para>
///     <para>
///         <b>Fisher discovers these rather than requiring registration</b>, which is Polecat's model
///         rather than Marten's — Marten needs a value type registered before it can use it in LINQ and
///         identity mapping. That is why <c>IComplianceStoreRegistrar.RegisterValueType&lt;T&gt;</c> is
///         a no-op here.
///     </para>
///     <para>
///         <see cref="ValueTypeInfo.ForType" /> <em>throws</em> for a type that is not a value wrapper,
///         and it is asked about every candidate identity member of every aggregate — so the answer is
///         cached, including the negative one. Without that, resolving the identity of an ordinary
///         aggregate would raise and swallow an exception on every call.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: ValueTypeInfo.ForType reflects over the wrapper's properties, constructors and static builders. Strong-typed id types are preserved at the aggregate/document registration boundary on the caller side.")]
[UnconditionalSuppressMessage("Trimming", "IL2067:UnrecognizedReflectionPattern",
    Justification = "See above.")]
internal static class StrongTypedId
{
    private static readonly ConcurrentDictionary<Type, ValueTypeInfo?> Resolved = new();

    /// <summary>
    ///     The identity types a wrapper is allowed to wrap — the same four
    ///     <see cref="DocumentMapping.SupportedIdTypes" /> allows raw.
    /// </summary>
    private static readonly HashSet<Type> WrappableTypes =
        [typeof(Guid), typeof(string), typeof(int), typeof(long)];

    /// <summary>
    ///     Whether this type is a strong-typed identifier Fisher can store, and its shape if so.
    /// </summary>
    internal static bool TryResolve(Type type, [NotNullWhen(true)] out ValueTypeInfo? info)
    {
        info = Resolved.GetOrAdd(type, static candidate =>
        {
            // Cheap exclusions first, so the common case never reaches the throwing resolver: a raw
            // identity type, a primitive, an enum, or anything the framework owns.
            if (WrappableTypes.Contains(candidate)
                || candidate.IsPrimitive
                || candidate.IsEnum
                || candidate.IsArray
                || candidate.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                return null;
            }

            try
            {
                var resolved = ValueTypeInfo.ForType(candidate);
                return WrappableTypes.Contains(resolved.SimpleType) ? resolved : null;
            }
            catch (Exception)
            {
                // ForType throws InvalidValueTypeException for a type with no single gettable property
                // or no way to build one. That is the ordinary answer for most types, not an error.
                return null;
            }
        });

        return info is not null;
    }

    /// <summary>
    ///     Resolve a wrapper eagerly, throwing when the type cannot be one — what
    ///     <c>StoreOptions.RegisterValueType</c> is (fisher#75).
    /// </summary>
    /// <remarks>
    ///     The difference from <see cref="TryResolve" /> is the whole value of the call. Discovery has
    ///     to treat "not a wrapper" as the ordinary answer, because it is asked about every candidate
    ///     identity member of every type; a caller who has <em>named</em> a type is asserting it is one,
    ///     so the same answer is a configuration error and says so here rather than surfacing later as
    ///     "has no identity member" from somewhere that cannot mention the wrapper.
    /// </remarks>
    /// <exception cref="InvalidValueTypeException"><paramref name="type" /> is not a usable wrapper.</exception>
    internal static ValueTypeInfo Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (TryResolve(type, out var info))
        {
            return info;
        }

        // Two different failures, and telling them apart is worth the second resolve: a shape problem
        // is answered by ValueTypeInfo's own message, where a perfectly good wrapper around an
        // unsupported inner type needs to be told which four Fisher can store.
        try
        {
            var resolved = ValueTypeInfo.ForType(type);

            throw new InvalidValueTypeException(type,
                $"It wraps a {resolved.SimpleType.FullNameInCode()}, and Fisher stores an identity as a "
                + "Guid, string, int or long.");
        }
        catch (InvalidValueTypeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new InvalidValueTypeException(type, e.Message);
        }
    }

    /// <summary>
    ///     The type actually stored for an identity — a wrapper's inner type, or the type itself.
    /// </summary>
    internal static Type StoredTypeFor(Type idType)
        => TryResolve(idType, out var info) ? info.SimpleType : idType;

    /// <summary>
    ///     Whether Fisher can use this type as an identity at all: one of the canonical four, or a
    ///     wrapper around one.
    /// </summary>
    /// <remarks>
    ///     This is the predicate handed to <c>DocumentIdentity.FindIdMember</c>, whose default only
    ///     accepts the canonical four — the overload exists precisely so a store can widen it.
    /// </remarks>
    internal static bool IsSupportedIdType(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        return DocumentMapping.SupportedIdTypes.Contains(unwrapped) || TryResolve(unwrapped, out _);
    }
}

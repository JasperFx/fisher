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

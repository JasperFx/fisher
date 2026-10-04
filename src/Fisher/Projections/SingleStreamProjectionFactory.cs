using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace Fisher.Projections;

/// <summary>
///     Closes <see cref="SingleStreamProjection{TDoc,TId}" /> over an aggregate and its identity type —
///     the projection both <c>Projections.Snapshot&lt;T&gt;()</c> and live aggregation build (fisher#398).
/// </summary>
/// <remarks>
///     <para>
///         <b>Ordinary generic calls for the four canonical identity types, because those work in a
///         Native AOT image.</b> The aggregate type is a generic argument at every caller, so
///         <c>new SingleStreamProjection&lt;TDoc, Guid&gt;()</c> is an instantiation ILC can see and
///         compile. Both callers used to close the type with <c>MakeGenericType</c>, which has no
///         native code: a store registering any snapshot could not be built, and live aggregation was
///         unreachable. #384 made the same change to document storage.
///     </para>
///     <para>
///         <b>A strong-typed id wrapper is still closed reflectively</b>, because its type is a runtime
///         value here. Under the JIT that works as before. In a native image it is refused by name,
///         and closing it statically would not help yet: JasperFx's own single-stream projection
///         constructor builds its identity sources with <c>IEvent.CreateAggregateIdentitySource</c> and
///         <c>StreamAction.CreateAggregateIdentitySource</c>, which compile with FastExpressionCompiler
///         and throw in a native image (jasperfx#950, measured in smoke/aot-consumer against JasperFx
///         2.80.1). jasperfx#942 fixed <c>ValueTypeInfo</c>'s half of this in 2.80.0, which is what
///         strong-typed document ids needed, but not these two. The refusal says so rather than failing
///         inside JasperFx.
///     </para>
/// </remarks>
internal static class SingleStreamProjectionFactory
{
    internal static ProjectionBase Create<TDoc>(Type idType) where TDoc : notnull
    {
        if (idType == typeof(Guid)) return new SingleStreamProjection<TDoc, Guid>();
        if (idType == typeof(string)) return new SingleStreamProjection<TDoc, string>();
        if (idType == typeof(int)) return new SingleStreamProjection<TDoc, int>();
        if (idType == typeof(long)) return new SingleStreamProjection<TDoc, long>();

        return CreateReflectively(typeof(TDoc), idType);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification =
            "Strong-typed identities only, whose wrapper type is a runtime value here. Refused by name in a Native AOT image before MakeGenericType is reached (fisher#398, jasperfx#950).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Strong-typed identities only; see the IL3050 justification.")]
    private static ProjectionBase CreateReflectively(Type documentType, Type idType)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Fisher cannot build the single-stream projection for '{documentType.FullName}' in a Native " +
                $"AOT image, because its identity type '{idType.Name}' is a strong-typed wrapper. JasperFx " +
                "compiles a wrapper-keyed aggregate's identity sources with FastExpressionCompiler, which throws in a " +
                "native image (jasperfx#950). Key the aggregate on a Guid, string, int or long until that ships. " +
                "See fisher#398 and fisher#423.");
        }

        return typeof(SingleStreamProjection<,>).CloseAndBuildAs<ProjectionBase>(documentType, idType);
    }
}

using System.Collections.Concurrent;
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
///         <b>A strong-typed id is closed statically when the configuration named it</b> (fisher#412):
///         <c>Projections.Snapshot&lt;T, TId&gt;()</c> or <c>Projections.LiveStreamAggregation&lt;T, TId&gt;()</c>
///         records a factory while both types are still generic arguments, and every later build of
///         that aggregate's projection uses it. That is #386's answer for documents one layer over.
///         The JasperFx half was its projection constructor compiling the wrapper's accessors with
///         FastExpressionCompiler: jasperfx#942 (<c>ValueTypeInfo</c>, 2.80.0) and jasperfx#950 (the
///         aggregate identity sources, 2.80.2).
///     </para>
///     <para>
///         <b>An undeclared wrapper is still closed reflectively</b>, which works under the JIT and is
///         refused by name in a native image. A <c>readonly record struct</c> is a value type, so
///         Native AOT has no shared instantiation to fall back on and <c>MakeGenericType</c> has
///         nothing to run.
///     </para>
/// </remarks>
internal sealed class SingleStreamProjectionFactory
{
    private readonly ConcurrentDictionary<Type, Func<ProjectionBase>> _declared = new();

    /// <summary>
    ///     Record that <typeparamref name="TDoc" /> is keyed on <typeparamref name="TId" />, closing its
    ///     projection statically from now on.
    /// </summary>
    internal void Declare<TDoc, TId>() where TDoc : notnull where TId : notnull
        => _declared[typeof(TDoc)] = static () => new SingleStreamProjection<TDoc, TId>();

    /// <summary>Whether <paramref name="aggregateType" />'s identity type was declared.</summary>
    internal bool IsDeclared(Type aggregateType) => _declared.ContainsKey(aggregateType);

    internal ProjectionBase Create<TDoc>(Type idType) where TDoc : notnull
    {
        if (idType == typeof(Guid)) return new SingleStreamProjection<TDoc, Guid>();
        if (idType == typeof(string)) return new SingleStreamProjection<TDoc, string>();
        if (idType == typeof(int)) return new SingleStreamProjection<TDoc, int>();
        if (idType == typeof(long)) return new SingleStreamProjection<TDoc, long>();

        return _declared.TryGetValue(typeof(TDoc), out var declared)
            ? declared()
            : CreateReflectively(typeof(TDoc), idType);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification =
            "Undeclared strong-typed identities only, whose wrapper type is a runtime value here. Refused by name in a Native AOT image before MakeGenericType is reached (fisher#398, fisher#412).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Undeclared strong-typed identities only; see the IL3050 justification.")]
    private static ProjectionBase CreateReflectively(Type documentType, Type idType)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Fisher cannot build the single-stream projection for '{documentType.FullName}' in a Native " +
                $"AOT image, because its identity type '{idType.Name}' is a strong-typed wrapper that the " +
                "configuration never named. Register the aggregate with " +
                $"Projections.Snapshot<{documentType.Name}, {idType.Name}>(...) or " +
                $"Projections.LiveStreamAggregation<{documentType.Name}, {idType.Name}>(), so its projection " +
                "is closed while both types are still generic arguments. See fisher#412.");
        }

        return typeof(SingleStreamProjection<,>).CloseAndBuildAs<ProjectionBase>(documentType, idType);
    }
}

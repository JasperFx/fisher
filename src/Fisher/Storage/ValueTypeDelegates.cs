using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JasperFx.Core.Reflection;

namespace Fisher.Storage;

/// <summary>
///     <see cref="ValueTypeInfo.CreateWrapper{TOuter,TInner}" /> and
///     <see cref="ValueTypeInfo.UnWrapper{TOuter,TInner}" />, with a Native AOT fallback.
/// </summary>
/// <remarks>
///     <para>
///         <b>A local workaround for <see href="https://github.com/JasperFx/jasperfx/issues/942">jasperfx#942</see>,
///         to be deleted when it ships.</b> Both JasperFx methods compile with FastExpressionCompiler
///         unconditionally, which emits IL and throws <c>PlatformNotSupportedException</c> in a native
///         image. <c>LambdaBuilder</c> beside them already branches on
///         <see cref="RuntimeFeature.IsDynamicCodeSupported" />; these do the same (fisher#386).
///     </para>
///     <para>
///         Deliberately free of Fisher types, so moving it upstream is a file move. Under the JIT it is
///         exactly the JasperFx call; the reflection fallback runs only where compiling cannot.
///     </para>
/// </remarks>
internal static class ValueTypeDelegates
{
    [RequiresUnreferencedCode("Compiles or reflects over the wrapper's constructor or builder.")]
    internal static Func<TInner, TOuter> WrapperFor<TOuter, TInner>(ValueTypeInfo info)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return info.CreateWrapper<TOuter, TInner>();
        }

        if (info.Builder is { } builder)
        {
            return inner => (TOuter)builder.Invoke(null, [inner])!;
        }

        var ctor = info.Ctor ?? throw new NotSupportedException(
            $"Cannot build a type converter for strong typed id type {info.OuterType.FullName}");

        return inner => (TOuter)ctor.Invoke([inner]);
    }

    [RequiresUnreferencedCode("Compiles or reflects over the wrapper's value property.")]
    internal static Func<TOuter, TInner> UnwrapperFor<TOuter, TInner>(ValueTypeInfo info)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return info.UnWrapper<TOuter, TInner>();
        }

        var property = info.ValueProperty;
        return outer => (TInner)property.GetValue(outer)!;
    }
}

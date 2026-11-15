using System.Reflection;
using System.Runtime.CompilerServices;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#550, Critter Stack plan decision D4: Fisher's net11.0 assemblies compile with runtime-async on by default
///     (<c>FisherRuntimeAsync</c> in Directory.Build.props), and its net10.0 assemblies cannot, because that runtime
///     will not load runtime-async code. Modelled on JasperFx's <c>runtime_async_defaults</c> and Wolverine's
///     <c>runtime_async_defaults_5150</c>.
/// </summary>
/// <remarks>
///     A property in Directory.Build.props that stops applying produces no build error and no failing behaviour test,
///     because async code works the same either way. It shows up only as allocations, so the assembly's own metadata
///     is what this checks. Fisher builds nothing at runtime through JasperFx's <c>GenerationRules</c>, so there is no
///     generated-code half to pin here.
/// </remarks>
public class runtime_async_defaults
{
    // MethodImplAttributes.Async, the flag the compiler sets on a runtime-async method.
    private const MethodImplAttributes AsyncFlag = (MethodImplAttributes)0x2000;

    [Fact]
    public void fisher_is_compiled_with_runtime_async_only_on_net11()
    {
        var types = typeof(DocumentStore).Assembly.GetTypes();

        var runtimeAsyncMethods = types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => (m.MethodImplementationFlags & AsyncFlag) != 0);

        var stateMachines = types
            .Where(t => typeof(IAsyncStateMachine).IsAssignableFrom(t) && t.IsDefined(typeof(CompilerGeneratedAttribute)))
            .ToArray();

#if NET11_0_OR_GREATER
        runtimeAsyncMethods.ShouldBeGreaterThan(0);

        // Async iterators are not converted by runtime-async, so only their state machines remain.
        stateMachines.Where(t => !IsAsyncIteratorStateMachine(t)).ShouldBeEmpty();
#else
        runtimeAsyncMethods.ShouldBe(0);
        stateMachines.ShouldNotBeEmpty();
#endif
    }

    // An async iterator's state machine also implements IAsyncEnumerator<T>.
    private static bool IsAsyncIteratorStateMachine(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType &&
                                      i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));
}

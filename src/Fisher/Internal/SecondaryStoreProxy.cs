using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Fisher.Internal;

/// <summary>
///     Presents a real <see cref="DocumentStore" /> as a marker interface, so several independently
///     configured stores can live in one container and be resolved apart (fisher#46).
/// </summary>
/// <remarks>
///     <para>
///         <b>Built on <see cref="DispatchProxy" />, which is in the BCL</b> — no proxy library, no
///         code generation, no new dependency. A marker interface is by definition empty apart from
///         what it inherits from <see cref="IDocumentStore" />, so every call it can receive is one the
///         wrapped store already implements and forwarding is a single reflective invoke.
///     </para>
///     <para>
///         A member declared on the marker itself rather than inherited would have nothing to forward
///         to and throws saying so. That is the right answer: a marker interface is a name, and giving
///         it behaviour would mean asking Fisher to implement something it has never seen.
///     </para>
/// </remarks>
internal class SecondaryStoreProxy : DispatchProxy
{
    private IDocumentStore _inner = null!;

    internal static T For<T>(IDocumentStore store) where T : class, IDocumentStore
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException(
                $"'{typeof(T).Name}' must be an interface that extends IDocumentStore. A secondary store "
                + "is identified by a marker interface, which is what lets the container tell two stores "
                + "apart — a concrete type would be a second store, not a second name for one.",
                nameof(T));
        }

        var proxy = Create<T, SecondaryStoreProxy>()!;
        ((SecondaryStoreProxy)(object)proxy)._inner = store;

        return proxy;
    }

    /// <summary>
    ///     The real store behind a proxy, or the argument itself when it is not one.
    /// </summary>
    /// <remarks>
    ///     Needed because <see cref="DispatchProxy" /> implements the interfaces it was asked for and no
    ///     others — so a proxy over a marker is not an <c>IEventStore</c>, the tooling surfaces being
    ///     implemented explicitly and deliberately absent from <see cref="IDocumentStore" /> (fisher#45).
    /// </remarks>
    internal static IDocumentStore Unwrap(IDocumentStore store)
        => (object)store is SecondaryStoreProxy proxy ? proxy._inner : store;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // Rethrown with its stack intact. Without this every exception from a secondary store
            // arrives as a TargetInvocationException wrapping the real one, which is a proxy detail
            // leaking into an application's catch blocks.
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

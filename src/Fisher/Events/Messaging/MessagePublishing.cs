using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Events;

namespace Fisher.Events.Messaging;

/// <summary>
///     Dispatches a side-effect message of a runtime-known type to
///     <see cref="IMessageSink.PublishAsync{T}(T, string)" />.
/// </summary>
/// <remarks>
///     <para>
///         The seam is generic on the message type but JasperFx hands the batch an <see cref="object" />
///         — <c>IProjectionBatch.PublishMessageAsync(object, string)</c> — so the generic method has to
///         be closed at runtime. Doing that with <c>MakeGenericMethod(...).Invoke(...)</c> on every
///         publish is the obvious version and the slow one; a compiled delegate cached per message type
///         pays the cost once. Polecat reached the same shape via polecat#46, with
///         <c>FastExpressionCompiler</c> where this uses the BCL's <see cref="Expression{TDelegate}.Compile" />
///         — Fisher does not take that dependency for one call site.
///     </para>
///     <para>
///         Closing a generic method over a runtime type is not AOT-safe, hence the annotations. An AOT
///         consumer publishing side effects has to pre-register its message types; a store with no bus
///         integration never reaches here at all, because <c>NulloMessageOutbox</c> is not routed
///         through this.
///     </para>
/// </remarks>
internal static class MessagePublishing
{
    // PublishAsync<T> takes T as its FIRST parameter, so a GetMethod(name, [typeof(object), ...])
    // lookup misses — the generic parameter does not match. Filter on arity and the second
    // parameter's type instead, which is what distinguishes the two overloads.
    private static readonly MethodInfo _withTenant = typeof(IMessageSink)
        .GetMethods()
        .First(m => m.Name == nameof(IMessageSink.PublishAsync)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is { Length: 2 } parameters
                    && parameters[1].ParameterType == typeof(string));

    private static readonly MethodInfo _withMetadata = typeof(IMessageSink)
        .GetMethods()
        .First(m => m.Name == nameof(IMessageSink.PublishAsync)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is { Length: 2 } parameters
                    && parameters[1].ParameterType == typeof(MessageMetadata));

    private static readonly ConcurrentDictionary<Type, Func<IMessageSink, object, string, ValueTask>>
        _tenantPublishers = new();

    private static readonly ConcurrentDictionary<Type, Func<IMessageSink, object, MessageMetadata, ValueTask>>
        _metadataPublishers = new();

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes IMessageSink.PublishAsync<T> over the runtime message type on the per-type cache miss. AOT consumers that publish projection side effects pre-register their message types.")]
    internal static ValueTask PublishAsync(IMessageSink sink, object message, string tenantId)
        => _tenantPublishers.GetOrAdd(message.GetType(), BuildTenantPublisher)(sink, message, tenantId);

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes IMessageSink.PublishAsync<T> over the runtime message type on the per-type cache miss. AOT consumers that publish projection side effects pre-register their message types.")]
    internal static ValueTask PublishAsync(IMessageSink sink, object message, MessageMetadata metadata)
        => _metadataPublishers.GetOrAdd(message.GetType(), BuildMetadataPublisher)(sink, message, metadata);

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    private static Func<IMessageSink, object, string, ValueTask> BuildTenantPublisher(Type messageType)
        => Build<Func<IMessageSink, object, string, ValueTask>>(_withTenant, messageType, typeof(string),
            "tenantId");

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    private static Func<IMessageSink, object, MessageMetadata, ValueTask> BuildMetadataPublisher(Type messageType)
        => Build<Func<IMessageSink, object, MessageMetadata, ValueTask>>(_withMetadata, messageType,
            typeof(MessageMetadata), "metadata");

    [RequiresDynamicCode("Closes a generic method over a runtime type.")]
    private static TDelegate Build<TDelegate>(MethodInfo definition, Type messageType, Type secondParameterType,
        string secondParameterName) where TDelegate : Delegate
    {
        var closed = definition.MakeGenericMethod(messageType);

        var sink = Expression.Parameter(typeof(IMessageSink), "sink");
        var message = Expression.Parameter(typeof(object), "message");
        var second = Expression.Parameter(secondParameterType, secondParameterName);

        var call = Expression.Call(sink, closed, Expression.Convert(message, messageType), second);

        return Expression.Lambda<TDelegate>(call, sink, message, second).Compile();
    }
}

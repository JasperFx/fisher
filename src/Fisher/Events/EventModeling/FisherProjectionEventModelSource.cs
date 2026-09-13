using Fisher.Internal;
using JasperFx.Events;
using JasperFx.Events.EventModeling;

namespace Fisher.Events.EventModeling;

/// <summary>
///     The store-derived Event Model rung (fisher#251, jasperfx#825), reading its model NAME off the
///     store's own <see cref="StoreOptions.EventModelName" /> rather than being told at registration.
/// </summary>
/// <remarks>
///     <para>
///         <b>fisher#276.</b> The name used to be an optional <c>eventModelName</c> parameter on every
///         <c>AddFisher</c> / <c>AddFisherStore&lt;T&gt;</c> overload, shipped in 1.8.0. That broke
///         binary compatibility — an optional argument binds at the call site, so an assembly that is
///         not recompiled throws <c>MissingMethodException</c> even though its source still compiles —
///         and on Marten the same shape silently stole every <c>AddMarten(connectionString)</c> call,
///         because C# prefers the candidate with no omitted optional parameters. Configuration that
///         belongs to a store goes on <see cref="StoreOptions" />.
///     </para>
///     <para>
///         <b>Resolving the store inside <see cref="TryCreateAsync" /> rather than capturing a name at
///         registration also retires the ordering caveat the parameter came with.</b> The options are
///         read when the model is assembled, so <c>AddEventModel("Something", …)</c> may be called
///         before or after <c>AddFisher</c> and either way lands on one model. fisher#271 documented
///         "the store cannot infer the name" as a constraint; it was a consequence of capturing early,
///         not a fact about the problem.
///     </para>
///     <para>
///         The mapping itself stays JasperFx's. This delegates to a
///         <see cref="ProjectionEventModelSource" /> — which is <c>sealed</c>, hence composition rather
///         than a subclass — so Fisher owns only the three things it alone knows: which service the
///         store is registered under, where its name came from, and which store the slices came from.
///     </para>
/// </remarks>
internal sealed class FisherProjectionEventModelSource : IEventModelDefinitionSource
{
    private readonly Func<IServiceProvider, IDocumentStore> _store;

    public FisherProjectionEventModelSource(Func<IServiceProvider, IDocumentStore> store, Uri? subject = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        if (subject is not null)
        {
            Subject = subject;
        }
    }

    /// <summary>
    ///     Which store these slices were read out of. An ancillary store carries one of its own.
    /// </summary>
    /// <remarks>
    ///     That distinction matters more on Fisher than on either sibling, because two Fisher stores
    ///     are usually two <em>files</em> — the same reason <c>FisherSystemPart&lt;T&gt;</c> draws it
    ///     for the command line (fisher#172).
    /// </remarks>
    public Uri Subject { get; } = new("event-model://projections");

    /// <inheritdoc />
    /// <remarks>
    ///     Derived, not declared: these roles are read out of the store's projection registry rather
    ///     than written down by anybody. That is what lets them win over a declaration that disagrees.
    /// </remarks>
    public EventModelProvenance Provenance => EventModelProvenance.Derived;

    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var store = _store(services);

        // Through IDocumentStore, which an ancillary store's marker proxy implements -- so this reads
        // the real store's options without unwrapping. The IEventStore cast below cannot: a
        // DispatchProxy implements only the interfaces it was asked for, and Fisher's DocumentStore
        // implements IEventStore EXPLICITLY (fisher#45), so it is not on IDocumentStore at all.
        var modelName = store.Options.EventModelName ?? ProjectionEventModelSource.DefaultModelName;

        var inner = new ProjectionEventModelSource((IEventStore)SecondaryStoreProxy.Unwrap(store))
        {
            ModelName = modelName,
            Subject = Subject
        };

        return inner.TryCreateAsync(services, token);
    }
}

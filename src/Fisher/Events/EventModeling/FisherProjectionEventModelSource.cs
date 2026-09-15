using Fisher.Internal;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Events.EventModeling;

/// <summary>
///     The store-derived Event Model rung (fisher#251, jasperfx#825), reading its model NAME off the
///     store's own <see cref="StoreOptions.EventModelName" /> rather than being told at registration —
///     and falling back to the service name rather than to a literal (fisher#280).
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
        var modelName = ResolveModelName(store, services);

        var inner = new ProjectionEventModelSource((IEventStore)SecondaryStoreProxy.Unwrap(store))
        {
            ModelName = modelName,
            Subject = Subject
        };

        return inner.TryCreateAsync(services, token);
    }

    /// <summary>
    ///     The model this store's slices contribute to: what the store was told, else the service
    ///     name, else the shared literal (fisher#280).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>The literal <c>"EventModel"</c> is the one default guaranteed to be wrong for
    ///         every host, and it was the root cause of fisher#271 rather than an incidental
    ///         choice.</b> Every other contributor to a canvas defaults to something meaningful —
    ///         Wolverine's chains and HTTP endpoints to the service name, a Bobcat spec assembly to
    ///         its own name, a curated file to its <c>model:</c> value — so the overwhelmingly common
    ///         host, Wolverine plus one store, assembled <b>two</b> models out of the box. Threading a
    ///         name through <c>AddFisher</c> (fisher#271) and then onto
    ///         <see cref="StoreOptions" /> (fisher#276) fixed the symptom twice; this is the default.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>"the service name" is deliberately not a property name, because fisher#284 found
    ///         that naming one was wrong.</b> The paragraph above used to say Wolverine's chains
    ///         contribute under <c>JasperFxOptions.ServiceName</c>. They do not:
    ///         <c>WolverineEventModelSource</c> names its model from <c>WolverineOptions.ServiceName</c>,
    ///         and <c>WolverineOptions.ReadJasperFxOptions</c> only ever did
    ///         <c>ServiceName ??= jasperfx.ServiceName</c> — reading FROM JasperFx, with nothing
    ///         carrying the value back. So <c>opts.ServiceName = "Ledgers"</c>, the documented way to
    ///         name a Wolverine service, left the property <see cref="ResolveModelName" /> reads at
    ///         ITS default, the entry assembly name, and the canvas split in two exactly as it did
    ///         before fisher#280. It only ever looked correct where the two defaults coincide — a host
    ///         whose assembly is named what its service is named agrees with itself by accident, which
    ///         is why it survived a release.
    ///     </para>
    ///     <para>
    ///         <b>Fixed upstream in wolverine#4448 and shipped in Wolverine 6.38.0</b>, which writes
    ///         the resolved Wolverine <c>ServiceName</c> back into <c>JasperFxOptions</c> — so all
    ///         three Critter Stack stores inherit it and this fallback is correct as written, with no
    ///         Fisher change. Fixed there rather than here on purpose: Fisher references neither
    ///         Wolverine nor a host that sets a Wolverine service name, so the alternative was a loose
    ///         type lookup repeated once per store. The regression test lives in Wolverine for the
    ///         same reason (<c>service_name_reaches_jasperfx_4448</c>) — coverage needs a host where
    ///         the assembly name and the service name differ, and Fisher cannot build one.
    ///         <see cref="StoreOptions.EventModelName" /> stays the reliable answer, and is what a
    ///         host on an older Wolverine should set.
    ///     </para>
    ///     <para>
    ///         <b>An explicit <see cref="StoreOptions.EventModelName" /> still wins</b>, which is what
    ///         a modular monolith needs when each module's store is genuinely its own bounded context.
    ///         The literal stays as the last resort for a host with no JasperFx options at all — a
    ///         bare <c>ServiceCollection</c>, which is what most of Fisher's own tests build.
    ///     </para>
    ///     <para>
    ///         Resolved here rather than at registration for the reason the whole class exists: the
    ///         container is only complete once the model is being assembled, and
    ///         <c>JasperFxOptions</c> may be registered either side of <c>AddFisher</c>.
    ///     </para>
    /// </remarks>
    private static string ResolveModelName(IDocumentStore store, IServiceProvider services)
    {
        if (store.Options.EventModelName is { } named) return named;

        // GetService, never GetRequiredService: a host with no JasperFx registration is an ordinary
        // shape here rather than a misconfiguration, and the literal is the right answer for it.
        var serviceName = services.GetService<JasperFxOptions>()?.ServiceName;

        // Whitespace is not a usable model name and would reproduce fisher#271 with a blank where the
        // name should be -- the same reason StoreOptions.EventModelName refuses one from its setter.
        return string.IsNullOrWhiteSpace(serviceName)
            ? ProjectionEventModelSource.DefaultModelName
            : serviceName;
    }
}

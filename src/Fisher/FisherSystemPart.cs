using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Descriptors;
using JasperFx.Resources;
using Weasel.Core.CommandLine;

namespace Fisher;

/// <summary>
///     Exposes a Fisher store's database file(s) to JasperFx's resource model, so
///     <c>services.AddResourceSetupOnStartup()</c> and the <c>resources</c> / <c>describe</c> CLI
///     commands see the store with no Fisher-specific call (fisher#172).
/// </summary>
/// <remarks>
///     <para>
///         Mirrors Marten's <c>MartenSystemPart</c> and Polecat's <c>PolecatSystemPart</c>:
///         <see cref="FindResources" /> wraps each tenant database in a Weasel
///         <see cref="DatabaseResource" />, which is what makes "set up the schema" a resource the host
///         already knows how to provision.
///     </para>
///     <para>
///         <b>The store is resolved lazily rather than injected</b>, because the
///         <see cref="IConfigureFisher" /> chain has to have run before the tenancy is meaningful and
///         that only happens on first <see cref="IDocumentStore" /> resolution. Registering the part
///         eagerly would build the store while the container is still being assembled.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "WriteToConsole reflects over the store options for the dev-time 'describe' command only; "
        + "the resource-setup path (FindResources/Setup) is reflection-free. AOT consumers run resource setup, "
        + "not describe.")]
[UnconditionalSuppressMessage("Trimming", "IL2046",
    Justification = "Class-level: override RUC mismatch — the base WriteToConsole does not yet carry "
        + "RequiresUnreferencedCode. Suppressed locally to match Marten's MartenSystemPart.")]
internal class FisherSystemPart : SystemPartBase
{
    private readonly Func<IDocumentStore> _store;

    public static Uri FisherStoreUri { get; } = new("fisher://store");

    protected FisherSystemPart(Func<IDocumentStore> store, string title, Uri subjectUri) : base(title, subjectUri)
    {
        _store = store;
    }

    public FisherSystemPart(Func<IDocumentStore> store) : this(store, "Fisher", FisherStoreUri)
    {
    }

    public override Task WriteToConsole()
    {
        var description = OptionsDescription.For(_store());
        OptionDescriptionWriter.Write(description);
        return Task.CompletedTask;
    }

    /// <remarks>
    ///     A dynamic tenancy is refreshed first, or a store whose tenants come from an
    ///     <see cref="Storage.ITenantSource" /> would report only the tenants something happened to have
    ///     resolved already — which on a freshly started host is none of them, and "no resources" reads
    ///     as a store with nothing to provision rather than as a question that was never asked.
    /// </remarks>
    public override async ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        var tenancy = _store().Tenancy;

        // Read through ITenancy rather than the store's own internal RefreshTenantsAsync, because an
        // ancillary store arrives here as its marker DispatchProxy and is not a DocumentStore.
        if (tenancy is Storage.DynamicTenancy dynamic)
        {
            await dynamic.RefreshAsync().ConfigureAwait(false);
        }

        return tenancy.AllDatabases()
            .Select(x => new DatabaseResource(x, SubjectUri))
            .ToArray();
    }
}

/// <summary>
///     Marker-typed variant of <see cref="FisherSystemPart" /> for an ancillary store registered with
///     <c>AddFisherStore&lt;T&gt;</c>, so each store contributes its own database(s) to the resource
///     model under a subject uri of its own.
/// </summary>
/// <remarks>
///     Distinct subject uris matter more here than on either sibling: two Fisher stores are usually two
///     <em>files</em>, so a shared uri would collapse two genuinely separate databases into one entry.
/// </remarks>
internal sealed class FisherSystemPart<T> : FisherSystemPart where T : class, IDocumentStore
{
    public FisherSystemPart(Func<T> store)
        : base(() => store(), $"Fisher {typeof(T).Name}",
            new Uri("fisher://" + typeof(T).Name.ToLowerInvariant()))
    {
    }
}

using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Projections;

/// <summary>
///     Refuses a store whose events are conjoined but whose aggregate documents are not (fisher#335).
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this prevents is silent in both directions.</b> Under conjoined event tenancy
///         two tenants may use the same stream id, and a single-tenant snapshot document keyed on that id
///         alone holds one row for both — so the second tenant's projection overwrites the first's, with
///         no error on append and none on read, and each tenant then reads a document describing the
///         other's stream. It is the fisher#51 shape reached through a projection, and nothing observes
///         it until two tenants happen to share an id. The shared
///         <c>inline_and_async_snapshots_of_a_shared_stream_id_stay_isolated_per_tenant</c> fact is
///         what found the gap.
///     </para>
///     <para>
///         <b>Refused rather than repaired, which is Marten's answer</b> (marten#5343, "Tenancy storage
///         style mismatch"). Silently marking the document <c>MultiTenanted()</c> would change an
///         existing table's primary key under the application — a table rebuild on live data — to
///         satisfy a configuration the application never wrote down. A refusal naming the type and the
///         two lines that fix it costs one startup.
///     </para>
///     <para>
///         <b>What is exempt, and why.</b> A <c>Live</c> projection writes nothing. A multi-stream
///         projection whose <see cref="TenancyGrouping" /> is not <c>RespectTenant</c> has said in so
///         many words that its documents are not per tenant — Marten's exemption, word for word. A type
///         stored by a registered projection storage provider (an EF Core entity) is not a Fisher
///         document, so there is no mapping to judge; it has to implement <see cref="ITenanted" />
///         instead, which is where the provider writes the tenant (fisher#334).
///     </para>
///     <para>
///         Only the direction that leaks is refused. Marten also refuses conjoined documents under
///         single-tenant events, which is a mismatch but not a cross-tenant read, and refusing it here
///         would break stores for no protection.
///     </para>
/// </remarks>
internal static class ProjectionTenancyGuard
{
    public static void AssertAggregateDocumentsMatchEventTenancy(StoreOptions options)
    {
        if (options.Events.TenancyStyle != TenancyStyle.Conjoined)
        {
            return;
        }

        var mismatches = new List<string>();
        var untenantedEntities = new List<string>();

        foreach (var source in Flatten(options.Projections.All))
        {
            if (source is not IAggregateProjection aggregate || aggregate.Lifecycle == ProjectionLifecycle.Live)
            {
                continue;
            }

            if (source is IHasTenancyGrouping { TenancyGrouping: not TenancyGrouping.RespectTenant })
            {
                continue;
            }

            var documentType = aggregate.AggregateType;

            // A type stored by a projection storage provider (an EF Core entity) has no Fisher mapping
            // to judge. What it needs instead is somewhere to put the tenant: the provider stamps
            // IHasTenantId.TenantId, and an entity without one would fold every tenant's same-id
            // stream into one row — Marten's and Polecat's EF rule (fisher#334).
            if (options.Projections.StorageProviders.HasProviderFor(documentType))
            {
                if (!typeof(IHasTenantId).IsAssignableFrom(documentType))
                {
                    untenantedEntities.Add(documentType.FullName ?? documentType.Name);
                }

                continue;
            }

            if (options.Schema.MappingFor(documentType).TenancyStyle != TenancyStyle.Conjoined)
            {
                mismatches.Add(documentType.FullName ?? documentType.Name);
            }
        }

        if (untenantedEntities.Count > 0)
        {
            throw new InvalidOperationException(
                "Tenancy storage style mismatch: this store's events are Conjoined, but the projected "
                + $"entity type(s) {string.Join(", ", untenantedEntities.Distinct())} are stored by a "
                + "projection storage provider (EF Core) and do not implement "
                + "JasperFx.MultiTenancy.ITenanted, so there is nowhere to write the tenant. Implement "
                + "ITenanted, and key the entity on (TenantId, Id) if two tenants may share a stream id.");
        }

        if (mismatches.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Tenancy storage style mismatch: this store's events are Conjoined, but the aggregate "
            + $"document type(s) {string.Join(", ", mismatches.Distinct())} are single-tenant. Two tenants "
            + "may use the same stream id, and a single-tenant document keyed on that id would hold one "
            + "row for both — each tenant's projection overwriting the other's, silently. Mark the "
            + "document conjoined with Schema.For<T>().MultiTenanted() (or [MultiTenanted]), or every "
            + "document with Policies.AllDocumentsAreMultiTenanted(). A multi-stream projection that "
            + "deliberately groups across tenants can say so with TenancyGrouping.AcrossTenants.");
    }

    private static IEnumerable<IProjectionSource<IDocumentSession, IQuerySession>> Flatten(
        IEnumerable<IProjectionSource<IDocumentSession, IQuerySession>> sources)
    {
        foreach (var source in sources)
        {
            if (source is JasperFx.Events.Projections.Composite.CompositeProjection<IDocumentSession, IQuerySession> composite)
            {
                foreach (var member in Flatten(composite.AllProjections()))
                {
                    yield return member;
                }

                continue;
            }

            yield return source;
        }
    }
}

/// <summary>
///     Reads <c>TenancyGrouping</c> off a multi-stream projection without knowing its type arguments;
///     satisfied by the property Fisher's <see cref="MultiStreamProjection{TDoc,TId}" /> inherits.
/// </summary>
internal interface IHasTenancyGrouping
{
    TenancyGrouping TenancyGrouping { get; }
}

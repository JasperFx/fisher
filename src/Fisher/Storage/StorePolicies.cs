using JasperFx;
using JasperFx.MultiTenancy;

namespace Fisher.Storage;

/// <summary>
///     Configuration applied to every document mapping as it is created, so a store-wide decision does
///     not have to be repeated per type (fisher#39).
/// </summary>
/// <remarks>
///     <para>
///         <b>Policies are the weakest of the four configuration layers</b>, and deliberately so. In
///         order: a policy, then the JasperFx metadata interfaces, then the
///         <see cref="Fisher.Attributes" /> schema attributes, then
///         <c>Schema.For&lt;T&gt;()</c> — each overriding the one before. A policy was written without
///         knowing about the type it is being applied to; the DSL names it.
///     </para>
///     <para>
///         A mapping is created lazily on first use, so a policy added <em>after</em> a type has been
///         mapped does not reach it. Register policies where the rest of the store is configured, which
///         is where every other decision that reshapes a table already has to go.
///     </para>
///     <para>
///         <b>Polecat's partitioning policies are deliberately absent and always will be.</b>
///         <c>AllDocumentsAreMultiTenantedWithPartitioning()</c> and its relatives have no SQLite
///         equivalent: there are no partition functions, no partition schemes and no per-partition
///         storage. The nearest thing is separate tables behind a <c>UNION ALL</c> view, which carries
///         none of the operational properties — partition switching, aged-partition drop — that make
///         the feature worth having. Not a gap; not on any roadmap.
///     </para>
/// </remarks>
public class StorePolicies
{
    private readonly List<Action<DocumentMapping>> _policies = [];

    /// <summary>
    ///     Apply an arbitrary policy to every document mapping.
    /// </summary>
    public StorePolicies ForAllDocuments(Action<DocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _policies.Add(configure);
        return this;
    }

    /// <summary>
    ///     Apply configuration to one document type when its mapping is created.
    /// </summary>
    /// <remarks>
    ///     Different from <c>Schema.For&lt;T&gt;()</c> in exactly one way, and it is the way that
    ///     matters: this does not create the mapping. A type that is never stored is never mapped and
    ///     never gets a table, where <c>Schema.For&lt;T&gt;()</c> registers it there and then. Use this
    ///     to say "if you store one of these, store it like so".
    /// </remarks>
    public StorePolicies ForDocument<T>(Action<DocumentMapping> configure) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);

        return ForAllDocuments(mapping =>
        {
            if (mapping.DocumentType == typeof(T))
            {
                configure(mapping);
            }
        });
    }

    /// <summary>
    ///     Make every document type soft-deleted: <c>Delete</c> flags the row rather than removing it.
    /// </summary>
    public StorePolicies AllDocumentsSoftDeleted()
        => ForAllDocuments(mapping => mapping.DeleteStyle = DeleteStyle.SoftDelete);

    /// <summary>
    ///     Give every document table a <c>tenant_id</c> column and make the primary key the tenant/id
    ///     pair.
    /// </summary>
    /// <remarks>
    ///     A schema decision, so it has to be set before the tables are created — the same rule
    ///     <c>MultiTenanted()</c> follows per type, and the same rule the event store's
    ///     <c>TenancyStyle</c> follows.
    /// </remarks>
    public StorePolicies AllDocumentsAreMultiTenanted()
        => ForAllDocuments(mapping => mapping.TenancyStyle = TenancyStyle.Conjoined);

    /// <summary>
    ///     Guard every document type's writes with an optimistic concurrency check.
    /// </summary>
    public StorePolicies AllDocumentsUseOptimisticConcurrency()
        => ForAllDocuments(mapping => mapping.UseOptimisticConcurrency = true);

    internal void Apply(DocumentMapping mapping)
    {
        for (var i = 0; i < _policies.Count; i++)
        {
            _policies[i](mapping);
        }
    }
}

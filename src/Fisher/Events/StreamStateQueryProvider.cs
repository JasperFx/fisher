using System.Data.Common;
using Fisher.Events.Internal;
using Fisher.Internal;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Events;

/// <summary>
///     The <see cref="IQueryable{T}" /> of <see cref="StreamState" /> behind
///     <see cref="IReadOnlyEventStore.QueryStreamStates" /> (jasperfx#740) — <c>fi_streams</c> as a
///     composable query surface, executed through the shared
///     <see cref="IDocumentQueryExecutor" /> hook the document read tier already dispatches on.
/// </summary>
/// <remarks>
///     <para>
///         A dedicated provider rather than a detour through <see cref="FisherQueryProvider" />,
///         because the document provider's whole pipeline starts from a
///         <c>DocumentMapping</c>/<c>json_extract</c> world and <see cref="StreamState" /> must never
///         become a document (asking the schema for its mapping would register it and give it a
///         table). What IS shared is everything worth sharing: the
///         <see cref="WhereClauseParser" /> over a <see cref="StreamStateMemberFactory" /> — the same
///         split <c>AssignTagWhere</c> uses for <c>fi_events</c> — the
///         <see cref="Statement" /> renderer, and the <see cref="FisherStreamsRowReader" /> that every
///         other <c>fi_streams</c> read materializes through.
///     </para>
///     <para>
///         Session lifetime follows <see cref="FisherReadOnlyEventStore" />'s rule: the queryable is
///         handed out unexecuted, so each terminator opens a session of its own and disposes it before
///         the result is returned — a captured session would pin a pooled connection for as long as
///         the caller keeps the queryable, which for a polling monitoring tool is forever. That rule,
///         the operator set below and the execution all live on <see cref="EventTableQueryProvider{TElement}" />,
///         shared with <see cref="EventDataQueryProvider{T}" /> (jasperfx#1073); this class supplies only
///         what is particular to <c>fi_streams</c>.
///     </para>
///     <para>
///         The supported operator set is the jasperfx#740 contract, not all of LINQ:
///         <c>Where</c> over every <see cref="StreamState" /> member,
///         <c>OrderBy</c>/<c>OrderByDescending</c>/<c>ThenBy</c>/<c>ThenByDescending</c>,
///         <c>Skip</c>/<c>Take</c>, and the four shared async terminators. Anything else is refused
///         naming the operator — the same never-silently-match-all rule the member factory applies one
///         level down.
///     </para>
/// </remarks>
internal sealed class StreamStateQueryProvider : EventTableQueryProvider<StreamState>
{
    /// <param name="store">The store whose streams table is being queried.</param>
    /// <param name="tenantId">
    ///     Explicit tenant scope, already validated by
    ///     <see cref="FisherReadOnlyEventStore.QueryStreamStates" /> to be null on a store without
    ///     a tenant dimension.
    /// </param>
    /// <param name="allTenants">
    ///     <see cref="JasperFx.Events.IEventStore.QueryStreamStatesForAllTenants" /> (jasperfx#1079): no tenant
    ///     predicate at all. Only ever true on a store with one database — the caller refuses the rest.
    /// </param>
    internal StreamStateQueryProvider(DocumentStore store, string? tenantId, bool allTenants = false)
        : base(store, tenantId)
    {
        _allTenants = allTenants;

        // jasperfx#1079: which tenant a row belongs to. Under conjoined tenancy that is the row's own
        // tenant_id. Otherwise every row in the file is one tenant's, and the column cannot say which: it
        // holds the default for every row, the file being the tenant under database-per-tenant. So the
        // tenant is the one the read was opened for, spelled as the tenancy spells it (fisher#393).
        _fileTenant = Graph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
                      || store.Tenancy.Cardinality == JasperFx.Descriptors.DatabaseCardinality.Single
            ? null
            : store.Tenancy.TenantIdFor(tenantId ?? JasperFx.StorageConstants.DefaultTenantId);
    }

    private readonly bool _allTenants;

    /// <summary>
    ///     The tenant every row of the read belongs to when the <c>tenant_id</c> column cannot say —
    ///     database-per-tenant without conjoined events — or null when the column is the answer.
    /// </summary>
    private readonly string? _fileTenant;

    protected override string Origin => "QueryStreamStates()";

    protected override string QueryKind => "stream-state query";

    protected override IMemberResolver CreateMemberResolver() => new StreamStateMemberFactory(Graph, _fileTenant);

    protected override Statement CreateStatement() => new()
    {
        FromTable = Graph.StreamsTableName,
        SelectColumns = FisherStreamsRowReader.SelectColumns
    };

    protected override StreamState Read(DbDataReader reader, FisherSession session)
    {
        var state = FisherStreamsRowReader.Read(reader, Graph, Graph.StreamIdentity == StreamIdentity.AsGuid);
        state.TenantId = _fileTenant ?? FisherStreamsRowReader.ReadTenantId(reader);
        return state;
    }

    /// <summary>
    ///     Under conjoined tenancy the queryable is always tenant-scoped: to the explicit tenant when
    ///     <c>QueryStreamStates(tenantId)</c> named one, and to the default session scope otherwise —
    ///     the same rule <see cref="EventOperations.QueryEventsAsync(EventQuery, CancellationToken)" />
    ///     applies to <see cref="EventQuery.TenantId" />. On a single-tenancy store there is no
    ///     tenant dimension worth filtering on, and a non-null tenant was already refused upstream.
    /// </summary>
    /// <remarks>
    ///     <b>A predicate is only half of it, and the other half is which database the session opens</b>
    ///     (jasperfx#885). Under conjoined tenancy there is one file and this filter is the whole scope;
    ///     under database-per-tenant there is no predicate to add and the <em>file</em> is the scope, so
    ///     the base class opens its session for the tenant rather than for the default one. Getting only
    ///     one of the two right is silent in both directions: a predicate against the wrong file matches
    ///     nothing, and the right file read with no predicate reads every tenant in it.
    /// </remarks>
    protected override void ApplyScope(Statement statement)
    {
        if (_allTenants)
        {
            // jasperfx#1079: every tenant's streams, so no predicate — which is the whole of the
            // difference from the default-tenant read OpenReadOnlyEventStore().QueryStreamStates() is.
            return;
        }

        if (Graph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            statement.Wheres.Add(new ComparisonFilter("tenant_id", "=",
                TenantId ?? JasperFx.StorageConstants.DefaultTenantId));
        }
    }
}

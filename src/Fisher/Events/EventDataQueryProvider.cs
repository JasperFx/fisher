using System.Data.Common;
using Fisher.Internal;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Events;

/// <summary>
///     The <see cref="IQueryable{T}" /> of event bodies behind
///     <see cref="IReadOnlyEventStore.QueryEventData{T}" /> (jasperfx#1073) — every <c>fi_events</c> row
///     stored as exactly <typeparamref name="T" />, across every stream, executed through the shared
///     <see cref="IDocumentQueryExecutor" /> hook.
/// </summary>
/// <remarks>
///     <para>
///         <b>No new SQL machinery, twice over.</b> The predicate half is the one
///         <see cref="EventOperations.QueryEventDataAsync{T}" /> (fisher#41) already runs: an event body is
///         a JSON document in a TEXT column called <c>data</c>, so the mapping-less
///         <see cref="MemberFactory(StoreOptions)" /> resolves its members to <c>json_extract</c> locators
///         verbatim, with the serializer's naming policy and enum storage, and with no
///         <c>DocumentMapping</c> (asking for one would register the event type as a document and give it
///         a table). The queryable half — operators, terminators, session lifetime — is
///         <see cref="EventTableQueryProvider{TElement}" />, shared with
///         <see cref="StreamStateQueryProvider" />.
///     </para>
///     <para>
///         <b>"Exactly <typeparamref name="T" />" is the <c>type</c> column</b>, the event-type alias, as
///         <c>QueryEventDataAsync</c> filters: short and stable where <c>dotnet_type</c> is
///         assembly-qualified and brittle across a rename. Another event type with the same shape has
///         another alias, so it is never returned — the suite's <c>BargeUnloaded</c> decoy.
///     </para>
///     <para>
///         Archived events are included: the contract is every event stored as <typeparamref name="T" />,
///         and archiving moves a stream out of the live reads, not out of the store — the same answer
///         <c>QueryEventDataAsync</c> gives. With no <c>OrderBy</c> the order is <c>seq_id</c>, which is
///         also the final tiebreak under one, so paging over a member with repeated values is stable.
///     </para>
///     <para>
///         <b>This is a scan.</b> There is no index over <c>fi_events.data</c>, which is honest for a
///         diagnostic console's query; the <c>type</c> term is the narrowing.
///     </para>
/// </remarks>
internal sealed class EventDataQueryProvider<T> : EventTableQueryProvider<T> where T : class
{
    private readonly string _eventTypeName;

    /// <param name="store">The store whose events table is being queried.</param>
    /// <param name="tenantId">
    ///     Explicit tenant scope, already validated by <see cref="FisherReadOnlyEventStore.QueryEventData{T}" />
    ///     to be null on a store without a tenant dimension.
    /// </param>
    internal EventDataQueryProvider(DocumentStore store, string? tenantId) : base(store, tenantId)
    {
        // A [BinaryEvent] keeps its body in data_binary, where json_extract cannot reach: refused when
        // the queryable is asked for rather than answering every predicate with no rows.
        Graph.AssertBodyIsQueryable(typeof(T), "QueryEventData<T>()");

        _eventTypeName = Graph.EventMappingFor(typeof(T)).EventTypeName;
    }

    protected override string Origin => $"QueryEventData<{typeof(T).Name}>()";

    protected override string QueryKind => "event-data query";

    protected override IMemberResolver CreateMemberResolver() => new MemberFactory(Store.Options);

    protected override Statement CreateStatement() => new()
    {
        FromTable = Graph.EventsTableName,
        SelectColumns = "data"
    };

    protected override T? Read(DbDataReader reader, FisherSession session)
        => reader.IsDBNull(0) ? null : session.FisherSerializer.FromJson<T>(reader.GetString(0));

    /// <summary>
    ///     The event type, and under conjoined tenancy the tenant — the explicit one when
    ///     <c>QueryEventData&lt;T&gt;(tenantId)</c> named it, the default otherwise, exactly as
    ///     <see cref="StreamStateQueryProvider" /> scopes <c>fi_streams</c>. Under database-per-tenant the
    ///     base class's session is the scope.
    /// </summary>
    protected override void ApplyScope(Statement statement)
    {
        statement.Wheres.Add(new ComparisonFilter("type", "=", _eventTypeName));

        if (Graph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            statement.Wheres.Add(new ComparisonFilter("tenant_id", "=",
                TenantId ?? JasperFx.StorageConstants.DefaultTenantId));
        }
    }

    protected override void ApplyFinalOrdering(Statement statement) => statement.OrderBys.Add(("seq_id", false));
}

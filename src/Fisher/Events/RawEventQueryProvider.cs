using System.Data.Common;
using Fisher.Events.Internal;
using Fisher.Internal;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Events;

/// <summary>
///     The <see cref="IQueryable{T}" /> of <see cref="IEvent" /> behind
///     <see cref="IReadOnlyEventStore.QueryRawEvents" /> (jasperfx#1094) — every <c>fi_events</c> row as
///     its envelope, executed through the shared <see cref="IDocumentQueryExecutor" /> hook.
/// </summary>
/// <remarks>
///     <para>
///         The predicate half is <see cref="EventOperations.QueryEventsAsync(System.Linq.Expressions.Expression{System.Func{IEvent, bool}}, CancellationToken)" />'s:
///         the <see cref="EventMemberFactory" /> resolves <see cref="IEvent" /> members to columns, and
///         anything it cannot resolve throws <see cref="BadLinqExpressionException" /> naming the member,
///         never matching every event. The body is <c>object</c> here and is not reachable; a predicate on
///         one event type's members stays on <c>QueryEventData&lt;T&gt;</c>.
///     </para>
///     <para>
///         Rows hydrate through <see cref="FisherEventsRowReader.ReadEventAcrossStreams" />, so an
///         unresolvable <c>dotnet_type</c> is skipped as the stream reads skip it. With no <c>OrderBy</c>
///         the order is <c>seq_id</c>, which is also the final tiebreak.
///     </para>
/// </remarks>
internal sealed class RawEventQueryProvider : EventTableQueryProvider<IEvent>
{
    internal RawEventQueryProvider(DocumentStore store, string? tenantId) : base(store, tenantId)
    {
    }

    protected override string Origin => "QueryRawEvents()";

    protected override string QueryKind => "raw event query";

    protected override string ElementName => nameof(IEvent);

    protected override IMemberResolver CreateMemberResolver() => new EventMemberFactory(Graph);

    protected override Statement CreateStatement() => new()
    {
        FromTable = Graph.EventsTableName,
        SelectColumns = FisherEventsRowReader.ComposeSelectColumns(Graph.EventOptions)
    };

    protected override IEvent? Read(DbDataReader reader, FisherSession session)
        => throw new NotSupportedException("Raw events hydrate asynchronously.");

    protected override async ValueTask<IEvent?> ReadAsync(DbDataReader reader, FisherSession session,
        CancellationToken token)
    {
        var ctx = new EventHydrationContext(Graph, session.FisherSerializer, string.Empty, TenantId);
        var slots = MetadataSlots.For(Graph.EventOptions);

        return await FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots,
            Graph.StreamIdentity == StreamIdentity.AsGuid, token).ConfigureAwait(false);
    }

    protected override void ApplyScope(Statement statement)
    {
        if (Graph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            statement.Wheres.Add(new ComparisonFilter("tenant_id", "=",
                TenantId ?? JasperFx.StorageConstants.DefaultTenantId));
        }
    }

    protected override void ApplyFinalOrdering(Statement statement) => statement.OrderBys.Add(("seq_id", false));
}

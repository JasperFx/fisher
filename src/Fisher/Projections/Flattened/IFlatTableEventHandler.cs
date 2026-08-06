using Fisher.Events;
using JasperFx.Events;

namespace Fisher.Projections.Flattened;

/// <summary>
///     What a flat-table projection does with one event type: build the statement once, then turn each
///     matching event into an operation.
/// </summary>
/// <remarks>
///     Compilation is deferred to the first apply because the statement depends on the store's stream
///     identity — a table with no explicit primary key source is keyed on the stream, and whether that
///     is <c>StreamId</c> or <c>StreamKey</c> is not known when the projection's constructor runs.
/// </remarks>
internal interface IFlatTableEventHandler
{
    void Compile(EventGraph events);

    FlatTableSqlOperation CreateOperation(IEvent e);
}

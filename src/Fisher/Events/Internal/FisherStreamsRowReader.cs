using System.Data.Common;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher.Events.Internal;

/// <summary>
///     The canonical SELECT projection and row reader for <c>fi_streams</c>. Same locked-column-order
///     discipline as <see cref="FisherEventsRowReader" />.
/// </summary>
internal static class FisherStreamsRowReader
{
    internal const string SelectColumns = "id, type, version, created, timestamp, tenant_id, is_archived";

    /// <summary>
    ///     Read the current row into a <see cref="StreamState" />.
    /// </summary>
    /// <remarks>
    ///     The aggregate type is resolved leniently through
    ///     <see cref="EventGraph.TryResolveAggregateType" />: a stream written by a deployment that
    ///     knew a type this one does not must still be able to report its version and timestamps.
    /// </remarks>
    internal static StreamState Read(DbDataReader reader, EventGraph graph, bool isGuidIdentity)
    {
        var state = new StreamState();

        if (isGuidIdentity)
        {
            state.Id = Guid.Parse(reader.GetString(0));
        }
        else
        {
            state.Key = reader.GetString(0);
        }

        state.AggregateType = reader.IsDBNull(1) ? null : graph.TryResolveAggregateType(reader.GetString(1));
        state.Version = reader.GetInt64(2);
        state.Created = SqliteTimestamp.FromDatabaseValue(reader.GetString(3));
        state.LastTimestamp = SqliteTimestamp.FromDatabaseValue(reader.GetString(4));
        state.IsArchived = reader.GetInt64(6) != 0;

        return state;
    }
}

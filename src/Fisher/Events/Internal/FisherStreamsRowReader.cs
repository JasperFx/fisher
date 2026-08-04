using System.Data.Common;
using Fisher.Storage;
using JasperFx.Descriptors;
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

    /// <summary>
    ///     Read the current row into the event store explorer's <see cref="StreamSummary" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The id is read as raw text rather than parsed and re-rendered. Under Guid identity
    ///         <c>fi_streams.id</c> already holds the lowercase canonical form — see
    ///         <c>SqliteGuidIdentification</c> — which is exactly what <see cref="Guid.ToString()" />
    ///         produces, so the round trip through <see cref="Guid" /> would be a no-op that could only
    ///         introduce a casing discrepancy.
    ///     </para>
    ///     <para>
    ///         <c>StreamType</c> is the stored alias, not a resolved <see cref="Type" />: the explorer is
    ///         a diagnostic view of what is on disk, and a stream whose aggregate type this deployment
    ///         does not know still has a name worth showing.
    ///     </para>
    /// </remarks>
    internal static StreamSummary ReadStreamSummary(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            SqliteTimestamp.FromDatabaseValue(reader.GetString(3)),
            SqliteTimestamp.FromDatabaseValue(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5));

    /// <summary>
    ///     Read the current row into the event store explorer's <see cref="StreamMetadata" />.
    /// </summary>
    /// <remarks>
    ///     Snapshot columns are <see langword="null" /> because Fisher has no stream compaction, and
    ///     <c>Tags</c> is an <em>empty dictionary rather than null</em> because DCB tags do not exist
    ///     yet. The record declares <c>Tags</c> non-nullable, so "no tags" is the empty dictionary;
    ///     returning null there is what polecat#412 was, and
    ///     <c>stream_metadata_for_a_known_stream</c> asserts against it directly.
    /// </remarks>
    internal static StreamMetadata ReadStreamMetadata(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            SqliteTimestamp.FromDatabaseValue(reader.GetString(3)),
            SqliteTimestamp.FromDatabaseValue(reader.GetString(4)),
            LastSnapshotAt: null,
            LastSnapshotVersion: null,
            reader.GetInt64(6) != 0,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            Tags: new Dictionary<string, string>());
}

using System.Text;
using Fisher.Events.Internal;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Events;

/// <summary>
///     The read half of the event store surface on a session.
/// </summary>
public partial class EventOperations
{
    private bool IsGuidIdentity => Graph.StreamIdentity == StreamIdentity.AsGuid;

    private bool IsConjoined => Graph.TenancyStyle == TenancyStyle.Conjoined;

    /// <summary>
    ///     Every event in a stream, in version order.
    /// </summary>
    /// <param name="streamId">The stream to read.</param>
    /// <param name="version">When greater than 0, read only up to and including this version.</param>
    /// <param name="timestamp">When supplied, read only events at or before this time.</param>
    /// <param name="fromVersion">When greater than 0, read only from this version onward.</param>
    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
    {
        AssertGuidIdentity();
        return FetchStreamAsync((object)streamId, version, timestamp, fromVersion, token);
    }

    /// <inheritdoc cref="FetchStreamAsync(Guid,long,System.Nullable{DateTimeOffset},long,CancellationToken)" />
    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
    {
        AssertStringIdentity();
        return FetchStreamAsync((object)streamKey, version, timestamp, fromVersion, token);
    }

    private async Task<IReadOnlyList<IEvent>> FetchStreamAsync(object streamId, long version,
        DateTimeOffset? timestamp, long fromVersion, CancellationToken token)
    {
        var options = _session.Options.Events;
        var sql = new StringBuilder("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(options))
            .Append(" from ")
            .Append(Graph.EventsTableName)
            .Append(" where stream_id = @stream_id");

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
        }

        if (version > 0)
        {
            sql.Append(" and version <= @version");
        }

        if (fromVersion > 0)
        {
            sql.Append(" and version >= @from_version");
        }

        if (timestamp.HasValue)
        {
            // Comparing ISO-8601 UTC text lexicographically is the same ordering as comparing the
            // instants, which is the whole reason SqliteTimestamp fixes that representation.
            sql.Append(" and timestamp <= @timestamp");
        }

        sql.Append(" order by version");

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        BindStreamId(command, streamId);

        if (IsConjoined)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", TenantId)
            {
                SqliteType = SqliteType.Text
            });
        }

        if (version > 0)
        {
            command.Parameters.Add(new SqliteParameter("version", version) { SqliteType = SqliteType.Integer });
        }

        if (fromVersion > 0)
        {
            command.Parameters.Add(new SqliteParameter("from_version", fromVersion)
            {
                SqliteType = SqliteType.Integer
            });
        }

        if (timestamp.HasValue)
        {
            command.Parameters.Add(
                new SqliteParameter("timestamp", SqliteTimestamp.ToDatabaseValue(timestamp.Value))
                {
                    SqliteType = SqliteType.Text
                });
        }

        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, streamId, TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;
        var results = new List<IEvent>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var @event = isGuid
                ? await FisherEventsRowReader.ReadEventAsGuid(reader, ctx, slots, token).ConfigureAwait(false)
                : await FisherEventsRowReader.ReadEventAsString(reader, ctx, slots, token).ConfigureAwait(false);

            // Null means dotnet_type named a type this process cannot resolve. Skipping rather than
            // throwing matches Marten and Polecat: a deployment that has not been told about an
            // event type must still be able to read the rest of the stream.
            if (@event is not null)
            {
                results.Add(@event);
            }
        }

        return results;
    }

    /// <summary>
    ///     The current version, timestamps, and aggregate type of a stream, or null when it does not
    ///     exist.
    /// </summary>
    public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken token = default)
    {
        AssertGuidIdentity();
        return FetchStreamStateAsync((object)streamId, token);
    }

    /// <inheritdoc cref="FetchStreamStateAsync(Guid,CancellationToken)" />
    public Task<StreamState?> FetchStreamStateAsync(string streamKey, CancellationToken token = default)
    {
        AssertStringIdentity();
        return FetchStreamStateAsync((object)streamKey, token);
    }

    private async Task<StreamState?> FetchStreamStateAsync(object streamId, CancellationToken token)
    {
        var sql = new StringBuilder("select ")
            .Append(FisherStreamsRowReader.SelectColumns)
            .Append(" from ")
            .Append(Graph.StreamsTableName)
            .Append(" where id = @stream_id");

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
        }

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;

        BindStreamId(command, streamId);

        if (IsConjoined)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", TenantId)
            {
                SqliteType = SqliteType.Text
            });
        }

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? FisherStreamsRowReader.Read(reader, Graph, IsGuidIdentity)
            : null;
    }

    /// <summary>
    ///     A single event by its id, or null when no such event exists.
    /// </summary>
    public async Task<IEvent?> LoadAsync(Guid id, CancellationToken token = default)
    {
        var options = _session.Options.Events;

        var connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select {FisherEventsRowReader.ComposeSelectColumns(options)} from {Graph.EventsTableName} where id = @id";
        command.CommandTimeout = _session.Options.CommandTimeout;
        command.Parameters.Add(new SqliteParameter("id", id.ToString()) { SqliteType = SqliteType.Text });

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            return null;
        }

        // Unlike FetchStream the stream identity is not known in advance here, so it comes off the
        // row itself.
        var rawStreamId = reader.GetString(2);
        var isGuid = IsGuidIdentity;
        object streamId = isGuid ? Guid.Parse(rawStreamId) : rawStreamId;

        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, streamId, TenantId);
        var slots = MetadataSlots.For(options);

        return isGuid
            ? await FisherEventsRowReader.ReadEventAsGuid(reader, ctx, slots, token).ConfigureAwait(false)
            : await FisherEventsRowReader.ReadEventAsString(reader, ctx, slots, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     A single event by id, typed to its payload. Null when the event does not exist or carries a
    ///     different payload type.
    /// </summary>
    public async Task<IEvent<T>?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class
        => await LoadAsync(id, token).ConfigureAwait(false) as IEvent<T>;

    private void BindStreamId(SqliteCommand command, object streamId)
        => command.Parameters.Add(
            new SqliteParameter("stream_id", SqliteStorageDialect<Guid>.ToDatabaseValue(streamId))
            {
                SqliteType = SqliteType.Text
            });
}

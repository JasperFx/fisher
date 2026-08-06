using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Fisher.Events.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;

namespace Fisher;

/// <summary>
///     Fisher's <see cref="IEventStore" /> implementation — the store-agnostic surface that monitoring
///     and tooling code (CritterWatch, the event store explorer) reads a Critter Stack event store
///     through.
/// </summary>
/// <remarks>
///     <para>
///         Implemented explicitly, as Polecat does, so none of it lands on <see cref="DocumentStore" />'s
///         own public API. Application code uses sessions; this surface exists for tools.
///     </para>
///     <para>
///         Most of <see cref="IEventStore" /> is default-implemented by the interface itself and left
///         alone here. What Fisher overrides is the pair of explorer reads it can answer from
///         <c>fi_streams</c> — <see cref="IEventStore.GetRecentStreamsAsync(int, CancellationToken)" />
///         and <see cref="IEventStore.GetStreamMetadataAsync(string, CancellationToken)" /> — plus the
///         required members. The rest throw, naming the milestone they wait on, exactly as
///         <c>EventOperations.Unsupported.cs</c> does: a tool that reaches for a capability Fisher does
///         not have should be told so rather than handed an empty result that reads as "no data".
///     </para>
/// </remarks>
public partial class DocumentStore : IEventStore
{
    private static readonly Meter _meter = new("Fisher", typeof(DocumentStore).Assembly.GetName().Version?.ToString());

    private static readonly ActivitySource _activitySource =
        new("Fisher", typeof(DocumentStore).Assembly.GetName().Version?.ToString());

    Meter IEventStore.Meter => _meter;

    ActivitySource IEventStore.ActivitySource => _activitySource;

    string IEventStore.MetricsPrefix => "fisher";

    Uri IEventStore.Subject => Database.Describe().DatabaseUri();

    /// <summary>
    ///     One SQLite file is one database, and Fisher has no database-per-tenant tenancy, so the
    ///     cardinality is always <see cref="DatabaseCardinality.Single" />.
    /// </summary>
    DatabaseCardinality IEventStore.DatabaseCardinality => DatabaseCardinality.Single;

    bool IEventStore.HasMultipleTenants => false;

    EventStoreIdentity IEventStore.Identity => new(Options.DatabaseSchemaName, "fisher");

    /// <summary>
    ///     jasperfx#420 — how many projection rebuild cells may run concurrently against this database.
    /// </summary>
    /// <remarks>
    ///     An explicit <see cref="DaemonSettings.MaxConcurrentRebuildsPerDatabase" /> wins; a
    ///     non-positive value disables the cap entirely (<see langword="null" /> means "unbounded" to
    ///     JasperFx). Otherwise it derives from <see cref="StoreOptions.MaxPoolSize" /> as
    ///     <c>max(1, poolSize / 8)</c>, the same formula Marten and Polecat use — see
    ///     <see cref="StoreOptions.MaxPoolSize" /> for why Fisher's ceiling is a store option rather
    ///     than a connection-string keyword.
    /// </remarks>
    int? IEventStore.MaxConcurrentRebuildsPerDatabase => ResolveMaxConcurrentRebuilds();

    private int? ResolveMaxConcurrentRebuilds()
    {
        var configured = Options.DaemonSettings.MaxConcurrentRebuildsPerDatabase;
        if (configured.HasValue)
        {
            return configured.Value > 0 ? configured.Value : null;
        }

        return Math.Max(1, Options.MaxPoolSize / 8);
    }

    // ---- explorer reads ----

    Task<IReadOnlyList<StreamSummary>> IEventStore.GetRecentStreamsAsync(int count, CancellationToken ct)
        => GetRecentStreamsAsync(count, ct);

    private async Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(int count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        // Ordering by the ISO-8601 TEXT timestamp is a string sort, and correct only because
        // SqliteTimestamp.Format is fixed-width, UTC-normalised and millisecond-precision. A format
        // with a variable-width offset or no sub-second component would silently mis-order streams
        // written within the same second.
        var sql = $"""
                   select {FisherStreamsRowReader.SelectColumns}
                   from {EventGraph.StreamsTableName}
                   order by timestamp desc
                   limit @count;
                   """;

        return await ReadStreamsAsync(sql,
            command => command.Parameters.AddWithValue("@count", count),
            FisherStreamsRowReader.ReadStreamSummary,
            ct).ConfigureAwait(false);
    }

    Task<StreamMetadata?> IEventStore.GetStreamMetadataAsync(string streamId, CancellationToken ct)
        => GetStreamMetadataAsync(streamId, ct);

    private async Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        // Under Guid identity the parse both validates the input and normalises its casing to the
        // lowercase canonical form fi_streams holds — SQLite's default collation is case-sensitive, so
        // an uppercase Guid string would match nothing. See SqliteGuidIdentification.
        var id = EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.Parse(streamId).ToString()
            : streamId;

        var sql = $"""
                   select {FisherStreamsRowReader.SelectColumns}
                   from {EventGraph.StreamsTableName}
                   where id = @id;
                   """;

        var rows = await ReadStreamsAsync(sql,
            command => command.Parameters.AddWithValue("@id", id),
            FisherStreamsRowReader.ReadStreamMetadata,
            ct).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    ///     Run a <c>fi_streams</c> read through the store's resilience pipeline, hydrating each row with
    ///     <paramref name="read" />.
    /// </summary>
    /// <remarks>
    ///     Rows are materialised inside the pipeline rather than streamed out of it. A retried
    ///     <c>SQLITE_BUSY</c> re-executes the whole delegate, so handing a live reader back to the caller
    ///     would let a retry resume against a connection the previous attempt had already disposed.
    /// </remarks>
    private async Task<IReadOnlyList<T>> ReadStreamsAsync<T>(
        string sql,
        Action<Microsoft.Data.Sqlite.SqliteCommand> configure,
        Func<System.Data.Common.DbDataReader, T> read,
        CancellationToken ct)
    {
        return await Options.ResiliencePipeline.ExecuteAsync(async token =>
        {
            await using var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            configure(command);

            var results = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(read(reader));
            }

            return (IReadOnlyList<T>)results;
        }, ct).ConfigureAwait(false);
    }

    // ---- diagnostics ----

    /// <summary>
    ///     Describe this store's configuration for monitoring tools.
    /// </summary>
    /// <remarks>
    ///     Built by hand rather than through <see cref="EventStoreUsage" />'s reflective constructor,
    ///     which walks the subject's properties and would dump the store's runtime handles into the
    ///     descriptor as if they were configuration.
    /// </remarks>
    Task<EventStoreUsage?> IEventStore.TryCreateUsage(CancellationToken token)
    {
        var usage = new EventStoreUsage
        {
            Subject = "Fisher.DocumentStore",
            SubjectUri = Database.Describe().DatabaseUri(),
            Version = GetType().Assembly.GetName().Version?.ToString()!,
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = Database.Describe()
            }
        };

        usage.AddValue(nameof(EventGraph.StreamIdentity), EventGraph.StreamIdentity);
        usage.AddValue(nameof(EventGraph.AppendMode), EventGraph.AppendMode);
        usage.AddValue(nameof(Options.DatabaseSchemaName), Options.DatabaseSchemaName);
        usage.AddValue(nameof(Options.AutoCreateSchemaObjects), Options.AutoCreateSchemaObjects);

        // jasperfx#434 — surface the effective cap so a rebuild orchestrator can size itself off the
        // wire rather than guessing.
        usage.MaxConcurrentRebuildsPerDatabase = ResolveMaxConcurrentRebuilds();

        foreach (var eventType in EventGraph.AllKnownEventTypes())
        {
            usage.Events.Add(new EventDescriptor(eventType.EventTypeName, TypeDescriptor.For(eventType.EventType)));
        }

        return Task.FromResult<EventStoreUsage?>(usage);
    }

    // ---- not supported yet ----
    //
    // Each names the milestone it waits on. Same discipline as EventOperations.Unsupported.cs: a
    // monitoring tool that reaches for one of these gets told Fisher cannot do it, rather than an
    // empty result it would render as "nothing here".

    /// <summary>
    ///     Fisher answers four of <see cref="IReadOnlyEventStore" />'s five members through a session
    ///     already; the gap is <c>QueryEventsAsync</c>, which needs the paged event query Fisher has no
    ///     querying layer for.
    /// </summary>
    IReadOnlyEventStore IEventStore.OpenReadOnlyEventStore()
        => throw new NotSupportedException(
            "Fisher has no read-only event store session yet — it needs the paged event query surface.");

    /// <summary>
    ///     The tooling-facing compaction entry point, which has no aggregate type parameter and so has
    ///     to resolve one from <c>fi_streams</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Polecat throws here even though it implements the generic overload.</b> Fisher does not,
    ///     because the type it needs is already on the row: a stream started with an aggregate type
    ///     records it, and <c>StreamState.AggregateType</c> resolves it. A stream with none cannot be
    ///     compacted through this door and says so, naming the generic overload — which is a real
    ///     answer, unlike declining for every stream.
    /// </remarks>
    Task IEventStore.CompactStreamAsync(Guid streamId, CancellationToken token)
        => CompactByStreamStateAsync(streamId, token);

    /// <inheritdoc cref="IEventStore.CompactStreamAsync(Guid, CancellationToken)" />
    Task IEventStore.CompactStreamAsync(string streamKey, CancellationToken token)
        => CompactByStreamStateAsync(streamKey, token);

    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification =
            "Closes CompactStreamAsync over the aggregate type named by the stream row. Aggregate types are preserved by projection registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private async Task CompactByStreamStateAsync(object streamIdentity, CancellationToken token)
    {
        await using var session = LightweightSession();

        var state = streamIdentity is Guid streamId
            ? await session.Events.FetchStreamStateAsync(streamId, token).ConfigureAwait(false)
            : await session.Events.FetchStreamStateAsync((string)streamIdentity, token).ConfigureAwait(false);

        if (state is null)
        {
            throw new InvalidOperationException($"Stream '{streamIdentity}' does not exist.");
        }

        if (state.AggregateType is null)
        {
            throw new InvalidOperationException(
                $"Stream '{streamIdentity}' records no aggregate type, so there is nothing to compact it "
                + "into. Either the stream was started without one, or this deployment cannot resolve the "
                + "type it names. Use the generic CompactStreamAsync<T> overload to say which aggregate "
                + "to compact into.");
        }

        var method = typeof(Events.EventOperations)
            .GetMethods()
            .Single(x => x.Name == nameof(Events.EventOperations.CompactStreamAsync)
                         && x.GetParameters()[0].ParameterType == streamIdentity.GetType())
            .MakeGenericMethod(state.AggregateType);

        await ((Task)method.Invoke(session.Events, [streamIdentity, null])!).ConfigureAwait(false);
    }
}

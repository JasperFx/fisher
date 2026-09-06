using System.Runtime.CompilerServices;
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
///         required members.
///     </para>
///     <para>
///         <b>Nothing here throws any more</b> (fisher#15). The standing discipline, for the next
///         member that arrives ahead of the feature, is that one Fisher cannot honour throws naming its
///         milestone rather than returning an empty result a monitoring tool would render as "no data".
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
    DatabaseCardinality IEventStore.DatabaseCardinality => Tenancy.Cardinality;

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

    IAsyncEnumerable<EventRecord> IEventStore.ReadStreamAsync(string streamId, CancellationToken ct)
        => ReadStreamAsync(streamId, null, ct);

    IAsyncEnumerable<EventRecord> IEventStore.ReadStreamAsync(string streamId, string? tenantId,
        CancellationToken ct)
        => ReadStreamAsync(streamId, tenantId, ct);

    /// <summary>
    ///     Every event of one stream, in version order, as wire <see cref="EventRecord" />s.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Materialised inside <see cref="StoreOptions.ResiliencePipeline" /> and then yielded,
    ///         rather than streamed out of it — the same reason <c>ReadStreamsAsync</c> gives above. A
    ///         retried <c>SQLITE_BUSY</c> re-executes the whole delegate, so handing a live reader to the
    ///         caller would let a retry resume against a connection the previous attempt had already
    ///         disposed. A single stream is a bounded read, so holding it in memory costs little.
    ///     </para>
    ///     <para>
    ///         Rows are returned whether or not this process can resolve their CLR event type — see
    ///         <see cref="FisherEventsRowReader.ReadEventRecord" />. That is what lets the
    ///         <c>projection-run</c> CLI and a monitoring console read a stream without the consumer's
    ///         event assemblies.
    ///     </para>
    /// </remarks>
    private async IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, string? tenantId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        // Same normalisation as GetStreamMetadataAsync: under Guid identity the parse validates the
        // input and lowercases it to the canonical form fi_events holds, because SQLite's default
        // collation is case-sensitive and an uppercase Guid would match nothing.
        var id = EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.Parse(streamId).ToString()
            : streamId;

        var tenantFilter = tenantId == null ? "" : "and tenant_id = @tenant_id\n                   ";

        var sql = $"""
                   select {FisherEventsRowReader.ComposeSelectColumns(EventGraph.EventOptions)}
                   from {EventGraph.EventsTableName}
                   where stream_id = @stream_id
                   {tenantFilter}order by version;
                   """;

        var ctx = new EventHydrationContext(
            EventGraph,
            Options.Serializer,
            id,
            defaultTenantId: StorageConstants.DefaultTenantId);

        var slots = MetadataSlots.For(EventGraph.EventOptions);

        var records = await Options.ResiliencePipeline.ExecuteAsync(async token =>
        {
            await using var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@stream_id", id);
            if (tenantId != null) command.Parameters.AddWithValue("@tenant_id", tenantId);

            var results = new List<EventRecord>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(FisherEventsRowReader.ReadEventRecord(reader, ctx, slots));
            }

            return (IReadOnlyList<EventRecord>)results;
        }, ct).ConfigureAwait(false);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }
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
    ///     <para>
    ///         Built by hand rather than through <see cref="EventStoreUsage" />'s reflective constructor,
    ///         which walks the subject's properties and would dump the store's runtime handles into the
    ///         descriptor as if they were configuration.
    ///     </para>
    ///     <para>
    ///         <b>Building it by hand is why fisher#120 happened, and the shape of that bug is the reason
    ///         to be exhaustive here rather than tidy.</b> An unfilled slot on this object is not read as
    ///         "this store does not describe that" — it is read as <em>the store has none</em>. The
    ///         missing <see cref="EventStoreUsage.Subscriptions" /> list made
    ///         <c>projections list</c> answer "No projections in this store" for a store with twenty of
    ///         them, and <c>projections rebuild</c> match none of them, with nothing anywhere reporting a
    ///         gap. Every list below is populated for that reason, not because a consumer was known to
    ///         want it.
    ///     </para>
    /// </remarks>
    async Task<EventStoreUsage?> IEventStore.TryCreateUsage(CancellationToken token)
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

        // Both event-type collections, because EventStoreUsage carries the registry twice and a
        // consumer is entitled to read either. Filling one alone is what polecat#411 was.
        foreach (var eventType in EventGraph.AllKnownEventTypes())
        {
            usage.Events.Add(new EventDescriptor(eventType.EventTypeName, TypeDescriptor.For(eventType.EventType)));

            usage.RegisteredEventTypes.Add(new EventTypeDescriptor(
                EventType: TypeDescriptor.For(eventType.EventType),
                Alias: eventType.EventTypeName,
                Description: null!));
        }

        foreach (var registration in EventGraph.TagTypes)
        {
            usage.TagTypes.Add(new TagTypeDescriptor
            {
                TagType = registration.TagType.FullName ?? registration.TagType.Name,
                SimpleType = registration.SimpleType.FullName ?? registration.SimpleType.Name,
                TableSuffix = registration.TableSuffix,
                AggregateType = registration.AggregateType?.FullName
            });

            usage.DcbTagTypes.Add(new DcbTagDescriptor(
                Name: registration.TagType.Name,
                SimpleType: registration.SimpleType.FullName ?? registration.SimpleType.Name,
                TagType: TypeDescriptor.For(registration.TagType),
                Description: null!));
        }

        // JasperFx/ProductSupport#3 — the two policies are separate because they differ: a rebuild
        // stops on an error a normal run would skip, and a console showing "view related dead
        // letters" for a store that halts instead offers a button that never returns anything.
        usage.ProjectionErrors = ErrorPolicyFor(Options.Projections.Errors);
        usage.ProjectionRebuildErrors = ErrorPolicyFor(Options.Projections.RebuildErrors);

        // jasperfx#475 — the four event columns are opt-in and read straight off the options. Every
        // stream facet is universal in Fisher, so those keep EventMetadataCapabilities' defaults.
        usage.EventMetadata = new EventMetadataCapabilities
        {
            StoreType = "Fisher",
            CorrelationId = Options.Events.EnableCorrelationId,
            CausationId = Options.Events.EnableCausationId,
            Headers = Options.Events.EnableHeaders,
            UserName = Options.Events.EnableUserName
        };

        usage.MaxEventSequence = await TryReadMaxEventSequenceAsync(token).ConfigureAwait(false);

        // fisher#120 — the line whose absence was the issue. Everything it fills is already built:
        // ProjectionGraph.Describe walks the registered projections and subscriptions, and Fisher's
        // two source types of its own (CompositeIProjectionSource, FlatTableProjection) implement
        // Describe. Nothing here is Fisher-specific, which is exactly why it was easy to leave out.
        Options.Projections.Describe(usage, this);

        return usage;
    }

    private static ProjectionErrorHandlingDescriptor ErrorPolicyFor(ErrorHandlingOptions options)
        => new()
        {
            SkipApplyErrors = options.SkipApplyErrors,
            SkipUnknownEvents = options.SkipUnknownEvents,
            SkipSerializationErrors = options.SkipSerializationErrors
        };

    /// <summary>
    ///     The highest sequence physically present in <c>fi_events</c>, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>On Fisher this is always equal to the high-water mark, and that is worth stating
    ///         rather than leaving a consumer to infer it.</b> The gap between the two is what
    ///         CritterWatch#150's second signal renders — a sequence issued but not yet safe to read —
    ///         and on Marten and Polecat it is real, because a server-side sequence or IDENTITY hands
    ///         out numbers outside the transaction. SQLite allows one writer per file and
    ///         <c>BEGIN IMMEDIATE</c> commits a transaction's sequences before the next writer
    ///         allocates any, so committed sequences are contiguous and the signal cannot fire here.
    ///         Reporting the number anyway is what lets a console see that, where leaving it null
    ///         renders as "n/a" and says nothing.
    ///     </para>
    ///     <para>
    ///         A failure is swallowed rather than propagated: this is a diagnostics call, and the most
    ///         likely reason the read fails is that the schema has not been created yet — which is
    ///         precisely when a monitoring tool is most likely to be pointed at the store. Failing the
    ///         whole description over one optional number would answer nothing at all.
    ///     </para>
    /// </remarks>
    private async Task<long?> TryReadMaxEventSequenceAsync(CancellationToken token)
    {
        try
        {
            return await Database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     The read-only event store slice, for monitoring tools.
    /// </summary>
    /// <remarks>
    ///     Returns a type that owns session lifetime rather than a captured session's
    ///     <c>Events</c>, which is what Polecat hands back — see
    ///     <see cref="Events.FisherReadOnlyEventStore" /> for why an embedded single-file store cannot
    ///     afford that shape.
    /// </remarks>
    IReadOnlyEventStore IEventStore.OpenReadOnlyEventStore() => new Events.FisherReadOnlyEventStore(this);

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

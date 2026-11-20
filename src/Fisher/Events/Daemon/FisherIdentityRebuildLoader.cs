using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Fisher.Events.Internal;
using Fisher.Events.Storage;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Events.Daemon;

/// <summary>
///     Reads one stream's events for a per-identity rebuild's re-fold (fisher#629, jasperfx#1201): the store side
///     of <c>IEventStore.LoadEventsForIdentityRebuildAsync</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The same rules as <see cref="FisherEventLoader" />, so the re-fold writes what the shard would
///         have.</b> The SQL is <see cref="SqliteEventPagingDialect" />'s own <c>where</c> with a stream
///         predicate added: archived events only when the projection reads them, the allow list with its
///         upcast source names, and an interface or abstract type reading every event (weasel#845). Rows are
///         hydrated through the same reader, so deserialization and upcasting are the daemon's.
///     </para>
///     <para>
///         The tenant is filtered under conjoined tenancy only, as a stream read is. Every other tenancy keeps
///         one tenant per file, and the database handed in is already that tenant's.
///     </para>
///     <para>
///         <b>A short answer means "no more".</b> JasperFx stops paging when it gets fewer events than it asked
///         for, so a row skipped under <c>SkipUnknownEvents</c> or <c>SkipSerializationErrors</c> must not
///         shorten the answer. The read goes on past it until it has the limit or runs out of rows. A row the
///         shard would not skip throws, which faults the rebuild request and leaves the shard running.
///         Nothing is dead-lettered here: the shard already did that when it met the row.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: hydrates IEvent rows through the row reader, as FisherEventLoader does. Event types are preserved by EventGraph registration on the caller side per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: ISerializer.FromJson and Event<T> construction are annotated RDC, as in FisherEventLoader. AOT consumers register concrete event types ahead of time.")]
internal sealed class FisherIdentityRebuildLoader
{
    private readonly FisherDatabase _database;
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly SqliteEventPagingDialect _dialect;

    internal FisherIdentityRebuildLoader(FisherDatabase database, StoreOptions options)
    {
        _database = database;
        _options = options;
        _events = options.EventGraph;
        _dialect = new SqliteEventPagingDialect(options);
    }

    internal async Task<IReadOnlyList<IEvent>> LoadAsync(IdentityRebuildEventRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isGuid = _events.StreamIdentity == StreamIdentity.AsGuid;
        var streamId = StreamIdFor(request.StreamIdentity, isGuid);
        var tenantId = _events.TenancyStyle == TenancyStyle.Conjoined ? request.TenantId : null;
        var allowList = EventTypeAllowList.For(_events, request.EventTypes);

        var events = await loadAsync(query => _dialect.BuildStreamPageCommand(query, streamId), allowList, tenantId,
            request.AfterSequence, request.Ceiling, request.Limit, request.IncludeArchivedEvents,
            request.SkipSerializationErrors, includeTags: false, token).ConfigureAwait(false);

        Diagnostics.DaemonTrace.Record("identity_rebuild.load", streamId.ToString(),
            request.AfterSequence, request.Ceiling, events.Count);

        return events;
    }

    /// <summary>
    ///     One identity's events from one source (fisher#652, marten#6003): the store side of
    ///     <c>IEventStore.LoadEventsForIdentityAsync</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every source is the daemon's page with one more predicate: the stream, a tag table subselect, a slice
    ///         index subselect, or nothing for a scan. So archived events, the allow list and the tenant rule are the
    ///         daemon's, as for a per-identity rebuild.
    ///     </para>
    ///     <para>
    ///         The tenant is filtered under conjoined tenancy only, except for the slice index, which lists events for
    ///         a slice group's tenant whatever tenant the event is in. The index rows are matched on the request's
    ///         tenant instead.
    ///     </para>
    /// </remarks>
    internal async Task<IReadOnlyList<IEvent>> LoadAsync(IdentityEventLoadRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conjoined = _events.TenancyStyle == TenancyStyle.Conjoined;
        var allowList = EventTypeAllowList.For(_events, request.EventTypes);
        var includeTags = request.IncludeTags || request.Source == IdentityEventSource.Tag;

        Func<EventPageQuery, Weasel.Storage.EventPageCommand> build;
        string? tenantId;

        switch (request.Source)
        {
            case IdentityEventSource.Stream:
                var streamId = StreamIdFor(request.Identity
                                           ?? throw new ArgumentException("A stream read needs an identity", nameof(request)),
                    _events.StreamIdentity == StreamIdentity.AsGuid);
                tenantId = conjoined ? request.TenantId : null;
                build = query => _dialect.BuildStreamPageCommand(query, streamId);
                break;

            case IdentityEventSource.Tag:
                var registration = (request.TagType is null ? null : _events.FindTagType(request.TagType))
                                   ?? throw new ArgumentException(
                                       $"Tag type {request.TagType?.Name ?? "(none)"} is not registered on this store", nameof(request));
                var tagValue = EventTagWriter.ToDatabaseValue(request.IdentityValue
                                                              ?? throw new ArgumentException("A tag read needs an identity", nameof(request)));
                tenantId = conjoined ? request.TenantId : null;
                build = query => _dialect.BuildFilteredPageCommand(query,
                    $"seq_id in (select seq_id from {_events.TagTableName(registration)} where value = @identity)",
                    new EventPageParameter("@identity", tagValue));
                break;

            case IdentityEventSource.SliceIndex:
                var table = _events.SliceIndexTableName(
                    request.ProjectionName ?? throw new ArgumentException("A slice index read needs a projection name", nameof(request)),
                    request.ProjectionVersion);
                var identity = FisherDatabase.SliceIndexIdentityValue(request.IdentityValue
                                                                      ?? throw new ArgumentException("A slice index read needs an identity", nameof(request)));
                var sliceTenant = request.TenantId ?? StorageConstants.DefaultTenantId;
                tenantId = null;
                build = query => _dialect.BuildFilteredPageCommand(query,
                    $"seq_id in (select seq_id from {table} where tenant_id = @slice_tenant and identity = @identity)",
                    new EventPageParameter("@slice_tenant", sliceTenant),
                    new EventPageParameter("@identity", identity));
                break;

            case IdentityEventSource.Scan:
                tenantId = conjoined ? request.TenantId : null;
                build = query => _dialect.BuildFilteredPageCommand(query, null);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Source, "Unknown identity event source");
        }

        var events = await loadAsync(build, allowList, tenantId, request.AfterSequence, request.Ceiling, request.Limit,
            request.IncludeArchivedEvents, request.SkipSerializationErrors, includeTags, token).ConfigureAwait(false);

        Diagnostics.DaemonTrace.Record("identity_events.load", request.Source.ToString(),
            request.AfterSequence, request.Ceiling, events.Count);

        return events;
    }

    private async Task<List<IEvent>> loadAsync(Func<EventPageQuery, Weasel.Storage.EventPageCommand> build,
        EventTypeAllowList allowList, string? tenantId, long afterSequence, long ceiling, int requestedLimit,
        bool includeArchivedEvents, bool skipSerializationErrors, bool includeTags, CancellationToken token)
    {
        var isGuid = _events.StreamIdentity == StreamIdentity.AsGuid;
        var limit = Math.Max(requestedLimit, 1);
        var errors = _options.Projections.Errors;

        var context = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            StorageConstants.DefaultTenantId);
        var slots = MetadataSlots.For(_options.Events);

        var events = new List<IEvent>();
        var floor = afterSequence;

        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);

        while (events.Count < limit)
        {
            var wanted = limit - events.Count;
            var page = build(new EventPageQuery
            {
                Floor = floor,
                Ceiling = ceiling,
                BatchSize = wanted,
                TenantId = tenantId,
                EventTypes = allowList,
                IncludeArchivedEvents = includeArchivedEvents
            });

            await using var command = connection.CreateCommand();
            command.CommandText = page.Sql;
            command.CommandTimeout = _options.CommandTimeout;
            foreach (var parameter in page.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            var rows = 0;
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    rows++;
                    floor = reader.GetInt64(0);

                    IEvent? @event;
                    try
                    {
                        @event = await FisherEventsRowReader
                            .ReadEventAcrossStreams(reader, context, slots, isGuid, token).ConfigureAwait(false);
                    }
                    // marten#6003 / decision 12: the request's own flag, never the store's continuous options, so a
                    // replay doesn't skip the very event it's meant to repair
                    catch (Exception e) when (EventLoaderBase.Classify(e) == ShardFailureCategory.EventSerialization
                                              && skipSerializationErrors)
                    {
                        continue;
                    }

                    if (@event is null)
                    {
                        if (errors.SkipUnknownEvents) continue;

                        var (_, dotNetTypeName) = FisherEventsRowReader.ReadUnresolvedIdentity(reader);
                        throw new UnknownEventTypeException(dotNetTypeName ?? "(null)", floor);
                    }

                    events.Add(@event);
                }
            }

            // Fewer rows than asked for means the source has nothing more in the range
            if (rows < wanted) break;
        }

        if (includeTags)
        {
            await FisherEventTagHydrator.HydrateAsync(_events, connection, events, _options.CommandTimeout, token)
                .ConfigureAwait(false);
        }

        return events;
    }

    // The projected identity as the projection knows it: the stream's Guid or string, or a strong-typed id
    // wrapping one (fisher#397's rule for the append side)
    private static object StreamIdFor(object identity, bool isGuid)
    {
        var streamIdType = isGuid ? typeof(Guid) : typeof(string);

        if (identity.GetType() == streamIdType) return identity;

        if (StrongTypedId.TryResolve(identity.GetType(), out var info) && info.SimpleType == streamIdType
                                                                       && info.ValueProperty.GetValue(identity) is { } inner)
        {
            return inner;
        }

        throw new ArgumentOutOfRangeException(nameof(identity),
            $"A per-identity rebuild was asked for '{identity}' ({identity.GetType().Name}), but this store's stream "
            + $"identity is {streamIdType.Name}. Pass a {streamIdType.Name} or a strong-typed id wrapping one.");
    }
}

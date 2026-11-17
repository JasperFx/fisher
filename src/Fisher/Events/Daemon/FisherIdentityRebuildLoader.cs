using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Fisher.Events.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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
        var limit = Math.Max(request.Limit, 1);
        var allowList = EventTypeAllowList.For(_events, request.EventTypes);
        var tenantId = _events.TenancyStyle == TenancyStyle.Conjoined ? request.TenantId : null;
        var errors = _options.Projections.Errors;

        var context = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            StorageConstants.DefaultTenantId);
        var slots = MetadataSlots.For(_options.Events);

        var events = new List<IEvent>();
        var floor = request.AfterSequence;

        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);

        while (events.Count < limit)
        {
            var wanted = limit - events.Count;
            var page = _dialect.BuildStreamPageCommand(new EventPageQuery
            {
                Floor = floor,
                Ceiling = request.Ceiling,
                BatchSize = wanted,
                TenantId = tenantId,
                EventTypes = allowList,
                IncludeArchivedEvents = request.IncludeArchivedEvents
            }, streamId);

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
                    catch (Exception e) when (EventLoaderBase.Classify(e) == ShardFailureCategory.EventSerialization
                                              && errors.SkipSerializationErrors)
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

            // Fewer rows than asked for means the stream has nothing more in the range
            if (rows < wanted) break;
        }

        Diagnostics.DaemonTrace.Record("identity_rebuild.load", streamId.ToString(),
            request.AfterSequence, request.Ceiling, events.Count);

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

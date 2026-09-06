using System.Diagnostics.CodeAnalysis;
using Fisher.Events.Internal;
using Fisher.Exceptions;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Events.Daemon;

/// <summary>
///     Pages events out of <c>fi_events</c> by sequence for the async daemon.
/// </summary>
/// <remarks>
///     <para>
///         The bare inner loader. Retry and load metrics are layered on by JasperFx's
///         <c>ResilientEventLoader</c> decorator when the store builds it, so nothing here handles
///         either.
///     </para>
///     <para>
///         Archived events are excluded. <c>EventsTable</c> carries an index on
///         <c>(is_archived, seq_id)</c> for exactly this predicate — without it the daemon's paging
///         would be a full scan filtered afterwards.
///     </para>
///     <para>
///         Row hydration goes through <see cref="FisherEventsRowReader.ReadEventAcrossStreams" />, the
///         same reader the DCB tag queries use: a page spans streams, so each event's identity has to
///         come off its own row rather than from the hydration context.
///     </para>
///     <para>
///         A subscription or projection that names its event types gets that filter pushed into the
///         SQL (<c>and type in (...)</c>), so non-matching rows are never read off disk, hydrated or
///         deserialized — the same strategy as Marten's <c>EventTypeFilter</c>. See the constructor
///         for why the page's ceiling accounting stays correct with the filter server-side.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: hydrates IEvent batches through the row reader, which routes to ISerializer.FromJson. Event types are preserved by EventGraph registration on the caller side per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: ISerializer.FromJson and Event<T> construction are annotated RDC. AOT consumers register concrete event types ahead of time.")]
internal sealed class FisherEventLoader : IEventLoader
{
    private readonly FisherDatabase _database;
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly HashSet<string>? _allowedEventTypeNames;
    private readonly string[]? _allowedTypeNameParameters;
    private readonly string _typeFilterSql = string.Empty;

    internal FisherEventLoader(FisherDatabase database, StoreOptions options, EventFilterable? filtering = null)
    {
        _database = database;
        _options = options;
        _events = options.EventGraph;

        // A subscription that names its event types reads only those. Matching on the stored type
        // alias rather than the .NET type name, so a renamed CLR type with a stable alias still
        // matches — the same choice the DCB tag queries make.
        if (filtering?.IncludedEventTypes is { Count: > 0 } included)
        {
            _allowedEventTypeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in included)
            {
                _allowedEventTypeNames.Add(_events.EventMappingFor(type).EventTypeName);
            }

            // The filter is pushed into SQL so non-matching rows never leave SQLite — a
            // subscription scoped to a few event types in a busy store would otherwise deserialize
            // essentially the whole event log to produce nothing. Marten pushes the same filter
            // down as its EventTypeFilter fragment.
            //
            // The page's ceiling accounting stays correct with the filter server-side, and it is
            // worth spelling out why. EventPage.CalculateCeiling's contract is "a page that did not
            // fill the batch exhausted everything up to the bound it was given": a filtered query
            // returning fewer than batch_size MATCHING rows really has scanned the whole
            // (floor, high-water] range, so taking the high-water mark as the ceiling steps the
            // floor past every non-matching sequence in it — that is what keeps a shard from
            // stalling on a run of filtered-out events. A full page takes the last matched
            // sequence, and the non-matching rows above it are re-scanned by the next request,
            // which never re-delivers anything (they still do not match).
            //
            // The set is materialized into a stable array because a HashSet's enumeration order is
            // unspecified — the parameter names in the SQL and the values bound each LoadAsync
            // must line up.
            _allowedTypeNameParameters = _allowedEventTypeNames.ToArray();
            var markers = string.Join(", ",
                Enumerable.Range(0, _allowedTypeNameParameters.Length).Select(i => $"@t{i}"));
            _typeFilterSql = $" and type in ({markers})";
        }
    }

    public async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
    {
        var page = new EventPage(request.Floor);
        var skipped = 0;

        var sql = $"""
                   select {FisherEventsRowReader.ComposeSelectColumns(_options.Events)}
                   from {_events.EventsTableName}
                   where seq_id > @floor and seq_id <= @ceiling and is_archived = 0{_typeFilterSql}
                   order by seq_id
                   limit @batch_size
                   """;

        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _options.CommandTimeout;
        command.Parameters.AddWithValue("@floor", request.Floor);
        command.Parameters.AddWithValue("@ceiling", request.HighWater);
        command.Parameters.AddWithValue("@batch_size", request.BatchSize);

        if (_allowedTypeNameParameters is not null)
        {
            for (var i = 0; i < _allowedTypeNameParameters.Length; i++)
            {
                command.Parameters.AddWithValue($"@t{i}", _allowedTypeNameParameters[i]);
            }
        }

        var ctx = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            JasperFx.StorageConstants.DefaultTenantId);
        var slots = MetadataSlots.For(_options.Events);
        var isGuid = _events.StreamIdentity == StreamIdentity.AsGuid;

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var @event = FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots, isGuid);

            if (@event is null)
            {
                // dotnet_type named a type this process cannot resolve. The stream reads skip such a
                // row unconditionally so a deployment can still read events it does not know about;
                // the daemon must not, because silently skipping one would leave the projection
                // permanently wrong with no signal. Honour the configured policy instead.
                if (request.ErrorOptions.SkipUnknownEvents)
                {
                    skipped++;
                    continue;
                }

                var (sequence, dotNetTypeName) = FisherEventsRowReader.ReadUnresolvedIdentity(reader);
                throw new UnknownEventTypeException(dotNetTypeName ?? "(null)", sequence);
            }

            if (_allowedEventTypeNames != null && !_allowedEventTypeNames.Contains(@event.EventTypeName))
            {
                // Belt and braces: the SQL type filter above already excludes these rows, so this
                // only fires if the rendered filter and the set ever disagree. Counting it as
                // skipped keeps the ceiling honest either way — the row consumed a slot of the
                // page's range.
                skipped++;
                continue;
            }

            page.Add(@event);
        }

        page.CalculateCeiling(request.BatchSize, request.HighWater, skipped);

        Diagnostics.DaemonTrace.Record("loader.page", request.Name.Identity,
            request.Floor, page.Ceiling, page.Count);

        return page;
    }
}

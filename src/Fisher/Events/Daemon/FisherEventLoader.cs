using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Weasel.Storage;
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
///         <b>The paging, skip accounting, ceiling calculation and cancellation translation are the shared
///         <see cref="EventLoaderBase" />'s</b> (weasel#566). Fisher supplies the SQL through
///         <see cref="SqliteEventPagingDialect" />, the row hydration, the dead letter for an unreadable body
///         and the trace points. Adopting the base also brings <c>EventPage.LastObservedSequence</c>
///         (jasperfx#667): a full batch whose every row was skipped takes its ceiling from the last row read
///         rather than claiming the high-water mark.
///     </para>
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
internal sealed class FisherEventLoader : EventLoaderBase
{
    private readonly FisherDatabase _database;
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly EventHydrationContext _context;
    private readonly MetadataSlots _slots;
    private readonly bool _isGuid;
    private readonly bool _includeTags;

    // fisher#652 / marten#6003: the unreadable events of the page being loaded, when its request parks them.
    // EventLoaderBase asks ShouldSkip with the error options alone, so the request's flag travels in the async
    // flow of LoadAsync rather than in a field the next page's load could see. A local workaround until
    // weasel#901 teaches the base about EventRequest.ParksUnreadableEvents; remove it then.
    private static readonly AsyncLocal<List<UnreadableEvent>?> _parking = new();

    internal FisherEventLoader(FisherDatabase database, StoreOptions options, EventFilterable? filtering = null)
        : base(new SqliteEventPagingDialect(options), new EventLoaderOptions
        {
            // Matching on the stored type alias rather than the .NET type name, so a renamed CLR type with
            // a stable alias still matches. The dialect widens it with upcast source names (fisher#191).
            EventTypes = EventTypeAllowList.For(options.EventGraph, filtering)
        })
    {
        _database = database;
        _options = options;
        _events = options.EventGraph;
        _context = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            JasperFx.StorageConstants.DefaultTenantId);
        _slots = MetadataSlots.For(_options.Events);
        _isGuid = _events.StreamIdentity == StreamIdentity.AsGuid;

        // marten#6003: a projection that groups by DCB tag (IdentityFromTag) needs the events' tags, which the
        // loader otherwise never reads
        _includeTags = filtering?.IncludeEventTags == true && _events.TagTypes.Count > 0;
    }

    public override async Task<EventPage> LoadAsync(EventRequest request, CancellationToken token)
    {
        var unreadable = request.ParksUnreadableEvents ? new List<UnreadableEvent>() : null;
        _parking.Value = unreadable;

        EventPage page;
        try
        {
            page = await base.LoadAsync(request, token).ConfigureAwait(false);
        }
        finally
        {
            _parking.Value = null;
        }

        if (unreadable is { Count: > 0 })
        {
            foreach (var @event in unreadable)
            {
                page.AddUnreadable(@event);
            }
        }

        if (_includeTags && page.Count > 0)
        {
            await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
            await FisherEventTagHydrator.HydrateAsync(_events, connection, page, _options.CommandTimeout, token)
                .ConfigureAwait(false);
        }

        Diagnostics.DaemonTrace.Record("loader.page", request.Name.Identity,
            request.Floor, page.Ceiling, page.Count);

        return page;
    }

    protected override async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken token)
        => await _database.OpenConnectionAsync(token).ConfigureAwait(false);

    protected override void ConfigureCommand(DbCommand command)
        => command.CommandTimeout = _options.CommandTimeout;

    protected override ValueTask<IEvent?> ReadEventAsync(DbDataReader reader, CancellationToken token)
        => FisherEventsRowReader.ReadEventAcrossStreams((SqliteDataReader)reader, _context, _slots, _isGuid, token);

    // dotnet_type named a type this process cannot resolve. The stream reads skip such a row
    // unconditionally so a deployment can still read events it does not know about; the daemon must not,
    // because silently skipping one would leave the projection permanently wrong with no signal. The base
    // honours SkipUnknownEvents and otherwise throws this.
    protected override Exception UnresolvedEventTypeException(DbDataReader reader, long sequence)
    {
        var (_, dotNetTypeName) = FisherEventsRowReader.ReadUnresolvedIdentity((SqliteDataReader)reader);
        return new JasperFx.Events.UnknownEventTypeException(dotNetTypeName ?? "(null)", sequence);
    }

    // fisher#308. A body that is there and will not parse is governed by SkipSerializationErrors, which the
    // base classifies off the exception's own IEventFailureContext. Quarantined on its own connection,
    // outside anything this page is doing — the same rule the apply-error dead letter follows.
    // marten#6003: when the request parks unreadable events, a body that won't deserialize is reported on the page
    // (and counted as skipped for the ceiling) instead of being dead-lettered or thrown
    protected override bool ShouldSkip(Exception ex, ErrorHandlingOptions options)
        => (_parking.Value is not null && Classify(ex) == ShardFailureCategory.EventSerialization)
           || base.ShouldSkip(ex, options);

    protected override async ValueTask RecordSkippedEventAsync(Exception ex, EventRequest request,
        CancellationToken token)
    {
        if (request.ParksUnreadableEvents && _parking.Value is { } parking &&
            FindDeserializationFailure(ex) is { } failure)
        {
            parking.Add(new UnreadableEvent(failure.Sequence, failure.StreamId, failure.StreamKey, failure.TenantId,
                failure.EventTypeName, failure));

            Diagnostics.DaemonTrace.Record("loader.unreadable", request.Name.Identity, failure.Sequence);
            return;
        }

        if (ex is JasperFx.Events.EventDeserializationFailureException e)
        {
            await _database.StoreDeadLetterEventAsync(null!, e.ToDeadLetterEvent(request.Name), token)
                .ConfigureAwait(false);

            Diagnostics.DaemonTrace.Record("loader.skipped_bad_body", request.Name.Identity, e.Sequence);
        }
    }

    private static JasperFx.Events.EventDeserializationFailureException? FindDeserializationFailure(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is JasperFx.Events.EventDeserializationFailureException failure) return failure;
            ex = ex.InnerException;
        }

        return null;
    }

    protected override void ReportLastObservedSequence(EventPage page, long? lastObservedSequence)
        => page.LastObservedSequence = lastObservedSequence;
}

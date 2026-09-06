using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Fisher.Events.Schema;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher.Events.Internal;

/// <summary>
///     The canonical SELECT projection and row reader for <c>fi_events</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The column order is locked here.</b> Every read composes its SELECT from
///         <see cref="ComposeSelectColumns" /> and reads positionally, so adding or renaming a
///         <c>fi_events</c> column means changing this file and only this file.
///     </para>
///     <para>
///         Every column conversion is explicit rather than delegated to
///         <c>DbDataReader.GetGuid</c> / <c>GetFieldValue&lt;DateTimeOffset&gt;</c>. That is
///         deliberate: Fisher stores Guids and timestamps as TEXT, and the write path converts them
///         explicitly on the way in (see <see cref="SqliteStorageDialect{TId}.ToDatabaseValue" /> and
///         <see cref="SqliteTimestamp" />). Reading them back through a provider convenience method
///         would leave the round trip depending on Microsoft.Data.Sqlite's coercion rules rather than
///         on Fisher's own storage decisions — the kind of asymmetry that breaks quietly under a
///         provider upgrade.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: hydrates IEvent instances via ISerializer.FromJson on the event data column and the event mapping's Wrap. Event types are preserved by EventGraph registration on the caller side.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: ISerializer.FromJson and Event<T>.MakeGenericType for envelope construction are annotated RDC. AOT consumers supply a source-generator-backed ISerializer.")]
internal static class FisherEventsRowReader
{
    /// <summary>
    ///     Mandatory columns, always projected. Ordinals 0–9.
    /// </summary>
    internal const string CoreSelectColumns =
        "seq_id, id, stream_id, version, data, type, timestamp, tenant_id, dotnet_type, is_archived";

    /// <summary>
    ///     How many columns <see cref="CoreSelectColumns" /> holds — the first ordinal any optional
    ///     metadata column can occupy.
    /// </summary>
    internal const int CoreColumnCount = 10;

    /// <summary>
    ///     <see cref="CoreSelectColumns" /> plus whichever optional metadata columns the active
    ///     options enable. Must stay in lockstep with <see cref="MetadataSlots.For" />.
    /// </summary>
    internal static string ComposeSelectColumns(EventStoreOptions options)
    {
        var sb = new StringBuilder(CoreSelectColumns);

        if (options.EnableCorrelationId)
        {
            sb.Append(", correlation_id");
        }

        if (options.EnableCausationId)
        {
            sb.Append(", causation_id");
        }

        if (options.EnableHeaders)
        {
            sb.Append(", headers");
        }

        if (options.EnableUserName)
        {
            sb.Append(", user_name");
        }

        // Last in the list, so every ordinal above it is unmoved by the optional columns before it.
        // Unconditional, because the column is (fisher#93).
        sb.Append(", data_binary");

        return sb.ToString();
    }

    /// <summary>
    ///     Read the current row as a hydrated <see cref="IEvent" /> for a Guid-identified stream.
    ///     Returns null when <c>dotnet_type</c> does not resolve to a type this process knows, so
    ///     callers can skip with a single <c>continue</c>.
    /// </summary>
    internal static async ValueTask<IEvent?> ReadEventAsGuid(DbDataReader reader, EventHydrationContext ctx,
        MetadataSlots slots, CancellationToken token)
    {
        var @event = await ReadEventCore(reader, ctx, slots, token).ConfigureAwait(false);

        if (@event is null)
        {
            return null;
        }

        @event.StreamId = ctx.StreamId is Guid g ? g : Guid.Empty;
        return @event;
    }

    /// <summary>
    ///     Read the current row as a hydrated <see cref="IEvent" /> for a string-identified stream.
    /// </summary>
    internal static async ValueTask<IEvent?> ReadEventAsString(DbDataReader reader, EventHydrationContext ctx,
        MetadataSlots slots, CancellationToken token)
    {
        var @event = await ReadEventCore(reader, ctx, slots, token).ConfigureAwait(false);

        if (@event is null)
        {
            return null;
        }

        @event.StreamKey = ctx.StreamId.ToString();
        return @event;
    }

    /// <summary>
    ///     Read the current row for a query that spans streams, taking the stream identity from the row
    ///     rather than from the context.
    /// </summary>
    /// <remarks>
    ///     The single-stream reads above take the identity from
    ///     <see cref="EventHydrationContext.StreamId" /> because the caller filtered on it and already
    ///     knows the answer. A DCB tag query does not: matching events can come from any number of
    ///     streams, and taking the identity from the context would stamp every result with the same
    ///     wrong id. <c>stream_id</c> is at ordinal 2 of <see cref="CoreSelectColumns" />, which is why
    ///     this belongs here rather than at the call site.
    /// </remarks>
    internal static async ValueTask<IEvent?> ReadEventAcrossStreams(DbDataReader reader,
        EventHydrationContext ctx, MetadataSlots slots, bool isGuidIdentity, CancellationToken token)
    {
        var @event = await ReadEventCore(reader, ctx, slots, token).ConfigureAwait(false);

        if (@event is null)
        {
            return null;
        }

        if (isGuidIdentity)
        {
            @event.StreamId = Guid.Parse(reader.GetString(2));
        }
        else
        {
            @event.StreamKey = reader.GetString(2);
        }

        return @event;
    }

    /// <summary>
    ///     The identity of a row whose <c>dotnet_type</c> did not resolve, for an error message.
    /// </summary>
    /// <remarks>
    ///     Exists so the async daemon can raise <c>UnknownEventTypeException</c> naming the offending
    ///     sequence and type when it is configured <em>not</em> to skip unknown events. Reading those
    ///     two columns at the call site instead would put ordinal knowledge outside this file, which is
    ///     the one thing the column order contract forbids.
    /// </remarks>
    internal static (long Sequence, string? DotNetTypeName) ReadUnresolvedIdentity(DbDataReader reader)
        => (reader.GetInt64(0), reader.IsDBNull(8) ? null : reader.GetString(8));

    /// <summary>
    ///     Decode a row whose body is in <c>data_binary</c>.
    /// </summary>
    /// <remarks>
    ///     Refused by name when the row is binary and the type has no serializer registered in this
    ///     process — the same row a differently configured process wrote perfectly well. Returning
    ///     null or falling through to the JSON placeholder would present an event with every member
    ///     at its default, which is a far worse answer than an exception naming the configuration.
    /// </remarks>
    private static object DeserializeBinary(DbDataReader reader, in MetadataSlots slots,
        FisherEventType mapping, Type resolvedType)
    {
        if (mapping.BinarySerializer is not { } serializer)
        {
            throw new InvalidOperationException(
                $"This event row's body is a BLOB in {EventsTable.TableSuffix}.data_binary, but no "
                + $"IEventBinarySerializer is registered for '{resolvedType.FullName}'. Set "
                + "StoreOptions.Events.DefaultBinarySerializer, or register one for this type with "
                + $"StoreOptions.Events.UseBinarySerializer<{resolvedType.Name}>(...).");
        }

        return serializer.Deserialize(resolvedType, (byte[])reader.GetValue(slots.BinaryDataIdx));
    }

    /// <summary>
    ///     Run the registered upcast transformation for this row's stored event type name, if there is
    ///     one, and return the transformed body. Null means "no transformation" — hydrate normally.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The stored event type name decides, and the <c>dotnet_type</c> hint does not get a
    ///         vote</b> — the marten#4680 authority rule, which the shared registry states as: a
    ///         registered transformation is the authoritative interpretation of its source name. It is
    ///         consulted <em>before</em> <c>ResolveEventType</c> for exactly that reason. The case it
    ///         exists for is a store that still has the old CLR type in its codebase: a typed append of
    ///         it writes both the source name and a <c>dotnet_type</c> pointing at the old type, and
    ///         letting the hint win would read those rows back as the old schema while every row
    ///         written by the previous deployment upcast correctly. Same store, same event type name,
    ///         two answers.
    ///     </para>
    ///     <para>
    ///         <b>Guarded on <c>HasAny</c> first</b>, so a store with no upcasts pays one boolean field
    ///         read per row and nothing else — no dictionary probe, no payload struct, no state
    ///         machine, since the method completes synchronously.
    ///     </para>
    /// </remarks>
    private static async ValueTask<object?> TryUpcast(DbDataReader reader, EventHydrationContext ctx,
        MetadataSlots slots, string typeName, CancellationToken token)
    {
        var upcasters = ctx.EventGraph.Upcasters;

        if (!upcasters.HasAny || !upcasters.TryFindTransformation(typeName, out var transformation))
        {
            return null;
        }

        var binary = reader.IsDBNull(slots.BinaryDataIdx)
            ? null
            : (byte[])reader.GetValue(slots.BinaryDataIdx);

        var payload = new Upcasting.FisherUpcastPayload(ctx.EventGraph, ctx.Serializer,
            reader.GetString(4), binary, typeName);

        // The async delegate, always — never the sync one. An async-only registration's synchronous
        // delegate throws UpcastingException by design, and Fisher has no synchronous read path to
        // reserve it for; a transformation registered the ordinary way wraps its sync delegate here
        // and completes without awaiting anything.
        return await transformation.UpcastAsync(payload, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Everything except the stream identity, which the specialized wrappers assign.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Asynchronous because upcasting can be</b> (fisher#191). The shared contract lets a
    ///         transformation be registered async-only, whose synchronous delegate throws by design —
    ///         so a store that hydrated synchronously could not honour one at all. Every Fisher read
    ///         path that reaches here is already inside an <c>await reader.ReadAsync(...)</c> loop, so
    ///         the change costs a <c>ValueTask</c> per row and nothing else; the ordinary path never
    ///         awaits anything and completes synchronously.
    ///     </para>
    /// </remarks>
    private static async ValueTask<IEvent?> ReadEventCore(DbDataReader reader, EventHydrationContext ctx,
        MetadataSlots slots, CancellationToken token)
    {
        var seqId = reader.GetInt64(0);
        var eventId = Guid.Parse(reader.GetString(1));
        // stream_id at ordinal 2 — the caller already knows it; not read here.
        var eventVersion = reader.GetInt64(3);
        var typeName = reader.GetString(5);
        var eventTimestamp = SqliteTimestamp.FromDatabaseValue(reader.GetString(6));
        var tenantId = reader.IsDBNull(7) ? ctx.DefaultTenantId : reader.GetString(7);
        var dotNetTypeName = reader.IsDBNull(8) ? null : reader.GetString(8);
        var isArchived = reader.GetInt64(9) != 0;

        var upcast = await TryUpcast(reader, ctx, slots, typeName, token).ConfigureAwait(false);

        var resolvedType = upcast?.GetType() ?? ctx.EventGraph.ResolveEventType(dotNetTypeName);

        if (resolvedType is null)
        {
            return null;
        }

        var mapping = ctx.EventGraph.EventMappingFor(resolvedType);

        // Whichever column holds this row's body — decided PER ROW, by data_binary being null or not,
        // never by the event type's current setting (fisher#93). That is what makes marking a type
        // [BinaryEvent] an in-place change: rows written before the change still carry JSON, and this
        // still reads them. A dispatch on the type would misread every one of them instead.
        var data = upcast ?? (reader.IsDBNull(slots.BinaryDataIdx)
            ? ctx.Serializer.FromJson(resolvedType, reader.GetString(4))
            : DeserializeBinary(reader, slots, mapping, resolvedType));

        var @event = mapping.Wrap(data);

        @event.Id = eventId;
        @event.Sequence = seqId;
        @event.Version = eventVersion;
        @event.Timestamp = eventTimestamp;
        @event.TenantId = tenantId;
        @event.EventTypeName = typeName;
        @event.DotNetTypeName = dotNetTypeName!;
        @event.IsArchived = isArchived;

        if (slots.CorrelationIdx >= 0)
        {
            @event.CorrelationId = reader.IsDBNull(slots.CorrelationIdx)
                ? null
                : reader.GetString(slots.CorrelationIdx);
        }

        if (slots.CausationIdx >= 0)
        {
            @event.CausationId = reader.IsDBNull(slots.CausationIdx)
                ? null
                : reader.GetString(slots.CausationIdx);
        }

        if (slots.HeadersIdx >= 0 && !reader.IsDBNull(slots.HeadersIdx))
        {
            @event.Headers = ctx.Serializer
                .FromJson<Dictionary<string, object>>(reader.GetString(slots.HeadersIdx));
        }

        if (slots.UserNameIdx >= 0)
        {
            @event.UserName = reader.IsDBNull(slots.UserNameIdx)
                ? null
                : reader.GetString(slots.UserNameIdx);
        }

        return @event;
    }
}

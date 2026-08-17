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
    internal static IEvent? ReadEventAsGuid(DbDataReader reader, in EventHydrationContext ctx,
        in MetadataSlots slots)
    {
        var @event = ReadEventCore(reader, ctx, slots);

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
    internal static IEvent? ReadEventAsString(DbDataReader reader, in EventHydrationContext ctx,
        in MetadataSlots slots)
    {
        var @event = ReadEventCore(reader, ctx, slots);

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
    internal static IEvent? ReadEventAcrossStreams(DbDataReader reader, in EventHydrationContext ctx,
        in MetadataSlots slots, bool isGuidIdentity)
    {
        var @event = ReadEventCore(reader, ctx, slots);

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
    ///     Everything except the stream identity, which the specialized wrappers assign.
    /// </summary>
    private static IEvent? ReadEventCore(DbDataReader reader, in EventHydrationContext ctx,
        in MetadataSlots slots)
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

        var resolvedType = ctx.EventGraph.ResolveEventType(dotNetTypeName);

        if (resolvedType is null)
        {
            return null;
        }

        var mapping = ctx.EventGraph.EventMappingFor(resolvedType);

        // Whichever column holds this row's body — decided PER ROW, by data_binary being null or not,
        // never by the event type's current setting (fisher#93). That is what makes marking a type
        // [BinaryEvent] an in-place change: rows written before the change still carry JSON, and this
        // still reads them. A dispatch on the type would misread every one of them instead.
        var data = reader.IsDBNull(slots.BinaryDataIdx)
            ? ctx.Serializer.FromJson(resolvedType, reader.GetString(4))
            : DeserializeBinary(reader, slots, mapping, resolvedType);

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

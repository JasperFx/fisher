using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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
    ///     Everything except the stream identity, which the specialized wrappers assign.
    /// </summary>
    private static IEvent? ReadEventCore(DbDataReader reader, in EventHydrationContext ctx,
        in MetadataSlots slots)
    {
        var seqId = reader.GetInt64(0);
        var eventId = Guid.Parse(reader.GetString(1));
        // stream_id at ordinal 2 — the caller already knows it; not read here.
        var eventVersion = reader.GetInt64(3);
        var json = reader.GetString(4);
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
        var data = ctx.Serializer.FromJson(resolvedType, json);
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

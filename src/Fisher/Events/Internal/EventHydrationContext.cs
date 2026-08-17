using Fisher.Serialization;

namespace Fisher.Events.Internal;

/// <summary>
///     The state a batch of event rows is hydrated against — stable for the whole read, so it is
///     built once by the caller rather than resolved per row.
/// </summary>
internal readonly struct EventHydrationContext
{
    public EventHydrationContext(EventGraph eventGraph, ISerializer serializer, object streamId,
        string defaultTenantId)
    {
        EventGraph = eventGraph;
        Serializer = serializer;
        StreamId = streamId;
        DefaultTenantId = defaultTenantId;
    }

    public EventGraph EventGraph { get; }
    public ISerializer Serializer { get; }

    /// <summary>
    ///     The stream being read. Known by the caller, so <c>stream_id</c> is never read back off the
    ///     row.
    /// </summary>
    public object StreamId { get; }

    public string DefaultTenantId { get; }
}

/// <summary>
///     Pre-computed column ordinals for the optional metadata columns, or -1 when the column is not
///     in the projection. Hoisted out of the per-row path so each branch resolves the same way for
///     every row in a batch instead of re-walking the options ladder.
/// </summary>
internal readonly struct MetadataSlots
{
    private MetadataSlots(int correlation, int causation, int headers, int userName, int binaryData)
    {
        CorrelationIdx = correlation;
        CausationIdx = causation;
        HeadersIdx = headers;
        UserNameIdx = userName;
        BinaryDataIdx = binaryData;
    }

    public int CorrelationIdx { get; }
    public int CausationIdx { get; }
    public int HeadersIdx { get; }
    public int UserNameIdx { get; }

    /// <summary>
    ///     Where <c>data_binary</c> sits (fisher#93). Never -1 — the column is unconditional, because
    ///     the row rather than the configuration is what says how a body is encoded.
    /// </summary>
    public int BinaryDataIdx { get; }

    /// <summary>
    ///     Compute the slots for the projection <see cref="FisherEventsRowReader.ComposeSelectColumns" />
    ///     produces. The order here must match that method exactly.
    /// </summary>
    public static MetadataSlots For(EventStoreOptions options)
    {
        var next = FisherEventsRowReader.CoreColumnCount;

        var correlation = options.EnableCorrelationId ? next++ : -1;
        var causation = options.EnableCausationId ? next++ : -1;
        var headers = options.EnableHeaders ? next++ : -1;
        var userName = options.EnableUserName ? next++ : -1;

        // Last, so adding it shifts nothing above it — the same reason fisher#29's session metadata
        // binders were appended rather than inserted.
        var binaryData = next;

        return new MetadataSlots(correlation, causation, headers, userName, binaryData);
    }
}

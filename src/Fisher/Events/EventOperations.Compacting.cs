using Fisher.Events.Protected;
using JasperFx.Events;
using JasperFx.Events.Protected;

namespace Fisher.Events;

/// <summary>
///     Stream compacting (fisher#10) — collapsing a stream's history into a single
///     <see cref="Compacted{T}" /> snapshot event.
/// </summary>
public partial class EventOperations
{
    /// <summary>
    ///     Replace this stream's events with a single <see cref="Compacted{T}" /> event carrying the
    ///     aggregate state, keeping anything appended after the point compacted to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Commits on its own.</b> Unlike the rest of <see cref="EventOperations" />, this does
    ///         not queue onto the caller's unit of work — it has to read the stream before it can decide
    ///         what to write, and the read has to see committed state rather than a half-built session.
    ///         Any work already pending on the session commits with it.
    ///     </para>
    ///     <para>
    ///         Compacting is one-way. The events below the snapshot are gone, so a projection rebuilt
    ///         afterwards rebuilds from the snapshot rather than from the history that produced it. That
    ///         is the trade compacting exists to make; pass an
    ///         <see cref="IEventsArchiver{TOperations}" /> on the request to copy the events somewhere
    ///         first.
    ///     </para>
    /// </remarks>
    public Task CompactStreamAsync<T>(Guid streamId, Action<StreamCompactingRequest<T>>? configure = null)
        where T : class
    {
        AssertGuidIdentity();

        var request = new StreamCompactingRequest<T>(streamId);
        configure?.Invoke(request);

        return StreamCompacting.ExecuteAsync(request, _session);
    }

    /// <inheritdoc cref="CompactStreamAsync{T}(Guid, Action{StreamCompactingRequest{T}})" />
    public Task CompactStreamAsync<T>(string streamKey, Action<StreamCompactingRequest<T>>? configure = null)
        where T : class
    {
        AssertStringIdentity();

        var request = new StreamCompactingRequest<T>(streamKey);
        configure?.Invoke(request);

        return StreamCompacting.ExecuteAsync(request, _session);
    }
}

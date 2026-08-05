using JasperFx.Events.Tags;

namespace Fisher.Events.Tags;

/// <summary>
///     Fisher's <see cref="IBatchedQuery" />: records the reads, then runs them in declaration order
///     when <see cref="Execute" /> is called.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not clever. Each declared read is a closure paired with a
///         <see cref="TaskCompletionSource{TResult}" />; <see cref="Execute" /> walks them in order,
///         resolving each. There is no statement coalescing, because on an embedded database the win
///         it buys elsewhere is not there to collect — see <see cref="IBatchedQuery" />.
///     </para>
///     <para>
///         A read that throws faults its own task <em>and</em> the <see cref="Execute" /> call, rather
///         than being swallowed so the caller discovers it later on an await. Faulting only the
///         individual task would let a caller who awaits <see cref="Execute" /> and nothing else
///         conclude the batch succeeded.
///     </para>
/// </remarks>
internal sealed class FisherBatchedQuery : IBatchedQuery
{
    private readonly EventOperations _events;
    private readonly List<Func<CancellationToken, Task>> _pending = [];

    internal FisherBatchedQuery(EventOperations events)
    {
        _events = events;
    }

    public Task<bool> EventsExist(EventTagQuery query)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending.Add(async token =>
        {
            var result = await _events.EventsExistAsync(query, token).ConfigureAwait(false);
            completion.SetResult(result);
        });

        return completion.Task;
    }

    public Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query) where T : class
    {
        var completion =
            new TaskCompletionSource<IEventBoundary<T>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending.Add(async token =>
        {
            var boundary = await _events.FetchForWritingByTags<T>(query, token).ConfigureAwait(false);
            completion.SetResult(boundary);
        });

        return completion.Task;
    }

    public async Task Execute(CancellationToken token = default)
    {
        // Taken and cleared first so a batch can be reused, and so a read that itself declares more
        // work does not extend the loop it is running in.
        var pending = _pending.ToArray();
        _pending.Clear();

        foreach (var read in pending)
        {
            await read(token).ConfigureAwait(false);
        }
    }
}

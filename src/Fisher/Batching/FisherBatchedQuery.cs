using JasperFx.Events.Tags;

namespace Fisher.Batching;

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
using Fisher.Events;
using Fisher.Linq;

internal sealed class FisherBatchedQuery : IBatchedQuery
{
    private readonly EventOperations _events;
    private readonly IQuerySession _session;
    private readonly List<Func<CancellationToken, Task>> _pending = [];

    internal FisherBatchedQuery(EventOperations events, IQuerySession session)
    {
        _events = events;
        _session = session;
    }

    /// <summary>
    ///     Record a read and hand back the task it will complete.
    /// </summary>
    /// <remarks>
    ///     Every member is this shape, so the ordering guarantee — reads run back to back against one
    ///     connection with nothing interleaved — is a property of one place rather than of each caller
    ///     remembering it.
    /// </remarks>
    private Task<T> Enqueue<T>(Func<CancellationToken, Task<T>> read)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending.Add(async token =>
        {
            try
            {
                completion.SetResult(await read(token).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                // Faulted rather than left uncompleted: a caller awaiting this item after Execute
                // would otherwise hang instead of seeing why. Execute rethrows as well — see there.
                completion.SetException(e);
                throw;
            }
        });

        return completion.Task;
    }

    public Task<T?> Load<T>(Guid id) where T : class => Enqueue(t => _session.LoadAsync<T>(id, t));
    public Task<T?> Load<T>(string id) where T : class => Enqueue(t => _session.LoadAsync<T>(id, t));
    public Task<T?> Load<T>(int id) where T : class => Enqueue(t => _session.LoadAsync<T>(id, t));
    public Task<T?> Load<T>(long id) where T : class => Enqueue(t => _session.LoadAsync<T>(id, t));

    public Task<IReadOnlyList<T>> LoadMany<T>(params Guid[] ids) where T : class
        => Enqueue(_ => _session.LoadManyAsync<T>(ids));

    public Task<IReadOnlyList<T>> LoadMany<T>(params string[] ids) where T : class
        => Enqueue(_ => _session.LoadManyAsync<T>(ids));

    public Task<bool> CheckExists<T>(Guid id) where T : class
        => Enqueue(t => _session.CheckExistsAsync<T>(id, t));

    public Task<bool> CheckExists<T>(string id) where T : class
        => Enqueue(t => _session.CheckExistsAsync<T>(id, t));

    public Task<IReadOnlyList<T>> Query<T>(Func<IQuerySession, IQueryable<T>> query) where T : notnull
        => Enqueue(t => query(_session).ToListAsync(t));

    public Task<T> QueryByPlan<T>(IQueryPlan<T> plan) => Enqueue(t => plan.Fetch(_session, t));

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

    /// <summary>
    ///     Run every declared read, in declaration order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A failing item does not stop the batch, and does not vanish either.</b> Every item
    ///         runs, each one's task is completed or faulted, and <c>Execute</c> then throws for
    ///         whatever failed. Stopping at the first failure would leave later items' tasks
    ///         uncompleted — so a caller awaiting one would hang rather than see an error — and
    ///         swallowing the failure into the item's task alone would let a caller who never awaits
    ///         that item conclude the batch succeeded.
    ///     </para>
    ///     <para>
    ///         One failure is rethrown as itself so the caller sees the real exception type; several
    ///         become an <see cref="AggregateException" />. Same rule the session's batch executor
    ///         follows.
    ///     </para>
    /// </remarks>
    public async Task Execute(CancellationToken token = default)
    {
        // Taken and cleared first so a batch can be reused, and so a read that itself declares more
        // work does not extend the loop it is running in.
        var pending = _pending.ToArray();
        _pending.Clear();

        var failures = new List<Exception>();

        foreach (var read in pending)
        {
            try
            {
                await read(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }
}

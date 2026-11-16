using System.Diagnostics;
using System.Threading.Channels;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     <b>Benchmark-only.</b> The fisher#547 candidate: an in-process single-writer queue in front of
///     one SQLite file, so concurrent writers stop racing for <c>BEGIN IMMEDIATE</c> and instead take
///     turns through one <see cref="Channel{T}" /> consumer.
/// </summary>
/// <remarks>
///     <para>
///         This is deliberately a wrapper <em>around</em> Fisher rather than a change inside it: each
///         writer builds its unit of work on its own session exactly as today, and only the
///         <c>SaveChangesAsync</c> call is handed to the queue. The consumer runs those commits one at
///         a time, so at most one Fisher connection in this process is ever inside <c>BEGIN IMMEDIATE</c>
///         and the busy handler never has a competitor to wait on. Nothing about Fisher's write path
///         — the resilience pipeline, the transaction, the batch — is altered, which is what makes the
///         comparison with the racing shape honest.
///     </para>
///     <para>
///         It is the plain serializing queue the issue names, <b>not group commit</b>: every queued
///         unit of work still gets its own transaction. Coalescing several queued sessions into one
///         transaction is the variant that usually makes a writer queue pay, and it is a different
///         design with different failure semantics (one bad unit of work fails its neighbours), so it
///         is not measured here.
///     </para>
///     <para>
///         The semantic change a caller would see is visible in the shape of <see cref="CommitAsync" />:
///         the returned task completes (or faults) when the <em>consumer</em> gets to the commit, not
///         when the caller made the call, so a failure surfaces after however long the queue ahead of
///         it took. An enlisted session (<c>SessionOptions.ForTransaction</c>) cannot be queued at all:
///         the caller already holds the transaction, and therefore the write lock, on its own thread.
///     </para>
/// </remarks>
public sealed class SingleWriterQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;

    private readonly Task _consumer;

    /// <param name="capacity">
    ///     Null for the unbounded channel round 1 measured. A number for fisher#547 round 2's bounded
    ///     variant: <see cref="BoundedChannelFullMode.Wait" />, so a writer finding the queue full waits
    ///     (backpressure) before its commit is even queued.
    /// </param>
    public SingleWriterQueue(int? capacity = null)
    {
        _channel = capacity is { } bound
            ? Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(bound)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
            })
            : Channel.CreateUnbounded<WorkItem>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    ///     Milliseconds each commit spent queued before the consumer started it, one entry per commit.
    ///     The racing shape's equivalent is the time inside <c>BEGIN IMMEDIATE</c>.
    /// </summary>
    public List<double> QueueWaits { get; } = new();

    /// <summary>
    ///     Queue <paramref name="commit" /> (normally a session's <c>SaveChangesAsync</c>) and complete when
    ///     the consumer has run it. A failure inside the commit faults the returned task — later than
    ///     the call, by the queue's length.
    /// </summary>
    public Task CommitAsync(Func<Task> commit)
    {
        var item = new WorkItem(commit, Stopwatch.GetTimestamp());
        if (_channel.Writer.TryWrite(item))
        {
            return item.Completion.Task;
        }

        // Only a bounded channel that is full gets here: wait for room, then for the commit.
        return WriteThenAwaitAsync(item);
    }

    private async Task WriteThenAwaitAsync(WorkItem item)
    {
        await _channel.Writer.WriteAsync(item).ConfigureAwait(false);
        await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Only the consumer touches the list, so no lock.
            QueueWaits.Add(Stopwatch.GetElapsedTime(item.EnqueuedAt).TotalMilliseconds);

            try
            {
                await item.Commit().ConfigureAwait(false);
                item.Completion.TrySetResult();
            }
            catch (Exception e)
            {
                item.Completion.TrySetException(e);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private sealed record WorkItem(Func<Task> Commit, long EnqueuedAt)
    {
        // RunContinuationsAsynchronously so a writer's continuation never runs on the consumer's
        // thread and holds up the next commit.
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     <b>Benchmark-only prototype.</b> fisher#547 round 2's group commit: one consumer takes up to
///     <c>maxGroup</c> queued units of work, runs them all inside one <c>BEGIN IMMEDIATE</c> transaction,
///     each as an enlisted session under its own <c>SAVEPOINT</c>, and commits once.
/// </summary>
/// <remarks>
///     <para>
///         <b>The caller hands over a unit of work, not a session.</b> An enlisted session has to be
///         opened on the transaction (<see cref="SessionOptions.ForTransaction" />), and the transaction
///         only exists on the consumer, so the writer's own session cannot be the one that commits. The
///         writer passes a delegate that the consumer replays onto a fresh enlisted session. Anything the
///         writer read through its own session (a stream version, a loaded document) was read outside the
///         transaction that writes, which is the same as today for the optimistic guards but means the
///         writer's session-level state (identity map, change tracking, listeners on that session) does
///         not carry over.
///     </para>
///     <para>
///         <b>One savepoint per unit of work</b>, so a unit that throws is rolled back to its savepoint
///         and fails alone while its neighbours continue. A unit whose savepoint was released is still
///         not durable until the group's single <c>COMMIT</c>: if that fails, every released unit in the
///         group fails with it, including ones that did nothing wrong.
///     </para>
///     <para>
///         The connection is opened once, before any writer starts, with the store's own connection
///         PRAGMAs applied, and reused for every group. Opening it on demand would be an open of the
///         store's pool outside Fisher's fisher#576 open gate.
///     </para>
/// </remarks>
public sealed class GroupCommitQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly SqliteConnection _connection;
    private readonly int _maxGroup;
    private readonly DocumentStore _store;
    private readonly Task _consumer;

    private GroupCommitQueue(DocumentStore store, SqliteConnection connection, int maxGroup)
    {
        _store = store;
        _connection = connection;
        _maxGroup = maxGroup;
        _consumer = Task.Run(ConsumeAsync);
    }

    public static async Task<GroupCommitQueue> StartAsync(DocumentStore store, string connectionString, int maxGroup)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await store.Options.PragmaSettings.ApplyConnectionPragmasAsync(connection);
        return new GroupCommitQueue(store, connection, maxGroup);
    }

    /// <summary>Units of work per transaction, one entry per group.</summary>
    public List<int> GroupSizes { get; } = new();

    /// <summary>Milliseconds the consumer waited inside <c>BEGIN IMMEDIATE</c>, one entry per group.</summary>
    public List<double> BeginWaits { get; } = new();

    /// <summary>Milliseconds each unit of work was queued before its group began.</summary>
    public List<double> QueueWaits { get; } = new();

    /// <summary>Units of work rolled back to their savepoint.</summary>
    public int RolledBackAlone { get; private set; }

    /// <summary>Groups whose final COMMIT failed (every unit in them failed).</summary>
    public int FailedGroups { get; private set; }

    public Task CommitAsync(Func<IDocumentSession, Task> apply)
    {
        var item = new WorkItem(apply, Stopwatch.GetTimestamp());
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("The group commit queue has been completed.");
        }

        return item.Completion.Task;
    }

    private async Task ConsumeAsync()
    {
        var group = new List<WorkItem>(_maxGroup);
        var failures = new List<Exception?>(_maxGroup);

        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            group.Clear();
            failures.Clear();
            while (group.Count < _maxGroup && _channel.Reader.TryRead(out var next))
            {
                group.Add(next);
                failures.Add(null);
            }

            if (group.Count == 0) continue;

            GroupSizes.Add(group.Count);

            var beginStarted = Stopwatch.GetTimestamp();
            foreach (var item in group)
            {
                QueueWaits.Add(Stopwatch.GetElapsedTime(item.EnqueuedAt, beginStarted).TotalMilliseconds);
            }

            SqliteTransaction transaction;
            try
            {
                transaction = (SqliteTransaction)await _connection.BeginTransactionAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                FailedGroups++;
                foreach (var item in group) item.Completion.TrySetException(e);
                continue;
            }

            BeginWaits.Add(Stopwatch.GetElapsedTime(beginStarted).TotalMilliseconds);

            await using (transaction)
            {
                for (var i = 0; i < group.Count; i++)
                {
                    transaction.Save("unit");
                    try
                    {
                        await using var session = _store.OpenSession(SessionOptions.ForTransaction(transaction));
                        await group[i].Apply(session).ConfigureAwait(false);
                        await session.SaveChangesAsync().ConfigureAwait(false);
                        transaction.Release("unit");
                    }
                    catch (Exception e)
                    {
                        transaction.Rollback("unit");
                        transaction.Release("unit");
                        failures[i] = e;
                        RolledBackAlone++;
                    }
                }

                try
                {
                    await transaction.CommitAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    FailedGroups++;
                    for (var i = 0; i < group.Count; i++) failures[i] ??= e;
                }
            }

            for (var i = 0; i < group.Count; i++)
            {
                if (failures[i] is { } failure) group[i].Completion.TrySetException(failure);
                else group[i].Completion.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
        await _connection.DisposeAsync();
    }

    private sealed record WorkItem(Func<IDocumentSession, Task> Apply, long EnqueuedAt)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

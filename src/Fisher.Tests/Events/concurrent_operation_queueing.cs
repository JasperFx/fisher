using System.Data.Common;
using Fisher.Internal;
using JasperFx;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#13 — the session's operation queue under concurrent writers.
/// </summary>
/// <remarks>
///     <para>
///         A session is normally driven by one caller, so this looks like a property nobody needs. The
///         async daemon is the exception: JasperFx's <c>ExecutionStage</c> fans its executions out with
///         <c>Task.WhenAll</c> and they all queue onto the <em>same</em> Fisher session, so two
///         projection slices can call <c>QueueOperation</c> at the same instant.
///     </para>
///     <para>
///         <c>List&lt;T&gt;.Add</c> is not thread-safe and the way it fails here is silent: two
///         concurrent adds can leave the count incremented once, so one slice's document write never
///         reaches the batch — which then commits the progression row for a range whose documents were
///         only partly written, with no exception, no shard failure and no dead letter. That was
///         fisher#13, and it presented as a multi-stream rebuild intermittently missing one slice's
///         document about one full-suite run in five.
///     </para>
///     <para>
///         Ten thousand concurrent adds is far past the point where losing at least one is certain, so
///         this fails essentially every run without the lock rather than being a flaky test about a
///         flaky bug.
///     </para>
/// </remarks>
public class concurrent_operation_queueing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("queueing");
    private DocumentStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task every_concurrently_queued_operation_reaches_the_batch()
    {
        const int writers = 4;
        const int each = 2_500;

        await using var session = (FisherSession)_store.LightweightSession();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            // All four writers released at once, so the adds genuinely overlap rather than being
            // serialised by staggered thread starts.
            await start.Task;

            for (var i = 0; i < each; i++)
            {
                session.QueueOperation(new NoopOperation());
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);

        session.TakePendingOperations().Count.ShouldBe(writers * each);
    }

    /// <summary>
    ///     Taking the operations while another writer is still queueing must not throw or skip either —
    ///     the batch's <c>TakePendingOperations</c> and a slice's <c>QueueOperation</c> are exactly this
    ///     pair.
    /// </summary>
    [Fact]
    public async Task taking_while_another_writer_queues_loses_nothing()
    {
        const int total = 10_000;

        await using var session = (FisherSession)_store.LightweightSession();

        var taken = 0;
        var writing = Task.Run(() =>
        {
            for (var i = 0; i < total; i++)
            {
                session.QueueOperation(new NoopOperation());
            }
        });

        while (!writing.IsCompleted)
        {
            taken += session.TakePendingOperations().Count;
        }

        await writing;
        taken += session.TakePendingOperations().Count;

        taken.ShouldBe(total);
    }

    private sealed class NoopOperation : Weasel.Storage.IStorageOperation
    {
        public Type DocumentType => typeof(object);

        public OperationRole Role() => OperationRole.Other;

        public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
        {
        }

        public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
            => Task.CompletedTask;
    }
}

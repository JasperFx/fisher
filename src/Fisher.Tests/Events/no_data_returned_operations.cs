using System.Data.Common;
using System.Reflection;
using Fisher.Internal;
using JasperFx;
using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Storage;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#66 — the <c>NoDataReturnedCall</c> audit, from the marten#5210 class.
/// </summary>
/// <remarks>
///     <para>
///         <b>The class:</b> Marten's batch executor concatenates a unit of work into one command and
///         walks the result sets with <c>NextResultAsync</c>, skipping the advance for an operation
///         marked <see cref="NoDataReturnedCall" />. marten#5210 was an operation carrying that marker
///         whose SQL <em>did</em> return a row (<c>select pg_notify(…)</c>) — so the reader stayed one
///         result set behind and every operation after it in the batch postprocessed against somebody
///         else's rows. Silent, and the symptom surfaces nowhere near the cause.
///     </para>
///     <para>
///         <b>Fisher cannot misalign, and that is worth pinning rather than asserting from memory.</b>
///         <c>FisherSession.ExecuteBatchAsync</c> compiles and executes each operation as its own
///         command with its own reader — see the remarks there for why (operations bind by position,
///         and the append operation ends in a SELECT). There is no <c>NextResult</c> walk to fall
///         behind, so the marker steers nothing at execution time and a mislabelled operation costs
///         nothing. <c>a_mislabelled_operation_cannot_misalign_the_batch</c> is that property, planted
///         with exactly marten#5210's shape.
///     </para>
///     <para>
///         The marker is still checked, because it is a claim in the code that a reader would trust and
///         because the execution strategy could change: any operation declaring it must genuinely
///         return no result set. That is asserted by <em>executing</em> the compiled SQL rather than by
///         reading it, since the thing that would go wrong — a <c>returning</c> clause added to a
///         statement whose operation still declares no-data — is a property of what SQLite does with
///         the statement, not of how it is spelled.
///     </para>
/// </remarks>
public class no_data_returned_operations : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("no-data-returned");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Bait>().SoftDeleted();
            options.Schema.For<Rod>().UseOptimisticConcurrency();

            // Registering an aggregate that declares a natural key is what puts fi_natural_key_order
            // into the migration, which the replay operation below needs to execute against
            // (fisher#206). Nothing here appends to it — the audit only wants the statement's shape.
            options.Projections.Add(new natural_keys.OrderProjection(),
                JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        // The tables have to exist before an operation's SQL can be executed against them — a document
        // table is created on demand at commit, and SQLite resolves a table name when it *prepares* a
        // statement, so a probe against a table that was never written fails before it runs.
        await using var session = _store.LightweightSession();
        session.Store(new Bait { Id = Guid.NewGuid(), Depth = 3 });
        session.Store(new Rod { Id = Guid.NewGuid(), Length = 9 });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     Every operation this store can queue that declares the marker really does return nothing.
    /// </summary>
    [Fact]
    public async Task an_operation_declaring_no_data_returns_no_result_set()
    {
        var marked = MarkedOperations(BuildEveryKindOfOperation());
        marked.ShouldNotBeEmpty("the unit of work below produces no marked operations, so this asserts nothing");

        await using var session = (FisherSession)_store.LightweightSession();
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // Rolled back rather than committed: these are real deletes, and the question is only what
        // shape SQLite hands back for them.
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(
                TestContext.Current.CancellationToken);

        foreach (var operation in marked)
        {
            var builder = new CommandBuilder();
            operation.ConfigureCommand(builder, session);

            var command = builder.Compile();
            command.Connection = connection;
            command.Transaction = transaction;

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            // A statement returning no result set has no columns at all; anything with a `returning`
            // clause or a leading `select` reports at least one.
            reader.FieldCount.ShouldBe(0,
                $"{operation.GetType().Name} declares NoDataReturnedCall but its SQL returns a result set: {command.CommandText}");
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Every concrete operation type in Fisher that declares the marker is covered by the check
    ///     above, so a new one cannot be added and quietly go unaudited.
    /// </summary>
    /// <remarks>
    ///     Matched by generic type definition, since the operations are closed over a document type. A
    ///     new marked operation type makes this fail naming itself, which is the prompt to add whatever
    ///     queues it to <c>BuildEveryKindOfOperation</c> rather than to widen the filter here.
    /// </remarks>
    [Fact]
    public void every_marked_operation_type_in_fisher_is_covered()
    {
        var declared = typeof(DocumentStore).Assembly
            .GetTypes()
            .Where(x => x is { IsAbstract: false, IsInterface: false })
            .Where(x => x.GetInterfaces().Contains(typeof(NoDataReturnedCall)))
            .Select(Definition)
            .ToHashSet();

        // Not merely "nothing uncovered" — an assembly scan that found nothing would agree.
        declared.ShouldNotBeEmpty();

        var covered = MarkedOperations(BuildEveryKindOfOperation())
            .Select(x => Definition(x.GetType()))
            .ToHashSet();

        declared.Except(covered).ShouldBeEmpty();

        static Type Definition(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }

    /// <summary>
    ///     An operation that lies about returning no data leaves the operations after it unharmed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is marten#5210 planted directly: an operation declaring <see cref="NoDataReturnedCall" />
    ///         whose SQL returns a row, queued ahead of one whose postprocessing reads its own result
    ///         set. On a batch that walks one reader with <c>NextResult</c>, the optimistic upsert below
    ///         would read the planted row, find an id it did not write, and report a concurrency failure
    ///         for a write that succeeded.
    ///     </para>
    ///     <para>
    ///         It passes here because each operation gets its own command and its own reader. That is
    ///         the property under test — not the marker, which nothing in Fisher's executor reads.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_mislabelled_operation_cannot_misalign_the_batch()
    {
        var rod = new Rod { Id = Guid.NewGuid(), Length = 9 };

        await using (var session = (FisherSession)_store.LightweightSession())
        {
            session.QueueOperation(new LyingOperation());
            session.Store(rod);
            session.QueueOperation(new LyingOperation());

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<Rod>(rod.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()
            .Length.ShouldBe(9);

        // And the version the upsert read back for itself, not a row the liar produced.
        var metadata = await query.MetadataForAsync(rod, TestContext.Current.CancellationToken);
        metadata.ShouldNotBeNull();
        metadata.Version.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    ///     One of every operation shape this store can produce that might carry the marker.
    /// </summary>
    private IReadOnlyList<Weasel.Storage.IStorageOperation> BuildEveryKindOfOperation()
    {
        var session = (FisherSession)_store.LightweightSession();

        try
        {
            var bait = new Bait { Id = Guid.NewGuid(), Depth = 3 };
            var rod = new Rod { Id = Guid.NewGuid(), Length = 9 };

            session.Store(bait);
            session.Store(rod);

            session.Delete(bait);                              // soft delete — an update
            session.HardDelete(bait);                          // hard delete of a soft-deleted type
            session.Delete<Rod>(rod.Id);                       // delete by id
            session.Delete(rod);                               // delete by document
            session.DeleteWhere<Bait>(x => x.Depth > 5);
            session.HardDeleteWhere<Bait>(x => x.Depth > 5);
            session.UndoDeleteWhere<Bait>(x => x.Depth > 5);
            session.DeleteWhere<Rod>(x => x.Length > 5);

            // The natural key lookup's replay write (fisher#206). Queued directly rather than reached
            // through an append, because the path that produces one is the daemon's rather than a
            // session's — and what the audit wants is the statement, not the route to it.
            session.QueueOperation(new Fisher.Events.Storage.NaturalKeyReplayOperation(
                _store.Options.EventGraph, typeof(natural_keys.Order), "AUDIT",
                Guid.NewGuid().ToString("D"), "*DEFAULT*"));

            return session.TakePendingOperations();
        }
        finally
        {
            // Disposed without committing: the operations are wanted, the writes are not.
            session.Dispose();
        }
    }

    private static IReadOnlyList<Weasel.Storage.IStorageOperation> MarkedOperations(
        IEnumerable<Weasel.Storage.IStorageOperation> operations)
        => operations.Where(x => x is NoDataReturnedCall).ToList();

    /// <summary>
    ///     Declares no data and returns a row — marten#5210's shape.
    /// </summary>
    private sealed class LyingOperation : Weasel.Storage.IStorageOperation, NoDataReturnedCall
    {
        public Type DocumentType => typeof(object);

        public OperationRole Role() => OperationRole.Other;

        public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
            => builder.Append("select 1 as id");

        public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
            => Task.CompletedTask;
    }

    public class Bait
    {
        public Guid Id { get; set; }
        public int Depth { get; set; }
    }

    public class Rod
    {
        public Guid Id { get; set; }
        public int Length { get; set; }
    }
}

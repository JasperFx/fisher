using System.Data;
using Fisher.Services;
using JasperFx;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#104 / jasperfx#679 — the two halves of the shared commit-listener contract that
///     <c>DocumentCommitListenerCompliance</c> cannot reach.
/// </summary>
/// <remarks>
///     <para>
///         The shared suite proves the middle of the story: a listener registered through the
///         contract is invoked, once per commit, with a snapshot of what the commit wrote. It cannot
///         prove the two ends, and both are silent when broken.
///     </para>
///     <para>
///         <b>The outbound half</b> is the default interface implementation on
///         <see cref="IDocumentSessionListener" /> that makes every Fisher listener an
///         <see cref="IDocumentCommitListener" />. Nothing inside Fisher calls it — Fisher's firing
///         site calls Fisher's own member — so a wrong forward there compiles, ships, and is never
///         executed by any test in the library.
///     </para>
///     <para>
///         <b>The two firing divergences</b> are the other end. The shared suite deliberately
///         asserts nothing about an empty unit of work or about a session enlisted in a caller's
///         transaction, because the contract permits either answer and Marten's differs from
///         Fisher's. <c>session_listeners</c> pins both through Fisher's own interface; these pin
///         them through the contract, which is the surface a store-agnostic consumer sees and the
///         one that could drift independently.
///     </para>
/// </remarks>
public class commit_listener_contract : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("commit_contract");
    private readonly RecordingCommitContractListener _listener = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ListenerFly>();

            // The shipped registration route for a listener that implements only the shared
            // contract: adapted onto Fisher's own listener type and added to the one Listeners
            // collection. There is no second list to add it to, on purpose.
            options.Listeners.Add(_listener.AsSessionListener());
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <remarks>
    ///     The outbound forward. A Fisher listener handed to code that only knows the shared type
    ///     has to arrive at the Fisher member with both arguments intact — and the forward casts
    ///     both, so a wrong one is an <see cref="InvalidCastException" /> rather than a wrong value.
    /// </remarks>
    [Fact]
    public async Task a_fisher_listener_is_reachable_through_the_shared_contract()
    {
        var fisherListener = new RecordingListener();
        IDocumentCommitListener asContract = fisherListener;

        await using var session = _store.LightweightSession();
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Outbound" });
        await session.SaveChangesAsync(Token);

        var commit = _listener.Commits.ShouldHaveSingleItem();

        await asContract.AfterCommitAsync(commit.Session, commit.Commit, Token);

        // Reached the Fisher member, not a default and not the contract member recursing.
        fisherListener.Hooks.ShouldBe(["after"]);
    }

    /// <remarks>
    ///     A Fisher listener already satisfies the contract, so adapting one would wrap a listener
    ///     that needs no wrapping — and, registered alongside itself, would fire it twice.
    /// </remarks>
    [Fact]
    public void adapting_a_listener_that_is_already_a_fisher_listener_returns_it_unchanged()
    {
        var fisherListener = new RecordingListener();

        ((IDocumentCommitListener)fisherListener).AsSessionListener().ShouldBeSameAs(fisherListener);
    }

    [Fact]
    public async Task the_contract_listener_sees_what_the_commit_wrote()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = id, Pattern = "Royal Coachman" });
            await session.SaveChangesAsync(Token);
        }

        var commit = _listener.Commits.ShouldHaveSingleItem().Commit;

        commit.Updated.ShouldHaveSingleItem().ShouldBeOfType<ListenerFly>().Id.ShouldBe(id);
        commit.Inserted.ShouldBeEmpty();
        commit.Deleted.ShouldBeEmpty();
    }

    /// <remarks>
    ///     Fisher's divergence #1, seen through the contract. The shared suite says nothing about
    ///     this case because Marten's answer was never stated; Fisher's is stated, so it is pinned
    ///     here rather than left to be rediscovered.
    /// </remarks>
    [Fact]
    public async Task an_empty_unit_of_work_does_not_reach_the_contract_listener()
    {
        await using var session = _store.LightweightSession();
        await session.SaveChangesAsync(Token);

        _listener.Commits.ShouldBeEmpty();
    }

    /// <remarks>
    ///     Fisher's divergence #2, and the sharper one: Marten fires unconditionally here.
    ///     <c>SaveChangesAsync</c> on an enlisted session is not the point at which the data becomes
    ///     durable, and Fisher is never told when the caller's transaction commits — so the callback
    ///     would be announcing writes an outer rollback can still discard. The contract explicitly
    ///     permits both answers; this is Fisher's.
    /// </remarks>
    [Fact]
    public async Task an_enlisted_session_does_not_reach_the_contract_listener()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Enlisted" });
            await session.SaveChangesAsync(Token);
        }

        _listener.Commits.ShouldBeEmpty();

        await transaction.CommitAsync(Token);
    }
}

/// <summary>
///     A listener that implements the shared contract and nothing else — the store-agnostic shape
///     jasperfx#679 exists to make registrable.
/// </summary>
public class RecordingCommitContractListener : IDocumentCommitListener
{
    public List<(IDocumentSessionOperations Session, IDocumentChangeSet Commit)> Commits { get; } = [];

    public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
        CancellationToken token)
    {
        Commits.Add((session, commit));
        return Task.CompletedTask;
    }
}

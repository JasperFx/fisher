using System.Data.Common;
using JasperFx;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Documents;

/// <summary>
///     <c>IDocumentReadOperations.LoadManyAsync&lt;T&gt;(IEnumerable&lt;Guid&gt;)</c> and its string twin
///     (jasperfx#930).
/// </summary>
/// <remarks>
///     The shared <c>DocumentLoadAndStoreCompliance</c> facts pin the RESULT, which the contract's
///     one-at-a-time default gets right too — so every one of them passes whether or not Fisher's
///     explicit implementation exists. Fisher's own <c>params</c> overloads do not satisfy the
///     <c>IEnumerable</c> members, so deleting the forwarders compiles and stays green. What tells the
///     two apart is the number of statements, which is what this counts.
/// </remarks>
public class loading_many_through_the_contract : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("load-many-contract");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_guid_load_many_is_one_statement_with_repeats_collapsed()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        await using (var session = _store.LightweightSession())
        {
            foreach (var id in ids) session.Store(new Receipt { Id = id, Vendor = "Chandlery" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();
        var logger = new CountingLogger();
        query.Logger = logger;

        IDocumentReadOperations contract = query;
        var loaded = await contract.LoadManyAsync<Receipt>(
            [ids[0], ids[1], ids[1], ids[2], ids[3], ids[4], Guid.NewGuid()],
            TestContext.Current.CancellationToken);

        loaded.Select(x => x.Id).OrderBy(x => x).ShouldBe(ids.OrderBy(x => x));
        logger.Statements.ShouldBe(1);
    }

    [Fact]
    public async Task a_string_load_many_is_one_statement_with_repeats_collapsed()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trawler { Id = "north", Name = "North Star" });
            session.Store(new Trawler { Id = "south", Name = "Southern Cross" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.QuerySession();
        var logger = new CountingLogger();
        query.Logger = logger;

        IDocumentReadOperations contract = query;
        var loaded = await contract.LoadManyAsync<Trawler>(
            ["north", "south", "north", "east"], TestContext.Current.CancellationToken);

        loaded.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["north", "south"]);
        logger.Statements.ShouldBe(1);
    }

    public class Trawler
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CountingLogger: IFisherSessionLogger
    {
        public int Statements { get; private set; }

        public void OnBeforeExecute(DbCommand command)
        {
        }

        public void LogSuccess(DbCommand command) => Statements++;

        public void LogFailure(DbCommand command, Exception ex) => Statements++;

        public void LogFailure(Exception ex, string message)
        {
        }

        public void RecordSavedChanges(IDocumentSession session, Services.IChangeSet commit)
        {
        }
    }
}

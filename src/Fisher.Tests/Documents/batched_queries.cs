using Fisher.Batching;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     The batched document reads, query plans, <c>CheckExistsAsync</c> and <c>ToSql</c> — fisher#37.
/// </summary>
/// <remarks>
///     <b>Read the batch's own doc comment before assuming this is a performance feature.</b> A batch
///     elsewhere collapses network round trips; SQLite is embedded and there are none to collapse. It
///     is carried so DCB and document code ports between the stores unchanged. What does still hold is
///     ordering: the reads run back to back on one connection with nothing interleaved.
/// </remarks>
public class batched_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("batching");
    private DocumentStore _store = null!;
    private readonly Guid _frodo = Guid.NewGuid();
    private readonly Guid _sam = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Angler>().SoftDeleted();
            o.Schema.For<Boat>();
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Angler { Id = _frodo, Name = "Frodo", Catches = 3 });
        session.Store(new Angler { Id = _sam, Name = "Sam", Catches = 9 });
        session.Store(new Boat { Id = "sea-fox", Name = "Sea Fox" });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IDocumentSession Session() => _store.LightweightSession();

    // ---- CheckExistsAsync ----

    [Fact]
    public async Task check_exists_for_present_and_absent()
    {
        await using var session = Session();

        (await session.CheckExistsAsync<Angler>(_frodo, Token)).ShouldBeTrue();
        (await session.CheckExistsAsync<Angler>(Guid.NewGuid(), Token)).ShouldBeFalse();
        (await session.CheckExistsAsync<Boat>("sea-fox", Token)).ShouldBeTrue();
        (await session.CheckExistsAsync<Boat>("nope", Token)).ShouldBeFalse();
    }

    /// <summary>
    ///     It runs through the LINQ path, so it carries the soft-delete filter without restating it —
    ///     the fourth caller that would otherwise have to remember all three implicit filters.
    /// </summary>
    [Fact]
    public async Task check_exists_does_not_see_a_soft_deleted_document()
    {
        await using (var session = Session())
        {
            session.Delete<Angler>(_frodo);
            await session.SaveChangesAsync(Token);
        }

        await using var check = Session();
        (await check.CheckExistsAsync<Angler>(_frodo, Token)).ShouldBeFalse();
        (await check.CheckExistsAsync<Angler>(_sam, Token)).ShouldBeTrue();
    }

    // ---- ToSql ----

    [Fact]
    public void to_sql_shows_the_filters_fisher_adds()
    {
        using var session = Session();

        var sql = session.ToSql(session.Query<Angler>().Where(x => x.Catches > 2));

        sql.ShouldContain("fi_doc_angler");
        sql.ShouldContain("json_extract");
        // The implicit soft-delete filter is there even though the caller never asked for it.
        sql.ShouldContain("is_deleted");
    }

    // ---- query plans ----

    [Fact]
    public async Task a_query_plan_runs_against_the_session()
    {
        await using var session = Session();

        (await session.QueryByPlanAsync(new BusyAnglers(), Token))
            .Select(x => x.Name).ShouldBe(["Sam"]);
    }

    // ---- the batch ----

    [Fact]
    public async Task a_batch_of_document_reads()
    {
        await using var session = Session();
        var batch = session.Events.CreateBatchQuery();

        var frodo = batch.Load<Angler>(_frodo);
        var missing = batch.Load<Angler>(Guid.NewGuid());
        var both = batch.LoadMany<Angler>(_frodo, _sam);
        var exists = batch.CheckExists<Angler>(_sam);
        var busy = batch.Query<Angler>(s => s.Query<Angler>().Where(x => x.Catches > 5));
        var plan = batch.QueryByPlan(new BusyAnglers());

        await batch.Execute(Token);

        (await frodo)!.Name.ShouldBe("Frodo");
        (await missing).ShouldBeNull();
        (await both).Count.ShouldBe(2);
        (await exists).ShouldBeTrue();
        (await busy).ShouldHaveSingleItem().Name.ShouldBe("Sam");
        (await plan).ShouldHaveSingleItem().Name.ShouldBe("Sam");
    }

    /// <summary>
    ///     A failing item neither stops the batch nor vanishes: every item runs, each task is completed
    ///     or faulted, and <c>Execute</c> throws for what failed.
    /// </summary>
    /// <remarks>
    ///     Both halves matter. Stopping at the first failure would leave later items' tasks
    ///     uncompleted, so a caller awaiting one would hang rather than see an error. Faulting only the
    ///     item's task would let a caller who never awaits that particular item conclude the batch
    ///     succeeded.
    /// </remarks>
    [Fact]
    public async Task a_failing_batch_item_faults_its_task_and_surfaces_from_execute()
    {
        await using var session = Session();
        var batch = session.Events.CreateBatchQuery();

        var bad = batch.Query<Angler>(s => s.Query<Angler>().Where(x => x.Name.Length.ToString() == "x"));
        var declaredAfterTheFailure = batch.Load<Angler>(_frodo);

        await Should.ThrowAsync<BadLinqExpressionException>(() => batch.Execute(Token));

        await Should.ThrowAsync<BadLinqExpressionException>(() => bad);
        (await declaredAfterTheFailure)!.Name.ShouldBe("Frodo");
    }

    [Fact]
    public async Task an_empty_batch_executes()
    {
        await using var session = Session();

        await session.Events.CreateBatchQuery().Execute(Token);
    }

    private sealed class BusyAnglers : QueryListPlan<Angler>
    {
        public override IQueryable<Angler> Query(IQuerySession session)
            => session.Query<Angler>().Where(x => x.Catches > 5).OrderBy(x => x.Name);
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Catches { get; set; }
    }

    public class Boat
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}

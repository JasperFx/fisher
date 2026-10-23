// Deliberately NOT importing the Fisher.Linq namespace, for the reason stream_state_queries gives: the
// shared JasperFx.Events.Documents terminators are the ones this queryable executes through.
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Events;

/// <summary>
///     Fisher's own pins on <c>IReadOnlyEventStore.QueryEventData&lt;T&gt;</c> (jasperfx#1073) — the
///     REFUSAL half of the contract, which the shared <c>EventDataQueryCompliance</c> suite does not
///     cover, plus the two Fisher-specific answers it cannot see: the order with no <c>OrderBy</c>, and
///     archived streams.
/// </summary>
/// <remarks>
///     Every refusal test seeds data first, so it would FAIL against a silently-match-all provider even
///     with the throw assertion removed — the <c>stream_state_queries</c> discipline.
/// </remarks>
public class event_data_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event_data_queries");
    private DocumentStore _store = null!;
    private Guid _first;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        _first = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(_first, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
        session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Guard the shire"));
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IReadOnlyEventStore Reader => ((IEventStore)_store).OpenReadOnlyEventStore();

    private IQueryable<QuestStarted> Starts => Reader.QueryEventData<QuestStarted>();

    [Fact]
    public async Task with_no_ordering_the_bodies_come_back_in_append_order()
    {
        (await Starts.ToListAsync(Token)).Select(x => x.Name).ShouldBe(["Find the ring", "Guard the shire"]);
    }

    /// <summary>
    ///     A predicate the provider cannot translate fails the QUERY, with rows demonstrably present.
    /// </summary>
    [Fact]
    public async Task an_untranslatable_predicate_fails_the_query_not_matches_all()
    {
        (await Starts.CountAsync(Token)).ShouldBe(2);

        await Should.ThrowAsync<BadLinqExpressionException>(
            () => Starts.Where(x => x.GetHashCode() == 42).ToListAsync(Token));
    }

    [Fact]
    public async Task an_unsupported_operator_is_refused_by_name()
    {
        var exception = await Should.ThrowAsync<BadLinqExpressionException>(
            () => Starts.Distinct().ToListAsync(Token));

        exception.Message.ShouldContain(nameof(Queryable.Distinct));
    }

    [Fact]
    public void a_projection_is_refused_when_composed()
    {
        Should.Throw<BadLinqExpressionException>(() => Starts.Select(x => x.Name))
            .Message.ShouldContain(nameof(QuestStarted));
    }

    /// <summary>
    ///     The tenant half of the contract, as <c>QueryStreamStates</c> has it: this store has no tenant
    ///     dimension, so a tenant-scoped read is refused naming the tenant rather than answered unscoped.
    /// </summary>
    [Fact]
    public void a_tenant_scope_on_a_tenantless_store_is_refused_not_unscoped()
    {
        var exception = Should.Throw<NotSupportedException>(() => Reader.QueryEventData<QuestStarted>("acme"));

        exception.Message.ShouldContain("acme");
        exception.Message.ShouldContain("Conjoined");
    }

    /// <summary>
    ///     Every event stored as the type, archived or not — archiving takes a stream out of the live
    ///     reads, not out of the store, and <c>QueryEventDataAsync</c> answers the same way.
    /// </summary>
    [Fact]
    public async Task an_archived_stream_s_events_are_still_returned()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(_first);
            await session.SaveChangesAsync(Token);
        }

        (await Starts.Where(x => x.Name == "Find the ring").CountAsync(Token)).ShouldBe(1);
    }
}

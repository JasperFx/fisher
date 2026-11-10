#if NET11_0_OR_GREATER
using JasperFx;
using JasperFx.Events;
using Fisher.Events;

namespace Fisher.Tests.Unions;

public record LedgerOpened(string Owner);
public record FundsDeposited(decimal Amount);
public record FundsWithdrawn(decimal Amount, string Reason);

public union LedgerEvent(FundsDeposited, FundsWithdrawn);

/// <summary>An aggregate folding the union through one Apply, which the source generator splits per case.</summary>
public partial class Ledger
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public int Movements { get; set; }

    public static Ledger Create(LedgerOpened opened) => new() { Owner = opened.Owner };

    public void Apply(LedgerEvent movement)
    {
        Movements++;
        Balance += movement switch
        {
            FundsDeposited d => d.Amount,
            FundsWithdrawn w => -w.Amount,
            _ => 0
        };
    }
}

/// <summary>
///     fisher#466 — a C# 15 union appended as an event is stored as its active case. Fisher's
///     <c>EventGraph.BuildEvent</c> does not end in JasperFx's base, so it has to unwrap for itself
///     (jasperfx#1166's note for downstream stores).
/// </summary>
public class union_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("union_events");
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<Ledger>(JasperFx.Events.Projections.SnapshotLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> StartAsync()
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        LedgerEvent deposit = new FundsDeposited(100m);
        LedgerEvent withdrawal = new FundsWithdrawn(30m, "rent");
        session.Events.StartStream<Ledger>(id, new LedgerOpened("Ann"), deposit, withdrawal);
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Fact]
    public async Task a_union_is_stored_as_its_active_case()
    {
        var id = await StartAsync();

        await using var session = _store.QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: Token);

        events.Select(x => x.Data.GetType()).ShouldBe([typeof(LedgerOpened), typeof(FundsDeposited), typeof(FundsWithdrawn)]);
        events[1].EventTypeName.ShouldBe(_store.Options.EventGraph.EventMappingFor(typeof(FundsDeposited)).EventTypeName);
        events[2].Data.ShouldBe(new FundsWithdrawn(30m, "rent"));
    }

    [Fact]
    public async Task an_apply_over_the_union_folds_live_and_inline()
    {
        var id = await StartAsync();

        await using var session = _store.QuerySession();
        var live = await session.Events.AggregateStreamAsync<Ledger>(id, token: Token);
        live!.Balance.ShouldBe(70m);
        live.Movements.ShouldBe(2);

        var inline = await session.LoadAsync<Ledger>(id, Token);
        inline!.Balance.ShouldBe(70m);
        inline.Owner.ShouldBe("Ann");
    }

    [Fact]
    public async Task appending_to_an_existing_stream_unwraps_too()
    {
        var id = await StartAsync();

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(id, (LedgerEvent)new FundsDeposited(5m));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        var events = await query.Events.FetchStreamAsync(id, token: Token);
        events[^1].Data.ShouldBe(new FundsDeposited(5m));
    }

    [Fact]
    public void a_default_union_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => _store.Options.EventGraph.BuildEvent(default(LedgerEvent)));
    }

    [Fact]
    public void registering_the_union_registers_its_cases()
    {
        var graph = _store.Options.EventGraph;
        graph.AddEventType(typeof(LedgerEvent));

        var known = graph.AllKnownEventTypes().Select(x => x.EventType).ToList();
        known.ShouldContain(typeof(FundsDeposited));
        known.ShouldContain(typeof(FundsWithdrawn));
        known.ShouldNotContain(typeof(LedgerEvent));
    }

    [Fact]
    public async Task a_case_is_still_queryable_as_an_event_body()
    {
        await StartAsync();

        await using var session = _store.QuerySession();
        var found = await session.Events.QueryEventDataAsync<FundsWithdrawn>(x => x.Reason == "rent", Token);
        found.Count.ShouldBeGreaterThan(0);
    }
}
#endif

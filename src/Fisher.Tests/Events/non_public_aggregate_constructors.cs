using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     jasperfx#1188 (JasperFx 3.3.0): the evolver generator calls a private, protected or internal
///     parameterless constructor through <c>[UnsafeAccessor]</c>, so its body and every field initializer
///     run. Before that it built such an aggregate with <c>RuntimeHelpers.GetUninitializedObject</c>, which
///     runs neither — an initialized collection came back null and the first <c>Apply</c> threw.
/// </summary>
/// <remarks>
///     Fisher owns none of this; the generator does. It is pinned here because every Fisher path that
///     folds a stream — live aggregation and <c>FetchForWriting</c> — goes through that constructor, and an
///     aggregate with no <c>Create</c> method has no other way to come into existence.
/// </remarks>
public class non_public_aggregate_constructors : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("private_ctor");
    private DocumentStore _store = null!;
    private readonly Guid _streamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await using var session = _store.LightweightSession();
        session.Events.StartStream<GuardedLedger>(_streamId,
            new LedgerEntryPosted("first"),
            new LedgerEntryPosted("second"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task live_aggregation_runs_a_private_constructor_and_its_initializers()
    {
        await using var session = _store.LightweightSession();

        var ledger = await session.Events.AggregateStreamAsync<GuardedLedger>(_streamId,
            token: TestContext.Current.CancellationToken);

        ledger.ShouldNotBeNull();
        ledger.OpenedBy.ShouldBe(GuardedLedger.ConstructorRan);
        ledger.Entries.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task fetch_for_writing_runs_a_private_constructor_and_its_initializers()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<GuardedLedger>(_streamId,
            TestContext.Current.CancellationToken);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.OpenedBy.ShouldBe(GuardedLedger.ConstructorRan);
        stream.Aggregate.Entries.ShouldBe(["first", "second"]);
    }
}

public record LedgerEntryPosted(string Entry);

/// <summary>
///     No <c>Create</c> method and no public constructor, so the evolver has exactly one way to build it.
/// </summary>
public class GuardedLedger
{
    public const string ConstructorRan = "the private constructor ran";

    private GuardedLedger()
    {
        OpenedBy = ConstructorRan;
    }

    public Guid Id { get; set; }

    public string OpenedBy { get; private set; }

    // A field initializer: GetUninitializedObject left this null, and Apply threw on it.
    public List<string> Entries { get; } = new();

    public void Apply(LedgerEntryPosted posted) => Entries.Add(posted.Entry);
}

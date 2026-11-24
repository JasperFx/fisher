using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#692 — a natural key of the store's stream identity type is refused when the store is built, with the
///     same wording as Marten 10 and Polecat 6, because a fetch by it cannot be told apart from a fetch by stream id.
/// </summary>
public class natural_keys_of_the_stream_identity_type
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore StoreFor(TemporaryDatabase database, StreamIdentity identity,
        Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = identity;
            configure(options);
        });

    [Fact]
    public void a_bare_string_key_on_a_string_identity_store_is_refused()
    {
        using var database = TemporaryDatabase.Create("nk-692-string");

        var ex = Should.Throw<InvalidProjectionException>(() => StoreFor(database, StreamIdentity.AsString,
            options => options.Projections.Snapshot<StringKeyedLedger>(SnapshotLifecycle.Inline)));

        ex.Message.ShouldContain(
            "Natural key Fisher.Tests.Events.StringKeyedLedger.Code has type string, the same type as this store's "
            + "stream identity (AsString), so fetching by it would fetch by stream identity instead. Wrap it in a "
            + "strong-typed identifier, such as a record struct with a single Value property.");
    }

    // A Guid key on a Guid store never reaches this check: JasperFx refuses every Guid natural key ("A natural key must
    // be an int, long or string, or a strong-typed identifier wrapping one"), so only the string case is Fisher's.

    [Fact]
    public async Task a_bare_string_key_on_a_guid_identity_store_is_a_natural_key()
    {
        using var database = TemporaryDatabase.Create("nk-692-string-on-guid");
        await using var store = StoreFor(database, StreamIdentity.AsGuid,
            options => options.Projections.Snapshot<GuidStreamStringKeyedLedger>(SnapshotLifecycle.Inline));

        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<GuidStreamStringKeyedLedger>(id, new LedgerCoded("L-1"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession();
        (await query.Events.FetchLatest<GuidStreamStringKeyedLedger, string>("L-1", Token))!.Id.ShouldBe(id);
    }

    /// <summary>The fix the message asks for: a strong-typed key on a string-identity store.</summary>
    [Fact]
    public async Task a_strong_typed_key_on_a_string_identity_store_fetches_by_the_key()
    {
        using var database = TemporaryDatabase.Create("nk-692-wrapped");
        await using var store = StoreFor(database, StreamIdentity.AsString,
            options => options.Projections.Snapshot<WrappedKeyedLedger>(SnapshotLifecycle.Inline));

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<WrappedKeyedLedger>("ledger-1", new LedgerCoded("L-1"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession();
        (await query.Events.FetchLatest<WrappedKeyedLedger, LedgerCode>(new LedgerCode("L-1"), Token))!
            .Id.ShouldBe("ledger-1");

        // And a fetch by string is a fetch by stream key, with nothing ambiguous about it
        (await query.Events.FetchLatest<WrappedKeyedLedger, string>("ledger-1", Token))!.Code.ShouldBe(new LedgerCode("L-1"));
    }
}

public record LedgerCoded(string Code);

public readonly record struct LedgerCode(string Value);

public class StringKeyedLedger
{
    public string Id { get; set; } = "";

    [NaturalKey] public string Code { get; set; } = "";

    [NaturalKeySource]
    public static StringKeyedLedger Create(LedgerCoded e) => new() { Code = e.Code };
}

public class GuidStreamStringKeyedLedger
{
    public Guid Id { get; set; }

    [NaturalKey] public string Code { get; set; } = "";

    [NaturalKeySource]
    public static GuidStreamStringKeyedLedger Create(LedgerCoded e) => new() { Code = e.Code };
}

public class WrappedKeyedLedger
{
    public string Id { get; set; } = "";

    [NaturalKey] public LedgerCode Code { get; set; }

    [NaturalKeySource]
    public static WrappedKeyedLedger Create(LedgerCoded e) => new() { Code = new LedgerCode(e.Code) };
}

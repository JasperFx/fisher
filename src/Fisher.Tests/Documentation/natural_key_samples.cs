using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/natural-keys.md's "A null from a key source" section (fisher#684,
 * jasperfx#1287). The behaviour is pinned by NaturalKeyCompliance's a_null_from_an_inferred_key_source_keeps_the_key
 * and a_null_from_an_explicit_key_source_removes_the_key, and by natural_keys.a_replay_does_not_restore_a_key_an_explicit_source_removed.
 */

#region sample_natural_key_sources_and_null

public record ShipmentBooked(string Code);

public record ShipmentUpdated(string? NewCode, string Notes);

public record ShipmentCodeWithdrawn;

public partial class Shipment
{
    public Guid Id { get; set; }

    [NaturalKey]
    public string? Code { get; set; }

    public string Notes { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(ShipmentBooked e) => Code = e.Code;

    // Inferred: the key is read off the event, so a null NewCode means "this event carries no key"
    // and the stream keeps its code
    [NaturalKeySource]
    public void Apply(ShipmentUpdated e)
    {
        if (e.NewCode != null) Code = e.NewCode;
        Notes = e.Notes;
    }

    // Explicit: a static method returning the key type, so null removes the stream's key and frees it
    [NaturalKeySource]
    public static string? KeyFor(ShipmentCodeWithdrawn e) => null;

    public void Apply(ShipmentCodeWithdrawn e) => Code = null;
}

#endregion

/*
 * The compiled source behind the rest of docs/events/natural-keys.md (fisher#679, fisher#688, fisher#689). Run as a
 * test as well as compiled, so the samples are known to do what the page says.
 */

#region sample_natural_key_declaration

// A strong-typed key: one public property, and a constructor taking it
public readonly record struct BookingReference(string Value);

public record BookingMade(BookingReference Number, decimal Amount);

public record BookingPaid(decimal Amount);

public partial class Booking
{
    public Guid Id { get; set; }

    // The owned key: Booking's inline projection keeps the lookup from BookingReference to the stream
    [NaturalKey]
    public BookingReference Number { get; set; }

    public decimal Balance { get; set; }

    [NaturalKeySource]
    public static Booking Create(BookingMade e) => new() { Number = e.Number, Balance = e.Amount };

    public void Apply(BookingPaid e) => Balance -= e.Amount;
}

#endregion

#region sample_mapped_natural_key

// An async read model of the same stream. It owns no lookup: the key resolves through Booking's
public partial class BookingSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Booking>]
    public BookingReference Number { get; set; }

    public int Payments { get; set; }

    public static BookingSummary Create(BookingMade e) => new() { Number = e.Number };

    public void Apply(BookingPaid _) => Payments++;
}

#endregion

public class natural_key_usage_samples : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("nksamples");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        #region sample_registering_natural_keys

        _store = DocumentStore.For(opts =>
        {
            opts.ConnectionString = _database.ConnectionString;

            // The owner of the key has to be registered, and Inline keeps its lookup current
            // as the events commit
            opts.Projections.Snapshot<Booking>(SnapshotLifecycle.Inline);

            // The mapped read model can be any lifecycle
            opts.Projections.Snapshot<BookingSummary>(SnapshotLifecycle.Async);
        });

        #endregion

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task the_samples_do_what_the_page_says()
    {
        var number = new BookingReference($"BK-{Guid.NewGuid():N}");
        var other = new BookingReference($"BK-{Guid.NewGuid():N}");

        await using (var setup = _store.LightweightSession())
        {
            setup.Events.StartStream<Booking>(new BookingMade(number, 100m));
            setup.Events.StartStream<Booking>(new BookingMade(other, 50m));
            await setup.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();

        #region sample_fetching_by_natural_key

        // Read the current state, or null when no live stream has the key
        var booking = await session.Events.FetchLatest<Booking, BookingReference>(number);

        // Fetch for writing, with the optimistic version guard at SaveChangesAsync
        var stream = await session.Events.FetchForWriting<Booking, BookingReference>(number);
        stream.AppendOne(new BookingPaid(25m));
        await session.SaveChangesAsync();

        #endregion

        booking!.Balance.ShouldBe(100m);

        #region sample_fetching_by_natural_key_with_a_version

        // The command carries the version the user saw. A stale one throws
        // EventStreamUnexpectedMaxEventIdException, a ConcurrencyException, before
        // anything is enlisted in the session
        var checkedStream = await session.Events.FetchForWriting<Booking, BookingReference>(number, expectedVersion: 2);
        checkedStream.AppendOne(new BookingPaid(25m));
        await session.SaveChangesAsync();

        #endregion

        #region sample_fetching_a_mapped_natural_key

        // BookingSummary is async and may not be caught up yet. FetchLatest folds the stream
        // the key names, so the answer is current anyway
        var summary = await session.Events.FetchLatest<BookingSummary, BookingReference>(number);

        #endregion

        summary!.Payments.ShouldBe(2);

        #region sample_fetch_many_by_natural_key

        // Three statements however many keys: one to resolve them, two to fetch the streams.
        // The handles come back in the order of the keys
        var streams = await session.Events.FetchManyForWriting<Booking, BookingReference>([number, other]);
        foreach (var each in streams)
        {
            each.AppendOne(new BookingPaid(1m));
        }

        await session.SaveChangesAsync();

        #endregion

        #region sample_batching_natural_key_fetches

        var batch = session.CreateBatchQuery();

        var writing = batch.Events.FetchForWriting<Booking, BookingReference>(number);
        var view = batch.Events.FetchLatest<BookingSummary, BookingReference>(other);

        await batch.Execute();

        var bookingStream = await writing;
        var otherSummary = await view;

        #endregion

        bookingStream.Aggregate!.Balance.ShouldBe(49m);
        otherSummary!.Payments.ShouldBe(1);

        var reference = new BookingReference($"BK-{Guid.NewGuid():N}");

        #region sample_fetching_an_unknown_natural_key

        // No stream has this booking reference yet, so this is a new stream at version 0
        var fresh = await session.Events.FetchForWriting<Booking, BookingReference>(reference);

        // Appending the event that sets the key registers it when the session commits
        fresh.AppendOne(new BookingMade(reference, 100m));
        await session.SaveChangesAsync();

        #endregion

        fresh.StartingVersion.ShouldBe(0);
        (await session.Events.FetchLatest<Booking, BookingReference>(reference))!.Id.ShouldBe(fresh.Id);

        await using var exclusive = _store.LightweightSession();

        #region sample_exclusive_writing_by_natural_key

        // Takes the database's write lock, resolves the key inside it, and holds the lock until
        // SaveChangesAsync commits or the session is disposed. A competing exclusive writer waits
        // its turn rather than failing
        var locked = await exclusive.Events.FetchForExclusiveWriting<Booking, BookingReference>(number);
        locked.AppendOne(new BookingPaid(9m));
        await exclusive.SaveChangesAsync();

        #endregion

        (await exclusive.Events.FetchLatest<Booking, BookingReference>(number))!.Balance.ShouldBe(40m);
    }
}

using Fisher.Exceptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     Natural keys at parity with Marten 10 (fisher#679, fisher#688): the versioned fetch, nullable key types,
///     <c>FetchManyForWriting</c> by key, mapped keys and batched fetches. The shared suites
///     (<c>NaturalKeyCompliance</c>, <c>NaturalKeyRegistrationCompliance</c>, <c>MappedNaturalKeyCompliance</c>)
///     cover the cross-store contract; this covers what they leave to each store.
/// </summary>
public class natural_key_parity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("nkparity");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            o.Projections.Snapshot<Ticket>(SnapshotLifecycle.Inline);
            o.Projections.Snapshot<TicketBoard>(SnapshotLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static TicketNumber ANumber() => new($"T-{Guid.NewGuid():N}");

    private async Task<Guid> OpenTicket(TicketNumber number, int notes = 0)
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Ticket>(id,
            new object[] { new TicketOpened(number, "Printer on fire") }
                .Concat(Enumerable.Range(0, notes).Select(i => new TicketNoted($"note {i}"))).ToArray());
        await session.SaveChangesAsync(Token);
        return id;
    }

    // ---- fisher#688: FetchForWriting(key, expectedVersion) ----

    [Fact]
    public async Task fetching_by_key_at_the_current_version_writes()
    {
        var number = ANumber();
        var id = await OpenTicket(number, notes: 1);

        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<Ticket, TicketNumber>(number, 2, Token);

        stream.Id.ShouldBe(id);
        stream.StartingVersion.ShouldBe(2);
        stream.AppendOne(new TicketNoted("checked"));
        await session.SaveChangesAsync(Token);

        (await session.Events.FetchLatest<Ticket, TicketNumber>(number, Token))!.Notes.ShouldBe(2);
    }

    [Fact]
    public async Task a_stale_version_by_key_is_a_concurrency_exception_and_enlists_nothing()
    {
        var number = ANumber();
        await OpenTicket(number, notes: 2);

        await using var session = _store.LightweightSession();

        var stale = await Should.ThrowAsync<ConcurrencyException>(
            () => session.Events.FetchForWriting<Ticket, TicketNumber>(number, 1, Token));

        stale.ShouldBeOfType<EventStreamUnexpectedMaxEventIdException>();

        // Checked before the session is touched, so there is nothing to commit by mistake
        session.Events.PendingStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_versioned_generic_overload_checks_a_stream_id_too()
    {
        var id = await OpenTicket(ANumber());

        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => session.Events.FetchForWriting<Ticket, Guid>(id, 5, Token));

        (await session.Events.FetchForWriting<Ticket, Guid>(id, 1, Token)).Id.ShouldBe(id);
    }

    // ---- fisher#694: an unknown key starts a new stream, as on Marten 10 and Polecat 6 ----

    [Fact]
    public async Task an_unknown_key_starts_a_new_stream_and_registers_the_key_on_commit()
    {
        var number = ANumber();

        Guid id;
        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);

            stream.Aggregate.ShouldBeNull();
            stream.CurrentVersion.ShouldBe(0);
            stream.Id.ShouldNotBe(Guid.Empty);
            id = stream.Id;

            stream.AppendOne(new TicketOpened(number, "Paper jam"));
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        var again = await check.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);
        again.Id.ShouldBe(id);
        again.Aggregate!.Title.ShouldBe("Paper jam");
    }

    [Fact]
    public async Task each_fetch_of_an_unknown_key_starts_its_own_stream()
    {
        var number = ANumber();

        await using var session = _store.LightweightSession();
        var first = await session.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);
        var second = await session.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);

        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task an_unknown_key_at_version_zero_starts_a_new_stream()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<Ticket, TicketNumber>(ANumber(), 0, Token);

        stream.CurrentVersion.ShouldBe(0);
        stream.Aggregate.ShouldBeNull();
    }

    [Fact]
    public async Task an_unknown_key_at_any_other_version_is_a_concurrency_exception_and_enlists_nothing()
    {
        var number = ANumber();
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => session.Events.FetchForWriting<Ticket, TicketNumber>(number, 1, Token));

        ex.ShouldBeAssignableTo<ConcurrencyException>();
        ex.Message.ShouldContain(number.Value);
        session.Events.PendingStreams.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unknown_key_for_exclusive_writing_starts_a_new_stream()
    {
        var number = ANumber();

        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForExclusiveWriting<Ticket, TicketNumber>(number, Token);
            stream.CurrentVersion.ShouldBe(0);
            stream.AppendOne(new TicketOpened(number, "Toner"));
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        (await check.Events.FetchLatest<Ticket, TicketNumber>(number, Token))!.Title.ShouldBe("Toner");
    }

    /// <summary>A mapped key resolves through its owner's lookup, so an unknown one is a new stream too.</summary>
    [Fact]
    public async Task an_unknown_mapped_key_starts_a_new_stream()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<TicketBoard, TicketNumber>(ANumber(), Token);

        stream.CurrentVersion.ShouldBe(0);
        stream.Aggregate.ShouldBeNull();
    }

    [Fact]
    public async Task two_sessions_starting_one_unknown_key_cannot_both_have_it()
    {
        var number = ANumber();

        await using var first = _store.LightweightSession();
        await using var second = _store.LightweightSession();

        var a = await first.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);
        var b = await second.Events.FetchForWriting<Ticket, TicketNumber>(number, Token);
        a.AppendOne(new TicketOpened(number, "first"));
        b.AppendOne(new TicketOpened(number, "second"));

        await first.SaveChangesAsync(Token);
        await Should.ThrowAsync<DuplicateNaturalKeyException>(() => second.SaveChangesAsync(Token));

        await using var check = _store.LightweightSession();
        (await check.Events.FetchLatest<Ticket, TicketNumber>(number, Token))!.Title.ShouldBe("first");
    }

    // ---- fisher#679: nullable key types and null keys ----

#pragma warning disable CS8714 // a Nullable<T> type argument is exactly what is being refused
    [Fact]
    public async Task a_nullable_key_type_argument_is_refused()
    {
        var number = ANumber();
        await OpenTicket(number);

        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchForWriting<Ticket, TicketNumber?>(number, Token));
        await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchLatest<Ticket, TicketNumber?>(number, Token).AsTask());
        await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<Ticket, TicketNumber?>([number], Token));
    }
#pragma warning restore CS8714

    [Fact]
    public async Task a_null_key_is_null_from_fetch_latest_and_refused_for_writing()
    {
        await using var session = _store.LightweightSession();

        // default(TicketNumber) wraps a null string
        (await session.Events.FetchLatest<Ticket, TicketNumber>(default, Token)).ShouldBeNull();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => session.Events.FetchForWriting<Ticket, TicketNumber>(default, Token));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => session.Events.FetchForExclusiveWriting<Ticket, TicketNumber>(default, Token));
    }

    // ---- fisher#679: FetchManyForWriting by key ----

    [Fact]
    public async Task fetch_many_for_writing_by_key_hands_back_each_stream_in_order()
    {
        var first = ANumber();
        var second = ANumber();
        var firstId = await OpenTicket(first, notes: 1);
        var secondId = await OpenTicket(second);

        await using var session = _store.LightweightSession();
        var streams = await session.Events.FetchManyForWriting<Ticket, TicketNumber>([second, first], Token);

        streams.Select(x => x.Id).ShouldBe([secondId, firstId]);
        streams[1].Aggregate!.Notes.ShouldBe(1);

        foreach (var stream in streams)
        {
            stream.AppendOne(new TicketNoted("bulk"));
        }

        await session.SaveChangesAsync(Token);

        (await session.Events.FetchLatest<Ticket, TicketNumber>(first, Token))!.Notes.ShouldBe(2);
        (await session.Events.FetchLatest<Ticket, TicketNumber>(second, Token))!.Notes.ShouldBe(1);
    }

    /// <summary>fisher#694: an unknown key gets a new stream at version 0 in its place, in key order.</summary>
    [Fact]
    public async Task fetch_many_for_writing_gives_an_unknown_key_a_new_stream_in_its_place()
    {
        var known = ANumber();
        var knownId = await OpenTicket(known);
        var unknown = ANumber();

        await using var session = _store.LightweightSession();

        var streams = await session.Events.FetchManyForWriting<Ticket, TicketNumber>([unknown, known], Token);

        streams.Count.ShouldBe(2);
        streams[0].CurrentVersion.ShouldBe(0);
        streams[0].Aggregate.ShouldBeNull();
        streams[0].Id.ShouldNotBe(knownId);
        streams[1].Id.ShouldBe(knownId);

        streams[0].AppendOne(new TicketOpened(unknown, "new"));
        await session.SaveChangesAsync(Token);

        (await session.Events.FetchLatest<Ticket, TicketNumber>(unknown, Token))!.Id.ShouldBe(streams[0].Id);
    }

    [Fact]
    public async Task fetch_many_for_writing_refuses_a_repeated_key()
    {
        var number = ANumber();
        await OpenTicket(number);

        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<Ticket, TicketNumber>([number, number], Token));
    }

    // ---- fisher#679: mapped keys ----

    [Fact]
    public async Task an_async_view_is_written_by_the_key_its_source_owns()
    {
        var number = ANumber();
        var id = await OpenTicket(number, notes: 1);

        await using (var session = _store.LightweightSession())
        {
            // TicketBoard is async and owns no lookup: the key resolves through Ticket's
            var stream = await session.Events.FetchForWriting<TicketBoard, TicketNumber>(number, 2, Token);

            stream.Id.ShouldBe(id);
            stream.Aggregate!.Notes.ShouldBe(1);
            stream.AppendOne(new TicketNoted("from the board"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        (await query.Events.FetchLatest<TicketBoard, TicketNumber>(number, Token))!.Notes.ShouldBe(2);
    }

    // ---- fisher#679: batched ----

    [Fact]
    public async Task a_batch_fetches_by_key()
    {
        var number = ANumber();
        var id = await OpenTicket(number, notes: 1);

        await using var session = _store.LightweightSession();
        var batch = session.CreateBatchQuery();

        var writing = batch.Events.FetchForWriting<Ticket, TicketNumber>(number, 2);
        var board = batch.Events.FetchLatest<TicketBoard, TicketNumber>(number);
        var missing = batch.Events.FetchLatest<Ticket, TicketNumber>(ANumber());

        await batch.Execute(Token);

        (await writing).Id.ShouldBe(id);
        (await board)!.Notes.ShouldBe(1);
        (await missing).ShouldBeNull();
    }

    [Fact]
    public async Task a_batch_fetch_for_writing_by_an_unknown_key_starts_a_new_stream()
    {
        await using var session = _store.LightweightSession();
        var batch = session.CreateBatchQuery();

        var writing = batch.Events.FetchForWriting<Ticket, TicketNumber>(ANumber());
        var versioned = batch.Events.FetchForWriting<Ticket, TicketNumber>(ANumber(), 0);

        await batch.Execute(Token);

        (await writing).CurrentVersion.ShouldBe(0);
        (await versioned).Aggregate.ShouldBeNull();
    }

    [Fact]
    public async Task a_stale_version_in_a_batch_faults_its_own_item()
    {
        var number = ANumber();
        await OpenTicket(number);

        await using var session = _store.LightweightSession();
        var batch = session.CreateBatchQuery();

        var stale = batch.Events.FetchForWriting<Ticket, TicketNumber>(number, 7);
        var fine = batch.Events.FetchLatest<Ticket, TicketNumber>(number);

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => batch.Execute(Token));

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => stale);
        (await fine).ShouldNotBeNull();
    }

    public readonly record struct TicketNumber(string Value);

    public record TicketOpened(TicketNumber Number, string Title);

    public record TicketNoted(string Note);

    public partial class Ticket
    {
        public Guid Id { get; set; }

        [NaturalKey]
        public TicketNumber Number { get; set; }

        public string Title { get; set; } = string.Empty;
        public int Notes { get; set; }

        [NaturalKeySource]
        public static Ticket Create(TicketOpened e) => new() { Number = e.Number, Title = e.Title };

        public void Apply(TicketNoted _) => Notes++;
    }

    public partial class TicketBoard
    {
        public Guid Id { get; set; }

        [MappedNaturalKey<Ticket>]
        public TicketNumber Number { get; set; }

        public int Notes { get; set; }

        public static TicketBoard Create(TicketOpened e) => new() { Number = e.Number };

        public void Apply(TicketNoted _) => Notes++;
    }
}

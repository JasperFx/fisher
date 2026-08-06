using Fisher.Tests.Schema;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record RingBearerNamed(string Name);

public record RingBearerRedacted(string Reason);

/// <summary>
///     Rewriting events that are already committed — the foundation under event data masking
///     (fisher#9) and stream compacting (fisher#10).
/// </summary>
public class rewriting_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("rewriting");
    private DocumentStore _store = null!;

    private readonly TerritoryId _shire = new(Guid.NewGuid());

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<TerritoryId>("territory");
            options.Events.AddEventType(typeof(RingBearerNamed));
            options.Events.AddEventType(typeof(RingBearerRedacted));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- OverwriteEvent ----

    [Fact]
    public async Task overwriting_replaces_the_stored_body()
    {
        var streamId = await StartStreamAsync(new RingBearerNamed("Frodo Baggins"));

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            var stored = (RingBearerNamed)events[0].Data;
            session.Events.OverwriteEvent(new Event<RingBearerNamed>(stored with { Name = "REDACTED" })
            {
                Sequence = events[0].Sequence
            });

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var reread = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        ((RingBearerNamed)reread[0].Data).Name.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     An overwrite rewrites what an event says, not what it is — so everything that places the row
    ///     in the stream and in the global order has to survive it.
    /// </summary>
    [Fact]
    public async Task overwriting_leaves_the_row_where_it_was()
    {
        var streamId = await StartStreamAsync(new RingBearerNamed("Bilbo"));

        var (seqBefore, typeBefore, versionBefore, timestampBefore) = await RowAsync(streamId);

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            session.Events.OverwriteEvent(new Event<RingBearerNamed>(new RingBearerNamed("Bilbo Baggins"))
            {
                Sequence = events[0].Sequence
            });

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (seqAfter, typeAfter, versionAfter, timestampAfter) = await RowAsync(streamId);

        seqAfter.ShouldBe(seqBefore);
        typeAfter.ShouldBe(typeBefore);
        versionAfter.ShouldBe(versionBefore);
        timestampAfter.ShouldBe(timestampBefore);
    }

    [Fact]
    public async Task overwriting_an_event_with_no_sequence_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = Should.Throw<ArgumentException>(() =>
            session.Events.OverwriteEvent(new Event<RingBearerNamed>(new RingBearerNamed("Sam"))));

        ex.Message.ShouldContain("no sequence");
    }

    // ---- CompletelyReplaceEvent ----

    [Fact]
    public async Task replacing_changes_the_body_and_the_type()
    {
        var streamId = await StartStreamAsync(new RingBearerNamed("Gollum"));

        long sequence;
        Guid newId;

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            sequence = events[0].Sequence;
            newId = session.Events.CompletelyReplaceEvent(sequence,
                new RingBearerRedacted("right to erasure"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var reread = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        var replaced = reread.ShouldHaveSingleItem();
        replaced.Data.ShouldBeOfType<RingBearerRedacted>().Reason.ShouldBe("right to erasure");
        replaced.Sequence.ShouldBe(sequence);
        replaced.Version.ShouldBe(1);
    }

    /// <summary>
    ///     The returned id is the one the row ends up carrying — it is handed back before the operation
    ///     runs, so nothing else would catch the two drifting apart.
    /// </summary>
    [Fact]
    public async Task replacing_returns_the_id_it_actually_wrote()
    {
        var streamId = await StartStreamAsync(new RingBearerNamed("Sméagol"));

        Guid returned;
        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            returned = session.Events.CompletelyReplaceEvent(events[0].Sequence,
                new RingBearerRedacted("erased"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var reread = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        reread[0].Id.ShouldBe(returned);
    }

    [Fact]
    public async Task replacing_at_a_sequence_that_cannot_exist_is_refused()
    {
        await using var session = _store.LightweightSession();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            session.Events.CompletelyReplaceEvent(0L, new RingBearerRedacted("nope")));
    }

    // ---- the unit of work ----

    /// <summary>
    ///     Both members queue rather than execute, which is what lets a masking batch rewrite many
    ///     events atomically. Abandoning the session has to leave the row alone.
    /// </summary>
    [Fact]
    public async Task a_rewrite_that_is_never_committed_changes_nothing()
    {
        var streamId = await StartStreamAsync(new RingBearerNamed("Frodo"));

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            session.Events.OverwriteEvent(new Event<RingBearerNamed>(new RingBearerNamed("REDACTED"))
            {
                Sequence = events[0].Sequence
            });

            // no SaveChangesAsync
        }

        await using var query = _store.LightweightSession();
        var reread = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        ((RingBearerNamed)reread[0].Data).Name.ShouldBe("Frodo");
    }

    // ---- DeleteEvents ----

    [Fact]
    public async Task deleting_removes_the_rows()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId,
                new RingBearerNamed("Frodo"), new RingBearerNamed("Sam"), new RingBearerNamed("Merry"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            session.Events.DeleteEvents(events.Take(2).Select(x => x.Sequence).ToArray());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var remaining = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        var survivor = remaining.ShouldHaveSingleItem();
        ((RingBearerNamed)survivor.Data).Name.ShouldBe("Merry");
    }

    /// <summary>
    ///     The tag tables carry a real foreign key to <c>fi_events(seq_id)</c> and Weasel's default
    ///     profile enforces it, so deleting the events before their tag rows fails with
    ///     <c>FOREIGN KEY constraint failed</c> — the same ordering fisher#6 had to learn. Reordering
    ///     the two deletes in <c>DeleteEventsOperation</c> fails this test with that exact message.
    /// </summary>
    [Fact]
    public async Task deleting_a_tagged_event_clears_its_tag_rows_first()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var @event = session.Events.BuildEvent(new RingBearerNamed("Frodo"));
            @event.WithTag(_shire);
            session.Events.StartStream(streamId, @event);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await TagRowCountAsync()).ShouldBe(1);

        await using (var session = _store.LightweightSession())
        {
            var events = await session.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken);

            session.Events.DeleteEvents(events.Select(x => x.Sequence).ToArray());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await TagRowCountAsync()).ShouldBe(0);

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    // ---- helpers ----

    private async Task<Guid> StartStreamAsync(object data)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, data);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<(long Seq, string Type, long Version, string Timestamp)> RowAsync(Guid streamId)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText =
            "select seq_id, type, version, timestamp from fi_events where stream_id = @id order by version";
        command.Parameters.AddWithValue("@id", streamId.ToString("D").ToLowerInvariant());

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        return (reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
    }

    private async Task<long> TagRowCountAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select count(*) from fi_event_tag_territory";

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

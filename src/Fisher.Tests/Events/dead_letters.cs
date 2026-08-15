using Fisher.Linq;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Daemon;

/// <summary>
///     The dead letter queue — fisher#5.
/// </summary>
/// <remarks>
///     A projection configured to skip a poison event quarantines it in <c>fi_dead_letters</c> and
///     keeps running. Before this existed the skip path threw, so the shard stopped instead and one
///     bad event in one stream halted the projection for every stream.
/// </remarks>
public class dead_letters : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("deadletters");
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

    private static ShardName Shard(string projection = "tally", string key = "All") => new(projection, key, 1);

    private static DeadLetterEvent LetterFor(ShardName shard, long sequence, string message = "boom",
        string? tenantId = StorageConstants.DefaultTenantId)
        => new()
        {
            Id = Guid.CreateVersion7(),
            ProjectionName = shard.Name,
            ShardName = shard.ShardKey,
            EventSequence = sequence,
            TenantId = tenantId,
            ExceptionType = "InvalidOperationException",
            ExceptionMessage = message,
            Timestamp = DateTimeOffset.UtcNow
        };

    private Task StoreAsync(DeadLetterEvent letter)
        => _store.Database.StoreDeadLetterEventAsync(null!, letter, TestContext.Current.CancellationToken);

    [Fact]
    public async Task a_stored_dead_letter_is_counted_and_read_back()
    {
        var shard = Shard();
        await StoreAsync(LetterFor(shard, 7, "the balrog ate it"));

        (await _store.Database.CountDeadLetterEventsAsync(shard, TestContext.Current.CancellationToken))
            .ShouldBe(1);

        var rows = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 0, 10,
            TestContext.Current.CancellationToken);

        var row = rows.ShouldHaveSingleItem();
        row.ProjectionName.ShouldBe("tally");
        row.ShardName.ShouldBe("All");
        row.EventSequence.ShouldBe(7);
        row.ExceptionType.ShouldBe("InvalidOperationException");
        row.ExceptionMessage.ShouldBe("the balrog ate it");
        row.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
    }

    /// <summary>
    ///     A Guid id survives the round trip.
    /// </summary>
    /// <remarks>
    ///     The recurring SQLite trap: a raw <see cref="Guid" /> binds as a 16-byte BLOB and the
    ///     provider's own string form is uppercase, either of which writes a row that can never be read
    ///     back under the case-sensitive default collation. Both fail by finding nothing rather than by
    ///     erroring, which is why this asserts the value rather than just the count.
    /// </remarks>
    [Fact]
    public async Task the_id_round_trips_as_lowercase_canonical_text()
    {
        var shard = Shard();
        var letter = LetterFor(shard, 1);
        await StoreAsync(letter);

        var rows = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 0, 10,
            TestContext.Current.CancellationToken);

        rows.ShouldHaveSingleItem().Id.ShouldBe(letter.Id);
    }

    /// <summary>
    ///     The write is an upsert, because the daemon retries it in the background.
    /// </summary>
    /// <remarks>
    ///     JasperFx assigns the id when the dead letter is constructed rather than letting the store
    ///     generate one, so a retry that lands after a successful first attempt carries the same
    ///     primary key. An <c>insert</c> would fail it.
    /// </remarks>
    [Fact]
    public async Task storing_the_same_dead_letter_twice_updates_rather_than_failing()
    {
        var shard = Shard();
        var letter = LetterFor(shard, 3, "first");

        await StoreAsync(letter);

        letter.ExceptionMessage = "second";
        await StoreAsync(letter);

        (await _store.Database.CountDeadLetterEventsAsync(shard, TestContext.Current.CancellationToken))
            .ShouldBe(1);

        var rows = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 0, 10,
            TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem().ExceptionMessage.ShouldBe("second");
    }

    [Fact]
    public async Task counts_are_scoped_to_one_shard()
    {
        await StoreAsync(LetterFor(Shard("tally", "All"), 1));
        await StoreAsync(LetterFor(Shard("tally", "All"), 2));
        await StoreAsync(LetterFor(Shard("tally", "Other"), 3));
        await StoreAsync(LetterFor(Shard("ledger", "All"), 4));

        (await _store.Database.CountDeadLetterEventsAsync(Shard("tally", "All"),
            TestContext.Current.CancellationToken)).ShouldBe(2);
        (await _store.Database.CountDeadLetterEventsAsync(Shard("tally", "Other"),
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await _store.Database.CountDeadLetterEventsAsync(Shard("ledger", "All"),
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task every_shards_counts_come_back_in_one_read()
    {
        await StoreAsync(LetterFor(Shard("tally", "All"), 1));
        await StoreAsync(LetterFor(Shard("tally", "All"), 2));
        await StoreAsync(LetterFor(Shard("ledger", "All"), 3));

        var counts = await _store.Database.FetchDeadLetterCountsAsync(TestContext.Current.CancellationToken);

        counts.Count.ShouldBe(2);
        counts.ShouldContain(x => x.ProjectionName == "tally" && x.ShardKey == "All" && x.Count == 2);
        counts.ShouldContain(x => x.ProjectionName == "ledger" && x.ShardKey == "All" && x.Count == 1);
    }

    /// <summary>
    ///     The same counts scoped to one tenant (fisher#77).
    /// </summary>
    /// <remarks>
    ///     Without the override this landed on JasperFx's default and threw <c>NotSupportedException</c>
    ///     for a non-null tenant, where the store-global overload beside it worked. A monitoring console
    ///     reads this to show per-tenant badges per shard.
    /// </remarks>
    [Fact]
    public async Task counts_can_be_scoped_to_one_tenant()
    {
        var shard = Shard();

        await StoreAsync(LetterFor(shard, 1, tenantId: "blue"));
        await StoreAsync(LetterFor(shard, 2, tenantId: "blue"));
        await StoreAsync(LetterFor(shard, 3, tenantId: "green"));

        var blue = await _store.Database.FetchDeadLetterCountsAsync("blue",
            TestContext.Current.CancellationToken);

        var row = blue.ShouldHaveSingleItem();
        row.ProjectionName.ShouldBe("tally");
        row.ShardKey.ShouldBe("All");
        row.Count.ShouldBe(2);

        // Stamped, so a consumer keying by {ProjectionName}:{ShardKey} no longer collapses two tenants
        // onto one badge — which is the whole reason the overload exists.
        row.TenantId.ShouldBe("blue");

        var green = await _store.Database.FetchDeadLetterCountsAsync("green",
            TestContext.Current.CancellationToken);

        green.ShouldHaveSingleItem().Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_tenant_with_no_dead_letters_counts_nothing()
    {
        await StoreAsync(LetterFor(Shard(), 1, tenantId: "blue"));

        (await _store.Database.FetchDeadLetterCountsAsync("green", TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     Storing a <see cref="DeadLetterEvent" /> as a document is refused by name (fisher#77).
    /// </summary>
    /// <remarks>
    ///     On Marten and Polecat a <c>DeadLetterEvent</c> is also an ordinary document, so
    ///     <c>session.Store(letter)</c> lands it in the very table the dead-letter query reads. Here it
    ///     is event-store infrastructure with its own table and its own write path — so before this the
    ///     same call compiled, succeeded, wrote a <c>fi_doc_deadletterevent</c> row, and the query never
    ///     saw it. Fisher's arrangement is the better one; the divergence is still worth failing over,
    ///     because it is silent in the direction that hurts.
    /// </remarks>
    [Fact]
    public async Task storing_a_dead_letter_as_a_document_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var message = Should.Throw<InvalidOperationException>(() => session.Store(LetterFor(Shard(), 1)))
            .Message;

        message.ShouldContain(nameof(DeadLetterEvent));
        message.ShouldContain("StoreDeadLetterEventAsync");
    }

    /// <remarks>
    ///     Reading is refused for the same reason and by the same guard — every path into document
    ///     storage resolves a mapping first, so a query that answered empty would be a different way of
    ///     saying nothing was ever recorded. It surfaces at the terminal rather than at
    ///     <c>Query&lt;T&gt;()</c>, because building the queryable resolves nothing.
    /// </remarks>
    [Fact]
    public async Task querying_dead_letters_as_documents_is_refused_too()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.Query<DeadLetterEvent>().ToListAsync(TestContext.Current.CancellationToken));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.LoadAsync<DeadLetterEvent>(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    /// <remarks>
    ///     A null tenant is store-global and must leave <c>TenantId</c> null rather than defaulting it:
    ///     a consumer has to be able to tell "every tenant" from "the default tenant". It also has to
    ///     count rows the daemon recorded with no tenant at all, which no tenant-scoped read can reach.
    /// </remarks>
    [Fact]
    public async Task a_null_tenant_is_store_global_and_stamps_nothing()
    {
        var shard = Shard();

        await StoreAsync(LetterFor(shard, 1, tenantId: "blue"));
        await StoreAsync(LetterFor(shard, 2, tenantId: "green"));
        await StoreAsync(LetterFor(shard, 3, tenantId: null));

        var all = await _store.Database.FetchDeadLetterCountsAsync(tenantId: null,
            TestContext.Current.CancellationToken);

        var row = all.ShouldHaveSingleItem();
        row.Count.ShouldBe(3);
        row.TenantId.ShouldBeNull();

        // ...and the tenant-less overload is the same answer, not a second implementation.
        var implicitly_global =
            await _store.Database.FetchDeadLetterCountsAsync(TestContext.Current.CancellationToken);

        implicitly_global.ShouldHaveSingleItem().Count.ShouldBe(3);
    }

    /// <summary>
    ///     The drill-in is newest first and pages.
    /// </summary>
    [Fact]
    public async Task rows_come_back_newest_first_and_page()
    {
        var shard = Shard();

        foreach (var sequence in new long[] { 1, 2, 3, 4, 5 })
        {
            await StoreAsync(LetterFor(shard, sequence));
        }

        var firstPage = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 0, 2,
            TestContext.Current.CancellationToken);
        firstPage.Select(x => x.EventSequence).ShouldBe([5, 4]);

        var secondPage = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 2, 2,
            TestContext.Current.CancellationToken);
        secondPage.Select(x => x.EventSequence).ShouldBe([3, 2]);
    }

    [Fact]
    public async Task a_tenant_filter_scopes_the_drill_in()
    {
        var shard = Shard();
        await StoreAsync(LetterFor(shard, 1, tenantId: "blue"));
        await StoreAsync(LetterFor(shard, 2, tenantId: "green"));

        var blue = await _store.Database.QueryDeadLetterEventsAsync(shard, "blue", 0, 10,
            TestContext.Current.CancellationToken);

        blue.ShouldHaveSingleItem().EventSequence.ShouldBe(1);

        // A null tenant spans every tenant sharing the database rather than meaning "the default one".
        var all = await _store.Database.QueryDeadLetterEventsAsync(shard, null, 0, 10,
            TestContext.Current.CancellationToken);
        all.Count.ShouldBe(2);
    }

    /// <summary>
    ///     A dead letter outlives the event it describes.
    /// </summary>
    /// <remarks>
    ///     Deliberate: <c>fi_dead_letters</c> carries no foreign key to <c>fi_events</c>, unlike the DCB
    ///     tag tables. A cascade would erase exactly the evidence an operator came looking for when the
    ///     offending stream is archived or compacted away. Nothing else removes them either, which is
    ///     why the cleaner does.
    /// </remarks>
    [Fact]
    public async Task a_dead_letter_survives_its_event_but_not_a_clean()
    {
        var shard = Shard();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("doomed"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Sequence 1 is the event just appended, so the dead letter genuinely points at a live row.
        await StoreAsync(LetterFor(shard, 1));

        // Deleting the event it names must not take the dead letter with it, and must not be rejected
        // by a foreign key. This is the delete a tag row would refuse — see fisher#6.
        await using (var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "delete from fi_events where seq_id = 1";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await _store.Database.CountDeadLetterEventsAsync(shard, TestContext.Current.CancellationToken))
            .ShouldBe(1);

        // Nothing else would ever remove it, so a "clean" store would keep reporting a sick projection.
        await _store.Advanced.Clean.DeleteAllEventDataAsync(TestContext.Current.CancellationToken);

        (await _store.Database.CountDeadLetterEventsAsync(shard, TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    /// <summary>
    ///     The whole path, not just the storage: a projection that throws on one event quarantines it
    ///     and the shard keeps going.
    /// </summary>
    /// <remarks>
    ///     This is what the milestone is actually for. Before <c>StoreDeadLetterEventAsync</c> existed
    ///     the skip path itself threw, so <c>SkipApplyErrors</c> could not be honoured and one bad event
    ///     stopped the projection for every stream — the events after the poison one were never applied.
    /// </remarks>
    [Fact]
    public async Task a_skipped_poison_event_is_quarantined_and_the_shard_carries_on()
    {
        await using var database = TemporaryDatabase.Create("poison");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
            options.Projections.Errors.SkipApplyErrors = true;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<PoisonTally>(streamId,
                new Counted(1), new Counted(PoisonTally.Poison), new Counted(1));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

            // The two good events were applied; the poison one in the middle did not stop them.
            await using var query = store.LightweightSession();
            var tally = await query.LoadAsync<PoisonTally>(streamId, TestContext.Current.CancellationToken);
            tally.ShouldNotBeNull();
            tally.Total.ShouldBe(2);

            var counts = await store.Database.FetchDeadLetterCountsAsync(TestContext.Current.CancellationToken);
            var poisoned = counts.ShouldHaveSingleItem();
            poisoned.ProjectionName.ShouldBe("PoisonTally");
            poisoned.Count.ShouldBe(1);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }
}

public record Counted(int Amount);

/// <summary>
///     A snapshot that throws on one specific event, so the daemon has a poison pill to skip.
/// </summary>
public class PoisonTally
{
    public const int Poison = -999;

    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(Counted counted)
    {
        if (counted.Amount == Poison)
        {
            throw new InvalidOperationException("This event cannot be applied.");
        }

        Total += counted.Amount;
    }
}

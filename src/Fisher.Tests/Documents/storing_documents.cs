using JasperFx;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Documents;

public class storing_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("documents");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Trail>();
            options.Schema.For<Marker>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task round_trips_a_document()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Pennine Way", Miles = 268 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var loaded = await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(id);
        loaded.Name.ShouldBe("Pennine Way");
        loaded.Miles.ShouldBe(268);
    }

    [Fact]
    public async Task assigns_an_identity_when_the_document_has_none()
    {
        var trail = new Trail { Name = "Unnamed" };

        await using var session = _store.LightweightSession();
        session.Store(trail);

        // The id is assigned at Store, not at commit, so the caller can use it straight away.
        trail.Id.ShouldNotBe(Guid.Empty);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await session.LoadAsync<Trail>(trail.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task storing_twice_updates_rather_than_duplicating()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "First", Miles = 1 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Second", Miles = 2 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var loaded = await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken);

        loaded!.Name.ShouldBe("Second");
        loaded.Miles.ShouldBe(2);
    }

    [Fact]
    public async Task mutating_after_store_and_before_commit_still_takes_effect()
    {
        var trail = new Trail { Id = Guid.NewGuid(), Name = "Before" };

        await using var session = _store.LightweightSession();
        session.Store(trail);

        // The document is serialized when the batch runs, not when Store is called.
        trail.Name = "After";
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Trail>(trail.Id, TestContext.Current.CancellationToken))!
            .Name.ShouldBe("After");
    }

    [Fact]
    public async Task a_guid_id_is_stored_as_lowercase_canonical_text()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Representation" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select typeof(id), id from fi_doc_trail where data ->> 'name' = 'Representation'";

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Not a BLOB, and not the uppercase form Microsoft.Data.Sqlite writes for a raw Guid
        // parameter. SQLite's default collation is case-sensitive, so an uppercase id here would be
        // written successfully and then never match on read — every load would quietly return null.
        reader.GetString(0).ShouldBe("text");
        reader.GetString(1).ShouldBe(id.ToString());
    }

    [Fact]
    public async Task loading_an_unknown_id_returns_null()
    {
        await using var session = _store.LightweightSession();

        (await session.LoadAsync<Trail>(Guid.NewGuid(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task nothing_is_written_until_save_changes()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Uncommitted" });
            // deliberately no SaveChangesAsync
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task loads_many_by_id()
    {
        var first = new Trail { Id = Guid.NewGuid(), Name = "One" };
        var second = new Trail { Id = Guid.NewGuid(), Name = "Two" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(first, second);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        // The unknown id is simply absent rather than a null entry.
        var loaded = await query.LoadManyAsync<Trail>(first.Id, Guid.NewGuid(), second.Id);

        loaded.Count.ShouldBe(2);
        loaded.Select(x => x.Name).OrderBy(x => x).ShouldBe(["One", "Two"]);
    }

    [Fact]
    public async Task load_many_with_no_ids_is_empty()
    {
        await using var session = _store.LightweightSession();
        (await session.LoadManyAsync<Trail>(Array.Empty<Guid>())).ShouldBeEmpty();
    }

    /// <remarks>
    ///     fisher#56 — these existed on the session and on no interface, so they were unreachable and
    ///     <c>LoadManyAsync</c> was the only read that could not be cancelled. The token leads because
    ///     nothing may follow a <c>params</c> array.
    /// </remarks>
    [Fact]
    public async Task loads_many_with_a_cancellation_token()
    {
        var trail = new Trail { Id = Guid.NewGuid(), Name = "One" };
        var marker = new Marker { Id = "m1", Label = "Cairn" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(trail);
            session.Store(marker);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.LoadManyAsync<Trail>(TestContext.Current.CancellationToken, trail.Id)).Count.ShouldBe(1);
        (await query.LoadManyAsync<Marker>(TestContext.Current.CancellationToken, "m1")).Count.ShouldBe(1);

        // And the explicit-identity form, which had no many-shape at all.
        (await query.LoadManyAsync<Trail, Guid>([trail.Id], TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);
    }

    [Fact]
    public async Task deletes_a_document()
    {
        var trail = new Trail { Id = Guid.NewGuid(), Name = "Doomed" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(trail);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Delete(trail);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Trail>(trail.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task deletes_by_id()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Doomed" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Delete<Trail>(id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task inserting_a_duplicate_identity_fails()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Insert(new Trail { Id = id, Name = "First" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = _store.LightweightSession();
        second.Insert(new Trail { Id = id, Name = "Duplicate" });

        await Should.ThrowAsync<Exception>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task round_trips_a_string_identified_document()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Marker { Id = "trig/nine-standards", Label = "Nine Standards Rigg" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var loaded = await query.LoadAsync<Marker>("trig/nine-standards", TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded!.Label.ShouldBe("Nine Standards Rigg");
    }

    [Fact]
    public async Task documents_and_events_commit_in_one_transaction()
    {
        var id = Guid.NewGuid();
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Trail { Id = id, Name = "Both" });
            session.Events.StartStream(streamId, new Events.QuestStarted("Both"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<Trail>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);
    }
}

public class storing_documents_with_a_numeric_identity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("documents-numeric");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = BuildStore();

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore BuildStore() => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Schema.For<Ledger>();
        options.Schema.For<Tally>();
    });

    [Fact]
    public async Task round_trips_a_long_identified_document()
    {
        var ledger = new Ledger { Balance = 42.5m };

        await using (var session = _store.LightweightSession())
        {
            session.Store(ledger);

            // Hi-Lo assigns at Store, not at commit, so the caller can use the id straight away.
            ledger.Id.ShouldBeGreaterThan(0L);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var loaded = await query.LoadAsync<Ledger>(ledger.Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(ledger.Id);
        loaded.Balance.ShouldBe(42.5m);
    }

    [Fact]
    public async Task round_trips_an_int_identified_document()
    {
        var tally = new Tally { Count = 7 };

        await using var session = _store.LightweightSession();
        session.Store(tally);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        tally.Id.ShouldBeGreaterThan(0);

        (await session.LoadAsync<Tally>(tally.Id, TestContext.Current.CancellationToken))!
            .Count.ShouldBe(7);
    }

    [Fact]
    public async Task assigns_increasing_ids_without_a_round_trip_per_document()
    {
        var first = new Ledger();
        var second = new Ledger();
        var third = new Ledger();

        await using var session = _store.LightweightSession();
        session.Store(first, second, third);

        // The whole point of Hi-Lo: one database fetch buys MaxLo ids, handed out client side.
        second.Id.ShouldBe(first.Id + 1);
        third.Id.ShouldBe(second.Id + 1);
    }

    [Fact]
    public async Task an_explicitly_assigned_id_is_left_alone()
    {
        var ledger = new Ledger { Id = 9_000, Balance = 1m };

        await using var session = _store.LightweightSession();
        session.Store(ledger);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        ledger.Id.ShouldBe(9_000L);
        (await session.LoadAsync<Ledger>(9_000L, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_second_store_over_the_same_file_does_not_reissue_ids()
    {
        var first = new Ledger();

        await using (var session = _store.LightweightSession())
        {
            session.Store(first);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A fresh store starts with no client-side allocation, so it must claim a new "hi" from
        // fi_hilo rather than start over at 1 — which is the whole reason the row is persisted.
        await using var second = BuildStore();
        var later = new Ledger();

        await using (var session = second.LightweightSession())
        {
            session.Store(later);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        later.Id.ShouldBeGreaterThan(first.Id);
    }

    [Fact]
    public async Task a_numeric_id_is_stored_as_an_integer()
    {
        var ledger = new Ledger { Balance = 3m };

        await using (var session = _store.LightweightSession())
        {
            session.Store(ledger);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select typeof(id) from fi_doc_ledger where id = @id";
        command.Parameters.AddWithValue("@id", ledger.Id);

        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("integer");
    }

    [Fact]
    public async Task loads_many_and_deletes_by_numeric_id()
    {
        var first = new Ledger { Balance = 1m };
        var second = new Ledger { Balance = 2m };

        await using (var session = _store.LightweightSession())
        {
            session.Store(first, second);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            (await session.LoadManyAsync<Ledger>(first.Id, second.Id, -1L)).Count.ShouldBe(2);

            session.Delete<Ledger>(first.Id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Ledger>(first.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await query.LoadAsync<Ledger>(second.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task resetting_the_sequence_floor_pushes_subsequent_ids_past_it()
    {
        await _store.Advanced.ResetHiloSequenceFloorAsync<Tally>(50_000);

        var tally = new Tally { Count = 1 };

        await using var session = _store.LightweightSession();
        session.Store(tally);

        tally.Id.ShouldBeGreaterThan(50_000);
    }

    [Fact]
    public async Task the_hilo_table_is_part_of_the_applied_schema()
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = 'fi_hilo'";

        // Registered up front because a mapped document type has a numeric id — the sequence would
        // create it on demand anyway, but a consumer scripting the schema out should see it.
        Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}

public class hilo_sequences_do_not_collide : IDisposable
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("documents-hilo-concurrency");

    public void Dispose() => _database.Dispose();

    /// <summary>
    ///     Six stores over one database file, each with its own sequence instance and therefore its own
    ///     "hi" allocation, pulling ids at the same time.
    /// </summary>
    /// <remarks>
    ///     This is the guard on Fisher advancing the hi with a single atomic upsert rather than
    ///     Polecat's read-then-compare-and-swap. Rewriting it as a read followed by an unguarded update
    ///     would let two stores claim the same hi and hand out the same thousand ids, which is exactly
    ///     what this asserts cannot happen.
    /// </remarks>
    [Fact]
    public async Task concurrent_stores_never_hand_out_the_same_id()
    {
        const int stores = 6;
        const int idsEach = 50;

        var built = Enumerable.Range(0, stores).Select(_ => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Ledger>();

            // Small enough that 50 ids means ten trips to fi_hilo rather than one, so the stores
            // actually contend for the row instead of each taking a single allocation up front.
            options.HiloSequenceDefaults.MaxLo = 5;
        })).ToArray();

        try
        {
            var pulled = await Task.WhenAll(built.Select(store => Task.Run(() =>
            {
                var sequence = store.Database.SequenceFor(typeof(Ledger));
                return Enumerable.Range(0, idsEach).Select(_ => sequence.NextLong()).ToArray();
            }, TestContext.Current.CancellationToken)));

            var all = pulled.SelectMany(x => x).OrderBy(x => x).ToArray();

            // Exactly 1..300, no duplicates and no gaps: every store consumes each allocation of five
            // in full, so the sixty hi values handed out have to be 0 through 59 with nothing repeated.
            all.ShouldBe(Enumerable.Range(1, stores * idsEach).Select(x => (long)x));
        }
        finally
        {
            foreach (var store in built)
            {
                await store.DisposeAsync();
            }
        }
    }
}

public class Trail
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Miles { get; set; }
}

public class Marker
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class Ledger
{
    public long Id { get; set; }
    public decimal Balance { get; set; }
}

public class Tally
{
    public int Id { get; set; }
    public int Count { get; set; }
}

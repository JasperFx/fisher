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
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Ledger>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task is_rejected_with_a_message_about_hi_lo()
    {
        await using var session = _store.LightweightSession();

        // The table shape supports an INTEGER id; what is missing is a way to assign one, since Weasel
        // offers only Hi-Lo for numeric identities and Fisher has no sequence table yet.
        var ex = Should.Throw<NotSupportedException>(() => session.Store(new Ledger { Id = 1 }));
        ex.Message.ShouldContain("Hi-Lo");

        await Task.CompletedTask;
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

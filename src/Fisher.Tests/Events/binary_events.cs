using System.Text;
using System.Text.Json;
using Fisher.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#43 / fisher#93 — <c>[BinaryEvent]</c> and <see cref="IEventBinarySerializer" />, storing an event
///     body as a BLOB rather than as JSON text.
/// </summary>
/// <remarks>
///     <para>
///         <b>Worth more here than the same feature is on Marten.</b> Fisher is embedded, so the
///         store's disk footprint is the application's, and SQLite has no <c>jsonb</c> — the literal
///         JSON text of every event is what is kept, property names included.
///     </para>
///     <para>
///         The trade is real and is what most of these tests pin: a binary body is not readable by
///         <c>json_extract</c>, so the operations that reach <em>into</em> a body refuse a binary type
///         by name rather than returning an empty result. Everything that reads the row's
///         <em>columns</em> is unaffected, which is why a stream can mix the two encodings.
///     </para>
/// </remarks>
public class binary_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("binary-events");
    private readonly CountingBinarySerializer _serializer = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(options => options.Events.DefaultBinarySerializer = _serializer);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    private DocumentStore StoreFor(Action<StoreOptions> extra)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Inline);
            extra(options);
        });

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_binary_event_round_trips()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new SoundingTaken(42, "fathoms"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        events.Single().Data.ShouldBeOfType<SoundingTaken>().Depth.ShouldBe(42);

        _serializer.Serialized.ShouldBe(1);
        _serializer.Deserialized.ShouldBe(1);
    }

    /// <remarks>
    ///     The column shape is the decision fisher#43 asked to make first: a separate nullable BLOB
    ///     column rather than BLOBs mixed into <c>data</c>. This asserts both halves — the binary row
    ///     holds the JSON placeholder in <c>data</c> and a BLOB in <c>data_binary</c>, and the JSON row
    ///     holds real JSON and a null.
    /// </remarks>
    [Fact]
    public async Task a_stream_can_mix_binary_and_json_events()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId,
                new VoyageBegun("Bristol"), new SoundingTaken(42, "fathoms"), new VoyageEnded("Lisbon"));

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        events.Select(x => x.Data.GetType().Name)
            .ShouldBe(["VoyageBegun", "SoundingTaken", "VoyageEnded"]);

        var encodings = await ReadEncodingsAsync();
        encodings.ShouldBe(["text/null", "text/blob", "text/null"]);
    }

    [Fact]
    public async Task live_aggregation_folds_a_binary_event()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(streamId,
                new VoyageBegun("Bristol"), new SoundingTaken(42, "fathoms"), new SoundingTaken(9, "fathoms"));

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        var live = await query.Events.AggregateStreamAsync<Voyage>(streamId, token: Token);
        live!.Soundings.ShouldBe(2);
        live.Shallowest.ShouldBe(9);

        // And the inline snapshot, which went through the same read on the way in.
        (await query.LoadAsync<Voyage>(streamId, Token))!.Soundings.ShouldBe(2);
    }

    /// <remarks>
    ///     The daemon's loader composes its SELECT from the same row reader every other read uses, so
    ///     this is really a check that it has not grown a copy — which is exactly the failure the
    ///     reader's "column order is locked here" contract exists to prevent.
    /// </remarks>
    [Fact]
    public async Task the_async_daemon_folds_a_binary_event()
    {
        await using var database = TemporaryDatabase.Create("binary-events-daemon");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.DefaultBinarySerializer = new CountingBinarySerializer();
            options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(streamId,
                new SoundingTaken(42, "fathoms"), new SoundingTaken(9, "fathoms"));

            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();

        await using var query = store.LightweightSession();
        (await query.LoadAsync<Voyage>(streamId, Token))!.Shallowest.ShouldBe(9);
    }

    // ---- what a binary event cannot do ----

    /// <remarks>
    ///     <c>data</c> holds the placeholder for a binary row, so <c>json_extract</c> over it answers
    ///     "no such member" for every row of the type and the query matches nothing at all. An empty
    ///     result is the wrong kind of answer to a question that cannot be asked.
    /// </remarks>
    [Fact]
    public async Task querying_into_a_binary_body_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async ()
            => await session.Events.QueryEventDataAsync<SoundingTaken>(x => x.Depth > 10, Token));

        ex.Message.ShouldContain("SoundingTaken");
        ex.Message.ShouldContain("[BinaryEvent]");
    }

    /// <remarks>
    ///     The single most likely way this feature could corrupt data: both rewrite operations write
    ///     the JSON <c>data</c> column, which against a binary row would leave a JSON body and a BLOB
    ///     body at once — and every reader resolves per row on the BLOB, so the JSON would be invisible
    ///     and the row quietly wrong. Refusing is acceptable; writing both is not.
    /// </remarks>
    [Fact]
    public async Task rewriting_a_binary_event_is_refused_by_name()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new SoundingTaken(42, "fathoms"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var stored = (await query.Events.FetchStreamAsync(streamId, token: Token)).Single();

        Should.Throw<InvalidOperationException>(() => query.Events.OverwriteEvent(stored))
            .Message.ShouldContain("SoundingTaken");

        Should.Throw<InvalidOperationException>(()
                => query.Events.CompletelyReplaceEvent(stored.Sequence, new SoundingTaken(1, "m")))
            .Message.ShouldContain("SoundingTaken");
    }

    /// <remarks>
    ///     Compacting works, because the snapshot it writes is a JSON <c>Compacted&lt;T&gt;</c> rather
    ///     than a binary body — and the replaced row's BLOB is cleared, or it would keep a body no
    ///     reader will ever look at.
    /// </remarks>
    [Fact]
    public async Task compacting_a_stream_of_binary_events_clears_the_blob()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(streamId,
                new VoyageBegun("Bristol"), new SoundingTaken(42, "fathoms"), new SoundingTaken(9, "fathoms"));

            await session.SaveChangesAsync(Token);
        }

        await using (var compacting = _store.LightweightSession())
        {
            await compacting.Events.CompactStreamAsync<Voyage>(streamId);
            await compacting.SaveChangesAsync(Token);
        }

        (await ReadEncodingsAsync()).ShouldBe(["text/null"]);

        await using var query = _store.LightweightSession();
        (await query.Events.AggregateStreamAsync<Voyage>(streamId, token: Token))!.Soundings.ShouldBe(2);
    }

    /// <remarks>
    ///     Marked <c>[BinaryEvent]</c> with no serializer configured is a configuration error, not a
    ///     silent reversion to JSON — writing JSON would put rows in the store in a format the operator
    ///     did not choose and believes they are not using.
    /// </remarks>
    [Fact]
    public async Task a_binary_event_without_a_serializer_is_refused_by_name()
    {
        await using var database = TemporaryDatabase.Create("binary-events-none");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new SoundingTaken(42, "fathoms"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async ()
            => await session.SaveChangesAsync(Token));

        ex.Message.ShouldContain("SoundingTaken");
        ex.Message.ShouldContain("BinarySerializer");
    }

    // ---- per-type registration (fisher#93) ----

    /// <remarks>
    ///     The route for an event type whose source you do not own, and the one a store-agnostic
    ///     consumer uses: no attribute anywhere on <see cref="VoyageEnded" />.
    /// </remarks>
    [Fact]
    public async Task an_unmarked_type_can_be_registered_binary_explicitly()
    {
        var serializer = new CountingBinarySerializer();

        await using var database = TemporaryDatabase.Create("binary-events-explicit");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.UseBinarySerializer<VoyageEnded>(serializer);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new VoyageBegun("Bristol"), new VoyageEnded("Lisbon"));
            await session.SaveChangesAsync(Token);
        }

        serializer.Serialized.ShouldBe(1);

        await using var query = store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        events[1].Data.ShouldBeOfType<VoyageEnded>().To.ShouldBe("Lisbon");
        serializer.Deserialized.ShouldBe(1);
    }

    /// <remarks>
    ///     Registration order must not matter, and it is easy for it to: a mapping is built by
    ///     <c>AddEventType</c> and by every projection registration, so a serializer set afterwards has
    ///     to reach a mapping that already exists. The compliance suite configures in exactly this
    ///     order, which is how the gap would otherwise have shown up.
    /// </remarks>
    [Fact]
    public async Task a_serializer_registered_after_the_event_type_still_takes_effect()
    {
        var serializer = new CountingBinarySerializer();

        await using var database = TemporaryDatabase.Create("binary-events-late");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType<SoundingTaken>();
            options.Events.AddEventType<VoyageEnded>();

            // Both routes, both after the types are known.
            options.Events.DefaultBinarySerializer = serializer;
            options.Events.UseBinarySerializer<VoyageEnded>(serializer);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(),
                new SoundingTaken(42, "fathoms"), new VoyageEnded("Lisbon"));

            await session.SaveChangesAsync(Token);
        }

        serializer.Serialized.ShouldBe(2);
    }

    // ---- schema, and the in-place upgrade it buys (fisher#93) ----

    /// <remarks>
    ///     <b>Unconditional, and that is the whole of fisher#93's storage half.</b> A store configured
    ///     with no serializer at all still has the column, so turning one event type binary later is a
    ///     configuration change rather than a migration.
    /// </remarks>
    [Fact]
    public async Task the_column_exists_whether_or_not_a_serializer_is_configured()
    {
        (await ColumnsAsync(_database.ConnectionString)).ShouldContain("data_binary");

        await using var database = TemporaryDatabase.Create("binary-events-schema");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ColumnsAsync(database.ConnectionString)).ShouldContain("data_binary");
    }

    [Fact]
    public async Task applying_the_configuration_again_is_a_no_op()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var store = StoreFor(options => options.Events.DefaultBinarySerializer = _serializer);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ColumnsAsync(_database.ConnectionString)).Count(x => x == "data_binary").ShouldBe(1);
    }

    /// <summary>
    ///     The property fisher#93 exists to protect: a type that was JSON becomes binary on a live file
    ///     with no migration, and the rows already written keep reading.
    /// </summary>
    /// <remarks>
    ///     This is what per-row dispatch buys and what a per-type dispatch would lose. Reading the
    ///     encoding off the event type would send the older rows through the binary path, where a null
    ///     <c>data_binary</c> means either an exception or an event with every member at its default —
    ///     silent, since the row and the stream are otherwise intact.
    /// </remarks>
    [Fact]
    public async Task turning_a_type_binary_on_an_existing_file_needs_no_migration()
    {
        await using var database = TemporaryDatabase.Create("binary-events-upgrade");
        var streamId = Guid.NewGuid();

        // Before: VoyageEnded is a plain JSON event, and a row of it is written.
        await using (var before = DocumentStore.For(options =>
                     {
                         options.ConnectionString = database.ConnectionString;
                         options.AutoCreateSchemaObjects = AutoCreate.All;
                     }))
        {
            await before.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = before.LightweightSession();
            session.Events.StartStream(streamId, new VoyageBegun("Bristol"), new VoyageEnded("Lisbon"));
            await session.SaveChangesAsync(Token);
        }

        // After: the same file, now with VoyageEnded registered binary. No migration between the two.
        var serializer = new CountingBinarySerializer();
        await using var after = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.UseBinarySerializer<VoyageEnded>(serializer);
        });

        await after.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = after.LightweightSession())
        {
            session.Events.Append(streamId, new VoyageEnded("Porto"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = after.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        events.Count.ShouldBe(3);
        events[1].Data.ShouldBeOfType<VoyageEnded>().To.ShouldBe("Lisbon");   // the pre-existing JSON row
        events[2].Data.ShouldBeOfType<VoyageEnded>().To.ShouldBe("Porto");    // the new binary row

        // One write and one read went through the serializer — the old row did not, which is the point.
        serializer.Serialized.ShouldBe(1);
        serializer.Deserialized.ShouldBe(1);

        (await ReadEncodingsAsync(database.ConnectionString))
            .ShouldBe(["text/null", "text/null", "text/blob"]);
    }

    /// <remarks>
    ///     The mirror image, and equally load-bearing: dropping the registration must not orphan the
    ///     rows already written binary. A store that decided encoding by type would read every one of
    ///     them as the JSON placeholder — an event with every member at its default, and no error.
    /// </remarks>
    [Fact]
    public async Task a_row_written_binary_still_needs_its_serializer_to_be_read()
    {
        await using var database = TemporaryDatabase.Create("binary-events-downgrade");
        var streamId = Guid.NewGuid();

        await using (var before = DocumentStore.For(options =>
                     {
                         options.ConnectionString = database.ConnectionString;
                         options.AutoCreateSchemaObjects = AutoCreate.All;
                         options.Events.UseBinarySerializer<VoyageEnded>(new CountingBinarySerializer());
                     }))
        {
            await before.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = before.LightweightSession();
            session.Events.StartStream(streamId, new VoyageEnded("Lisbon"));
            await session.SaveChangesAsync(Token);
        }

        await using var after = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await using var query = after.LightweightSession();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async ()
            => await query.Events.FetchStreamAsync(streamId, token: Token));

        ex.Message.ShouldContain("VoyageEnded");
        ex.Message.ShouldContain("data_binary");
    }

    /// <summary>How each row stores its body, oldest first — <c>text/null</c> or <c>text/blob</c>.</summary>
    private Task<List<string>> ReadEncodingsAsync() => ReadEncodingsAsync(_database.ConnectionString);

    /// <inheritdoc cref="ReadEncodingsAsync()" />
    private async Task<List<string>> ReadEncodingsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select typeof(data) || '/' || typeof(data_binary) from fi_events order by seq_id";

        var rows = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<List<string>> ColumnsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name from pragma_table_xinfo('fi_events')";

        var columns = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

/// <summary>
///     A deliberately unremarkable UTF-8 JSON encoder, which is enough to prove the seam without
///     taking a dependency on a real binary format — Fisher ships no implementation, and a test that
///     picked one would imply it did.
/// </summary>
public class CountingBinarySerializer : IEventBinarySerializer
{
    public int Serialized { get; private set; }
    public int Deserialized { get; private set; }

    public byte[] Serialize(Type eventType, object eventBody)
    {
        Serialized++;
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(eventBody, eventType));
    }

    public object Deserialize(Type eventType, byte[] data)
    {
        Deserialized++;
        return JsonSerializer.Deserialize(Encoding.UTF8.GetString(data), eventType)!;
    }
}

public record VoyageBegun(string From);

[BinaryEvent]
public record SoundingTaken(int Depth, string Unit);

public record VoyageEnded(string To);

public class Voyage
{
    public Guid Id { get; set; }
    public int Soundings { get; set; }
    public int Shallowest { get; set; } = int.MaxValue;

    public void Apply(SoundingTaken sounding)
    {
        Soundings++;
        Shallowest = Math.Min(Shallowest, sounding.Depth);
    }
}

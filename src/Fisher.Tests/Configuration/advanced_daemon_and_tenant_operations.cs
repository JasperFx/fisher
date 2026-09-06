using Fisher.Linq;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#173 — the <c>AdvancedOperations</c> members Marten carries and Fisher did not: the
///     unstick-the-daemon pair, the projection progress reads, single-stream rebuild, and wiping one
///     tenant's rows.
/// </summary>
/// <remarks>
///     <para>
///         Two of these are operational escape hatches whose whole value is that they exist before you
///         need them, so the tests are about the states they repair rather than about the calls
///         succeeding: a store whose projections are being retrofitted, and a store whose progression
///         has been left above the highest sequence there is.
///     </para>
/// </remarks>
public class advanced_daemon_and_tenant_operations : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("advanced-daemon");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<DocumentStore> StoreAsync(Action<StoreOptions>? extra = null)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            extra?.Invoke(options);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return store;
    }

    private async Task AppendAsync(DocumentStore store, int count, string? tenantId = null)
    {
        await using var session = store.LightweightSession(tenantId);

        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"Quest {i}"));
        }

        await session.SaveChangesAsync(Token);
    }

    // ---- AdvanceHighWaterMarkToLatestAsync ----

    /// <remarks>
    ///     The retrofit case: a large store that has never run a projection, where the mark climbing
    ///     from zero is a long read with nothing to show for it.
    /// </remarks>
    [Fact]
    public async Task advancing_the_high_water_mark_puts_it_at_the_highest_sequence()
    {
        await using var store = await StoreAsync();
        await AppendAsync(store, 7);

        (await store.Advanced.ProjectionProgressFor(new ShardName(ShardState.HighWaterMark), token: Token))
            .ShouldBe(0);

        await store.Advanced.AdvanceHighWaterMarkToLatestAsync(Token);

        (await store.Advanced.ProjectionProgressFor(new ShardName(ShardState.HighWaterMark), token: Token))
            .ShouldBe(7);
    }

    /// <remarks>
    ///     It advances the <em>mark</em>, not the shards — which is the half that decides whether this
    ///     is the right call for a store. A shard with no row still starts at zero, so a projection
    ///     registered after this still replays everything unless its own row is moved too.
    /// </remarks>
    [Fact]
    public async Task advancing_the_mark_leaves_the_shards_where_they_were()
    {
        await using var store = await StoreAsync(o => o.Projections.Snapshot<TallySnapshot>(SnapshotLifecycle.Async));
        await AppendAsync(store, 4);

        await store.Advanced.AdvanceHighWaterMarkToLatestAsync(Token);

        var shard = store.Advanced.AllAsyncProjectionShardNames().ShouldHaveSingleItem();
        (await store.Advanced.ProjectionProgressFor(shard, token: Token)).ShouldBe(0);
    }

    [Fact]
    public async Task advancing_an_empty_store_records_zero_rather_than_failing()
    {
        await using var store = await StoreAsync();

        await store.Advanced.AdvanceHighWaterMarkToLatestAsync(Token);

        (await store.Advanced.ProjectionProgressFor(new ShardName(ShardState.HighWaterMark), token: Token))
            .ShouldBe(0);
    }

    // ---- TryCorrectProgressInDatabaseAsync ----

    /// <remarks>
    ///     <b>Reachable on Fisher through a supported operation</b>, where Marten carries the same
    ///     method for a PostgreSQL race it believes it has closed: <c>seq_id</c> is
    ///     <c>AUTOINCREMENT</c>, and compacting and masking both delete rows, so removing events from
    ///     the top lowers <c>max(seq_id)</c> below progress already recorded. Planted with raw SQL here
    ///     because the state is what matters, not how it arose.
    /// </remarks>
    [Fact]
    public async Task correcting_pulls_a_stranded_row_back_to_the_ceiling()
    {
        await using var store = await StoreAsync();
        await AppendAsync(store, 3);

        await PlantProgressionAsync("HighWaterMark", 99);

        await store.Advanced.TryCorrectProgressInDatabaseAsync(Token);

        (await store.Advanced.ProjectionProgressFor(new ShardName(ShardState.HighWaterMark), token: Token))
            .ShouldBe(3);
    }

    /// <remarks>
    ///     <b>The discriminating fact against Marten's implementation, which resets every row
    ///     wholesale</b> the moment the high-water row is ahead. That drags a shard genuinely behind the
    ///     head <em>forward</em>, past events it never applied — silently, and on the very store
    ///     somebody is repairing. Only the impossible row moves here.
    /// </remarks>
    [Fact]
    public async Task correcting_leaves_a_shard_that_is_merely_behind_alone()
    {
        await using var store = await StoreAsync();
        await AppendAsync(store, 10);

        await PlantProgressionAsync("HighWaterMark", 44);
        await PlantProgressionAsync("Catching:All", 2);

        await store.Advanced.TryCorrectProgressInDatabaseAsync(Token);

        (await ProgressionAsync("HighWaterMark")).ShouldBe(10);
        (await ProgressionAsync("Catching:All")).ShouldBe(2);
    }

    [Fact]
    public async Task correcting_a_healthy_store_changes_nothing()
    {
        await using var store = await StoreAsync();
        await AppendAsync(store, 5);
        await PlantProgressionAsync("HighWaterMark", 5);

        await store.Advanced.TryCorrectProgressInDatabaseAsync(Token);

        (await ProgressionAsync("HighWaterMark")).ShouldBe(5);
    }

    // ---- the progression reads ----

    [Fact]
    public async Task all_projection_progress_reports_every_row()
    {
        await using var store = await StoreAsync();
        await AppendAsync(store, 6);

        await PlantProgressionAsync("HighWaterMark", 6);
        await PlantProgressionAsync("Catching:All", 4);

        var states = await store.Advanced.AllProjectionProgress(token: Token);

        states.Count.ShouldBe(2);
        states.Single(x => x.ShardName == "Catching:All").Sequence.ShouldBe(4);
    }

    /// <remarks>
    ///     Zero rather than an error for a shard nothing has recorded, which is what the daemon itself
    ///     reads as "start from the beginning" — a missing row and a row at zero mean the same thing.
    /// </remarks>
    [Fact]
    public async Task progress_for_a_shard_with_no_row_is_zero()
    {
        await using var store = await StoreAsync();

        (await store.Advanced.ProjectionProgressFor(new ShardName("Nothing"), token: Token)).ShouldBe(0);
    }

    [Fact]
    public async Task async_shard_names_cover_the_registered_async_projections_only()
    {
        await using var store = await StoreAsync(o =>
        {
            o.Projections.Snapshot<TallySnapshot>(SnapshotLifecycle.Async);
            o.Projections.Snapshot<InlineTally>(SnapshotLifecycle.Inline);
        });

        var names = store.Advanced.AllAsyncProjectionShardNames();

        // Inline has no shard because it has no progress to record.
        names.ShouldHaveSingleItem().Identity.ShouldContain(nameof(TallySnapshot));
    }

    // ---- RebuildSingleStreamAsync ----

    [Fact]
    public async Task rebuilding_one_stream_repairs_its_document()
    {
        await using var store = await StoreAsync(o => o.Projections.Snapshot<TallySnapshot>(SnapshotLifecycle.Inline));

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<TallySnapshot>(streamId, new MemberJoined("a"), new MemberJoined("b"));
            await session.SaveChangesAsync(Token);
        }

        // Corrupt the read model out of band, which is the state this method exists to repair.
        await using (var session = store.LightweightSession())
        {
            session.Store(new TallySnapshot { Id = streamId, Members = 99 });
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.RebuildSingleStreamAsync<TallySnapshot>(streamId, token: Token);

        await using var query = store.QuerySession();
        (await query.LoadAsync<TallySnapshot>(streamId, Token))!.Members.ShouldBe(2);
    }

    /// <remarks>
    ///     <b>Where Marten's equivalent throws from inside <c>Store(null!)</c>.</b> A stream that folds
    ///     to nothing is not exotic — a <c>ShouldDelete</c> that fired, or an id with no events at all —
    ///     and "no document" is exactly what a real rebuild leaves for such a stream, since teardown
    ///     clears the rows and the replay never recreates that one.
    /// </remarks>
    [Fact]
    public async Task rebuilding_a_stream_that_folds_to_nothing_removes_the_document()
    {
        await using var store = await StoreAsync(o => o.Projections.Snapshot<TallySnapshot>(SnapshotLifecycle.Inline));

        var orphan = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Store(new TallySnapshot { Id = orphan, Members = 5 });
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.RebuildSingleStreamAsync<TallySnapshot>(orphan, token: Token);

        await using var query = store.QuerySession();
        (await query.LoadAsync<TallySnapshot>(orphan, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task rebuilding_a_string_identified_stream_works_too()
    {
        await using var store = await StoreAsync(o =>
        {
            o.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
            o.Projections.Snapshot<KeyedTally>(SnapshotLifecycle.Inline);
        });

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<KeyedTally>("quest-1", new MemberJoined("a"));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession())
        {
            session.Store(new KeyedTally { Id = "quest-1", Members = 77 });
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.RebuildSingleStreamAsync<KeyedTally>("quest-1", token: Token);

        await using var query = store.QuerySession();
        (await query.LoadAsync<KeyedTally>("quest-1", Token))!.Members.ShouldBe(1);
    }

    /// <remarks>
    ///     A type registered through <c>Projections.StorageProviders</c> is deliberately never mapped,
    ///     so <c>Store</c> would create a <c>fi_doc_*</c> table nothing else ever reads — a rebuild that
    ///     silently wrote to the wrong place. Refused by name rather than half-working.
    /// </remarks>
    [Fact]
    public async Task rebuilding_a_type_with_no_fisher_mapping_is_refused_by_name()
    {
        await using var store = await StoreAsync();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            store.Advanced.RebuildSingleStreamAsync<UnmappedTally>(Guid.NewGuid(), token: Token));

        ex.Message.ShouldContain("StorageProviders");
        ex.Message.ShouldContain("RebuildProjectionAsync");
    }

    // ---- DeleteAllTenantDataAsync ----

    private Task<DocumentStore> ConjoinedStoreAsync() => StoreAsync(o =>
    {
        o.Events.TenancyStyle = TenancyStyle.Conjoined;
        o.Schema.For<TenantedNote>().MultiTenanted();
    });

    /// <remarks>
    ///     <b>The distinction this method rests on.</b> Fisher refuses tenant <em>deletion</em>, because
    ///     deprovisioning here means deleting a file it cannot know is backed up. Wiping a conjoined
    ///     tenant's rows destroys nothing a file restore would be needed to recover, and is the only way
    ///     to erase such a tenant at all.
    /// </remarks>
    [Fact]
    public async Task deleting_one_conjoined_tenants_data_leaves_the_other_tenant_alone()
    {
        await using var store = await ConjoinedStoreAsync();

        await AppendAsync(store, 3, "blue");
        await AppendAsync(store, 2, "green");

        await using (var session = store.LightweightSession("blue"))
        {
            session.Store(new TenantedNote { Id = Guid.NewGuid(), Text = "blue note" });
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession("green"))
        {
            session.Store(new TenantedNote { Id = Guid.NewGuid(), Text = "green note" });
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.DeleteAllTenantDataAsync("blue", Token);

        (await CountAsync("fi_events", "blue")).ShouldBe(0);
        (await CountAsync("fi_streams", "blue")).ShouldBe(0);
        (await CountAsync("fi_doc_tenantednote", "blue")).ShouldBe(0);

        (await CountAsync("fi_events", "green")).ShouldBe(2);
        (await CountAsync("fi_streams", "green")).ShouldBe(2);
        (await CountAsync("fi_doc_tenantednote", "green")).ShouldBe(1);
    }

    /// <remarks>
    ///     A tag row carries no tenant of its own and has a real foreign key to <c>fi_events(seq_id)</c>,
    ///     so it has to be reached through its events and deleted first. Without that this fails with
    ///     <c>FOREIGN KEY constraint failed</c> — the ordering fisher#6 established for
    ///     <c>DeleteAllEventDataAsync</c>, met again with a tenant predicate on it.
    /// </remarks>
    [Fact]
    public async Task deleting_a_tenants_data_clears_the_tag_rows_of_its_events_first()
    {
        await using var store = await StoreAsync(o =>
        {
            o.Events.TenancyStyle = TenancyStyle.Conjoined;
            o.Events.RegisterTagType<QuestTag>("quest");
        });

        await using (var session = store.LightweightSession("blue"))
        {
            var started = session.Events.BuildEvent(new QuestStarted("blue"));
            started.WithTag(new QuestTag(Guid.NewGuid()));
            session.Events.StartStream(Guid.NewGuid(), started);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession("green"))
        {
            var started = session.Events.BuildEvent(new QuestStarted("green"));
            started.WithTag(new QuestTag(Guid.NewGuid()));
            session.Events.StartStream(Guid.NewGuid(), started);
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.DeleteAllTenantDataAsync("blue", Token);

        (await CountAsync("fi_events", "blue")).ShouldBe(0);
        (await CountAsync("fi_events", "green")).ShouldBe(1);
        (await ScalarAsync("select count(*) from fi_event_tag_quest")).ShouldBe(1);
    }

    /// <remarks>
    ///     Under database-per-tenant the whole file is the tenant's, so this clears it — and
    ///     <b>leaves the file, its schema and its pooled connections where they were</b>. Removing the
    ///     file stays the operator's act, which is the limit Fisher's tenancy story keeps.
    /// </remarks>
    [Fact]
    public async Task deleting_a_tenants_data_under_database_per_tenant_clears_the_file_and_keeps_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-tenant-wipe-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var store = DocumentStore.For(options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.MultiTenantedDatabases(x => x.InDirectory(directory).AddTenants("one", "two"));
            });

            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await AppendAsync(store, 3, "one");
            await AppendAsync(store, 2, "two");

            await store.Advanced.DeleteAllTenantDataAsync("one", Token);

            await using (var session = store.QuerySession("one"))
            {
                (await session.Events.QueryEventsAsync(new JasperFx.Events.EventQuery(), Token)).Events
                    .ShouldBeEmpty();
            }

            // The file is still there and still works — a store that had deleted it would fail here.
            await AppendAsync(store, 1, "one");

            await using var after = store.QuerySession("one");
            (await after.Events.QueryEventsAsync(new JasperFx.Events.EventQuery(), Token)).Events.Count
                .ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    ///     Refused rather than silently doing nothing, which would report a successful erasure of a
    ///     tenant that has no rows because it has no column to have them under — the worst possible
    ///     answer to a compliance request.
    /// </remarks>
    [Fact]
    public async Task deleting_tenant_data_from_a_store_with_no_tenanted_data_is_refused_by_name()
    {
        await using var store = await StoreAsync();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            store.Advanced.DeleteAllTenantDataAsync("blue", Token));

        ex.Message.ShouldContain("Conjoined");
        ex.Message.ShouldContain("Advanced.Clean");
    }

    [Fact]
    public async Task deleting_an_unknown_tenants_data_under_database_per_tenant_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-tenant-unknown-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var store = DocumentStore.For(options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.MultiTenantedDatabases(x => x.InDirectory(directory).AddTenants("one"));
            });

            await Should.ThrowAsync<Storage.UnknownTenantException>(() =>
                store.Advanced.DeleteAllTenantDataAsync("nobody", Token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    ///     The progression rows describe how far the daemon read, not what a tenant owns — clearing them
    ///     would make every shard replay a store that is now empty.
    /// </remarks>
    [Fact]
    public async Task deleting_a_tenants_data_leaves_the_progression_rows_alone()
    {
        await using var store = await ConjoinedStoreAsync();

        await AppendAsync(store, 2, "blue");
        await PlantProgressionAsync("HighWaterMark", 2);

        await store.Advanced.DeleteAllTenantDataAsync("blue", Token);

        (await ProgressionAsync("HighWaterMark")).ShouldBe(2);
    }

    // ---- raw probes ----

    private async Task PlantProgressionAsync(string name, long sequence)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              insert into fi_event_progression (name, last_seq_id, last_updated)
                              values ($name, $seq, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                              on conflict (name) do update set last_seq_id = excluded.last_seq_id;
                              """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$seq", sequence);

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long> ProgressionAsync(string name)
        => await ScalarAsync($"select coalesce((select last_seq_id from fi_event_progression where name = '{name}'), -1)");

    private Task<long> CountAsync(string table, string tenantId)
        => ScalarAsync($"select count(*) from \"{table}\" where tenant_id = '{tenantId}'");

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}

public class TallySnapshot
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}

public class InlineTally
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}

public class KeyedTally
{
    public string Id { get; set; } = string.Empty;
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}

/// <summary>
///     Never registered as a document type, standing in for a projection whose storage is somewhere
///     Fisher does not own.
/// </summary>
public class UnmappedTally
{
    public Guid Id { get; set; }
}

public readonly record struct QuestTag(Guid Value);

public class TenantedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

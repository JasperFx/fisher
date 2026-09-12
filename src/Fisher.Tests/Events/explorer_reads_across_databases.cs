using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Fisher.Storage;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#240 — the explorer reads on a store that spans more than one SQLite file.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every fact here is one the shared suite structurally cannot reach.</b>
///         <c>EventStoreExplorerCompliance</c>'s fixture is single-database and its four
///         database-scoped tests say so in their own comments: what they pin is that the database
///         overload agrees with the store-global read, which is vacuously true of a store that ignores
///         the argument. The multi-database arm is where the bug was, and it is store-side.
///     </para>
///     <para>
///         The bug's shape is worth restating, because it is why these assert in both directions. Every
///         one of these reads went to <c>Database</c> — the store's <em>default</em> file — so a
///         database-per-tenant store answered from one tenant and the result was indistinguishable from
///         the whole store's. Right for whoever owned the default file, wrong for everybody else, and
///         silent either way. A test that only checked "the answer is non-empty" passes against that.
///     </para>
/// </remarks>
public class explorer_reads_across_databases : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-explorer-tenants-" + Guid.NewGuid().ToString("n")[..8]);

    private DocumentStore _store = null!;
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _south = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(databases => databases.InDirectory(_directory).AddTenants("north", "south"));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // North first, so "most recently updated" has a defined answer across the two files.
        await using (var session = _store.LightweightSession("north"))
        {
            session.Events.StartStream<Quest>(_north, new QuestStarted("Chart the Minch"));
            await session.SaveChangesAsync(Token);
        }

        await WaitForTheClockToMoveAsync();

        await using (var session = _store.LightweightSession("south"))
        {
            session.Events.StartStream<Quest>(_south, new QuestStarted("Chart the Solent"),
                new MemberJoined("Darwin"));
            await session.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    ///     Block until SQLite's own clock has left the millisecond north's row was stamped in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Without this, <c>the_count_bounds_the_merged_listing_and_the_newest_wins</c> is a real
    ///         intermittent</b>, and it was caught behaving like one: green on its own, red once in the
    ///         full class. <c>fi_streams.timestamp</c> is millisecond-precision, two back-to-back
    ///         appends land inside one millisecond on a warm machine, and
    ///         <c>OrderByDescending(LastUpdatedAt)</c> has no defined tiebreak — so "the newest wins"
    ///         becomes a coin flip exactly when the test is fastest.
    ///     </para>
    ///     <para>
    ///         <b>Polled against the column's own clock rather than slept for</b>, which is the
    ///         discipline <c>modified_since_and_before</c> established: a client-sampled bound compares
    ///         two clocks that are only incidentally the same one, and a fixed delay is a wall-clock
    ///         assumption that a loaded host can still lose. This asks the database what time it thinks
    ///         it is, which is the value that will be written, and returns the moment it has changed.
    ///     </para>
    /// </remarks>
    private async Task WaitForTheClockToMoveAsync()
    {
        await using var connection = await _store.Tenancy.DatabaseFor("north").OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select {SqliteTimestamp.NowExpression};";

        var start = (string)(await command.ExecuteScalarAsync(Token))!;

        while ((string)(await command.ExecuteScalarAsync(Token))! == start)
        {
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A tenant file still held by a pooled connection; the directory is under the temp path.
            }
        }
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IEventStore TheExplorer => _store;

    private async Task<IEventDatabase> DatabaseFor(string tenantId)
        => (await TheExplorer.AllDatabases()).Single(x => x.Identifier == tenantId);

    // ---- the claim the issue asked to be settled ----

    /// <remarks>
    ///     The doc comment on <c>DatabaseCardinality</c> claimed <c>Single</c> unconditionally while the
    ///     expression deferred to the tenancy. Both halves are asserted so the two cannot drift apart
    ///     again without something failing.
    /// </remarks>
    [Fact]
    public void the_store_reports_the_cardinality_it_actually_has()
    {
        TheExplorer.DatabaseCardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
        TheExplorer.HasMultipleTenants.ShouldBeTrue();
    }

    // ---- the listing fans out ----

    /// <summary>
    ///     A store-global listing reaches every tenant's file, not just the default one.
    /// </summary>
    /// <remarks>
    ///     This is the fact that fails against the old behaviour: the default file is north's, so the
    ///     old read returned north's stream and looked entirely correct until you asked about south.
    /// </remarks>
    [Fact]
    public async Task the_store_global_listing_reaches_every_database()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(10, Token);

        streams.Select(x => x.StreamId)
            .ShouldBe([_south.ToString(), _north.ToString()], ignoreOrder: true);
    }

    /// <summary>
    ///     And each merged summary carries the tenant whose file it came from.
    /// </summary>
    /// <remarks>
    ///     <b>This is what makes the store-global refusal below actionable rather than a dead end.</b>
    ///     A console lists streams, then drills into one — and the single-stream reads refuse without a
    ///     scope, so the listing has to hand it one. It cannot come off the row: <c>StreamsTable</c>
    ///     gives <c>tenant_id</c> a <c>DEFAULT '*DEFAULT*'</c> on a store that is not conjoined, so
    ///     every row in <c>north.db</c> reads <c>*DEFAULT*</c>. The file is the tenant, so the database
    ///     is what knows.
    /// </remarks>
    [Fact]
    public async Task a_fanned_out_summary_is_stamped_with_the_tenant_whose_file_it_came_from()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(10, Token);

        streams.Single(x => x.StreamId == _north.ToString()).TenantId.ShouldBe("north");
        streams.Single(x => x.StreamId == _south.ToString()).TenantId.ShouldBe("south");
    }

    /// <remarks>
    ///     The merge is a real merge rather than a concatenation that happens to be short enough:
    ///     <c>count</c> bounds the result across the two files, and the newest wins. South was written
    ///     second.
    /// </remarks>
    [Fact]
    public async Task the_count_bounds_the_merged_listing_and_the_newest_wins()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(1, Token);

        streams.Count.ShouldBe(1);
        streams[0].StreamId.ShouldBe(_south.ToString());
    }

    [Fact]
    public async Task a_tenant_scoped_listing_reads_that_tenants_file_only()
    {
        var north = await TheExplorer.GetRecentStreamsAsync(10, "north", Token);
        var south = await TheExplorer.GetRecentStreamsAsync(10, "south", Token);

        north.Select(x => x.StreamId).ShouldBe([_north.ToString()]);
        south.Select(x => x.StreamId).ShouldBe([_south.ToString()]);
    }

    [Fact]
    public async Task a_database_scoped_listing_reads_that_database_only()
    {
        var north = await TheExplorer.GetRecentStreamsAsync(await DatabaseFor("north"), 10, null, Token);
        var south = await TheExplorer.GetRecentStreamsAsync(await DatabaseFor("south"), 10, null, Token);

        north.Select(x => x.StreamId).ShouldBe([_north.ToString()]);
        south.Select(x => x.StreamId).ShouldBe([_south.ToString()]);
    }

    // ---- a single-stream lookup refuses rather than picking a file ----

    /// <summary>
    ///     A store-global lookup of one stream is refused, naming both ways forward.
    /// </summary>
    /// <remarks>
    ///     <b>Refused rather than fanned out, and the signature is the reason.</b> These return one
    ///     answer, and a stream id is unique within a database rather than across them — so there is no
    ///     merge to perform, and "whichever file we happened to open" is the bug. The message is
    ///     asserted, not just the exception type: a refusal that did not say which overloads exist would
    ///     leave a console with nowhere to go.
    /// </remarks>
    [Fact]
    public async Task a_store_global_stream_lookup_is_refused_with_both_ways_forward()
    {
        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => TheExplorer.GetStreamMetadataAsync(_north.ToString(), Token));

        ex.Message.ShouldContain("StaticMultiple");
        ex.Message.ShouldContain("tenantId");
        ex.Message.ShouldContain("database");
    }

    /// <remarks>
    ///     Its sibling, and the one where answering from the wrong file is worst: it would hand back
    ///     another tenant's events under the id the caller asked about. The refusal is raised eagerly
    ///     rather than from inside the iterator — an <c>async</c> iterator defers its body to the first
    ///     <c>MoveNextAsync</c>, so a refusal written inline there would surface at the
    ///     <c>await foreach</c> instead of at the call, which is the discipline
    ///     <c>ToAsyncEnumerable</c> already follows.
    /// </remarks>
    [Fact]
    public async Task a_store_global_stream_read_is_refused_too()
    {
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in TheExplorer.ReadStreamAsync(_north.ToString(), Token))
            {
            }
        });
    }

    [Fact]
    public async Task a_tenant_scoped_lookup_finds_its_own_stream_and_not_the_others()
    {
        (await TheExplorer.GetStreamMetadataAsync(_north.ToString(), "north", Token)).ShouldNotBeNull();

        // Null means "no such stream in THIS tenant", which is the distinction the store-global read
        // could not make — and the one that would have been answered wrongly by reading one file.
        (await TheExplorer.GetStreamMetadataAsync(_north.ToString(), "south", Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_database_scoped_lookup_finds_its_own_stream_and_not_the_others()
    {
        var north = await DatabaseFor("north");
        var south = await DatabaseFor("south");

        var found = await TheExplorer.GetStreamMetadataAsync(north, _north.ToString(), null, Token);
        found.ShouldNotBeNull();
        found.Version.ShouldBe(1);
        found.TenantId.ShouldBe("north");

        (await TheExplorer.GetStreamMetadataAsync(south, _north.ToString(), null, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_database_scoped_stream_read_returns_that_databases_events()
    {
        var versions = new List<long>();

        await foreach (var record in TheExplorer.ReadStreamAsync(await DatabaseFor("south"), _south.ToString(),
                           null, Token))
        {
            versions.Add(record.StreamVersion);
        }

        versions.ShouldBe([1L, 2L]);

        // And the same read against the other file finds nothing, rather than another tenant's stream.
        await foreach (var _ in TheExplorer.ReadStreamAsync(await DatabaseFor("north"), _south.ToString(),
                           null, Token))
        {
            throw new Exception("north.db answered for south's stream");
        }
    }

    [Fact]
    public async Task an_unknown_tenant_is_refused_rather_than_read_from_the_default_file()
    {
        await Should.ThrowAsync<UnknownTenantException>(
            () => TheExplorer.GetRecentStreamsAsync(10, "east", Token));
    }

    // ---- projection statuses (fisher#243) ----

    /// <summary>
    ///     A store-global status snapshot is refused once the store spans more than one file.
    /// </summary>
    /// <remarks>
    ///     <b>The refusal here is sharper than the stream lookups', not merely consistent with them.</b>
    ///     Those return one answer over an id that is unique within a database; this one could in
    ///     principle concatenate — <c>Advanced.AllProjectionProgress</c> does exactly that and documents
    ///     why. What stops it is that <c>ShardStatus</c> has no database or tenant field, so N
    ///     databases' rows come back as N entries per shard with the <em>same</em> ShardName and
    ///     different sequences, which a consumer cannot attribute. <c>ShardState</c> carries a TenantId,
    ///     which is what lets the other method get away with it.
    /// </remarks>
    [Fact]
    public async Task store_global_projection_statuses_are_refused_on_a_multi_database_store()
    {
        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => TheExplorer.GetProjectionStatusesAsync(Token));

        ex.Message.ShouldContain("StaticMultiple");
        ex.Message.ShouldContain("tenantId");
        ex.Message.ShouldContain("database");
    }

    /// <remarks>
    ///     And both scoped forms answer, which is what makes the refusal a signpost rather than a dead
    ///     end. Each tenant's file has its own <c>fi_event_progression</c>, so these are not filters
    ///     over a store-global answer — they are the answer, once per database.
    /// </remarks>
    [Fact]
    public async Task the_scoped_status_reads_answer_per_database()
    {
        var byTenant = await TheExplorer.GetProjectionStatusesAsync("north", Token);
        var byDatabase = await TheExplorer.GetProjectionStatusesAsync(await DatabaseFor("north"), null, Token);

        byTenant.Select(x => x.ProjectionName).ShouldBe(byDatabase.Select(x => x.ProjectionName));

        // No daemon is hosted here, so the state is Unknown rather than a guess at Stopped.
        foreach (var shard in byTenant.SelectMany(x => x.Shards))
        {
            shard.State.ShouldBe("Unknown");
        }
    }

    // ---- what a monitoring tool is told about the shape of the store ----

    /// <remarks>
    ///     Both usage descriptors hardcoded <c>Single</c>, which a console does not render as "unknown"
    ///     — it renders it as a one-file store. The <c>Databases</c> list is where the tenants are, and
    ///     an empty one claims there are none. Same failure the missing <c>Subscriptions</c> list was in
    ///     fisher#120, one field over.
    /// </remarks>
    [Fact]
    public async Task the_usage_descriptors_report_every_database()
    {
        var events = await TheExplorer.TryCreateUsage(Token);
        events.ShouldNotBeNull();
        events.Database.ShouldNotBeNull();
        events.Database.Cardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
        events.Database.Databases.Count.ShouldBe(2);

        var documents = await ((IDocumentStoreUsageSource)_store).TryCreateUsage(Token);
        documents.ShouldNotBeNull();
        documents.Database.ShouldNotBeNull();
        documents.Database.Cardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
        documents.Database.Databases.Count.ShouldBe(2);
    }
}

/// <summary>
///     The other arm of tenant scoping: one file, and the tenant is a column value.
/// </summary>
/// <remarks>
///     <b>A tenant predicate in SQL is correct here and wrong under database-per-tenant</b>, which is
///     the asymmetry <c>ResolveTenantScope</c> exists for and the half the old tenant-scoped
///     <c>ReadStreamAsync</c> had backwards: it composed <c>and tenant_id = @tenant_id</c>
///     unconditionally, against the store's default file. Under conjoined tenancy that is right; under
///     database-per-tenant the column holds <c>*DEFAULT*</c> in every file, so it matched nothing.
/// </remarks>
public class explorer_reads_under_conjoined_tenancy : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("explorer-conjoined");
    private DocumentStore _store = null!;
    private readonly Guid _north = Guid.NewGuid();
    private readonly Guid _south = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession("north");
        session.Events.StartStream<Quest>(_north, new QuestStarted("Chart the Minch"));
        session.ForTenant("south").Events.StartStream<Quest>(_south, new QuestStarted("Chart the Solent"));
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IEventStore TheExplorer => _store;

    /// <remarks>
    ///     One file, so the cardinality is <c>Single</c> — and the store is still multi-tenanted, which
    ///     is the pair <c>HasMultipleTenants</c> got wrong by answering <see langword="false" />
    ///     unconditionally. A console reading that renders no tenant dimension at all.
    /// </remarks>
    [Fact]
    public void one_file_can_still_be_multi_tenanted()
    {
        TheExplorer.DatabaseCardinality.ShouldBe(DatabaseCardinality.Single);
        TheExplorer.HasMultipleTenants.ShouldBeTrue();
    }

    [Fact]
    public async Task a_tenant_scoped_listing_filters_on_the_column()
    {
        var north = await TheExplorer.GetRecentStreamsAsync(10, "north", Token);

        north.Select(x => x.StreamId).ShouldBe([_north.ToString()]);
        north[0].TenantId.ShouldBe("north");
    }

    /// <remarks>
    ///     The store-global read is <em>not</em> refused here, because one file has one answer — it
    ///     returns both tenants' streams, as it always did. The refusal is about database cardinality,
    ///     not about tenancy.
    /// </remarks>
    [Fact]
    public async Task the_store_global_listing_still_spans_both_tenants()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(10, Token);

        streams.Select(x => x.StreamId)
            .ShouldBe([_north.ToString(), _south.ToString()], ignoreOrder: true);
    }

    /// <remarks>
    ///     The case jasperfx#503 added the tenant overloads for: the same lookup, two tenants, and the
    ///     tenant-less read resolves whichever the default session carries.
    /// </remarks>
    [Fact]
    public async Task a_tenant_scoped_lookup_will_not_see_another_tenants_stream()
    {
        (await TheExplorer.GetStreamMetadataAsync(_south.ToString(), "south", Token)).ShouldNotBeNull();
        (await TheExplorer.GetStreamMetadataAsync(_south.ToString(), "north", Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_tenant_scoped_stream_read_will_not_see_another_tenants_events()
    {
        var mine = new List<long>();
        await foreach (var record in TheExplorer.ReadStreamAsync(_south.ToString(), "south", Token))
        {
            mine.Add(record.StreamVersion);
        }

        mine.ShouldBe([1L]);

        await foreach (var _ in TheExplorer.ReadStreamAsync(_south.ToString(), "north", Token))
        {
            throw new Exception("the north tenant answered for south's stream");
        }
    }

    /// <remarks>
    ///     A single-database store's one database is necessarily the argument, so the database overload
    ///     is the store-global read — which is what the shared suite pins, and what has to keep holding
    ///     once the multi-database arm exists beside it.
    /// </remarks>
    [Fact]
    public async Task the_database_overload_agrees_with_the_store_global_read()
    {
        var database = (await TheExplorer.AllDatabases()).Single();

        var scoped = await TheExplorer.GetRecentStreamsAsync(database, 10, null, Token);
        var global = await TheExplorer.GetRecentStreamsAsync(10, Token);

        scoped.Select(x => x.StreamId).ShouldBe(global.Select(x => x.StreamId));
    }
}

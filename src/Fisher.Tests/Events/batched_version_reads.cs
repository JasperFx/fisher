using Fisher.Exceptions;
using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     The append planner reads every stream's current version in one set-based query per planning
///     pass (fisher#164), where it used to issue one scalar query per stream — 2N round trips per
///     save with inline projections on, N of them under the exclusive write lock.
/// </summary>
/// <remarks>
///     <para>
///         There is no SQL-capture seam over the planner's connection, so these tests hold the
///         batched read to the per-stream read's exact semantics instead: mixed new/existing/missing
///         streams in one save, the optimistic guard and the start-collision guard inside a
///         multi-stream save, archived streams (whose version still reads — the per-stream query
///         never filtered on <c>is_archived</c> and the batched one must not either), both stream
///         identity styles, per-tenant reads under conjoined tenancy, and the early best-effort pass
///         that hands inline projections their versions.
///     </para>
///     <para>
///         The two passes deliberately stay two reads rather than one. The early pass runs outside
///         the write lock so inline projections have versions to fold; the in-transaction pass is the
///         authoritative read the optimistic concurrency check and the final numbering rest on.
///         Merging them, or seeding one from the other, is exactly the race the planner's class
///         remarks forbid.
///     </para>
/// </remarks>
public class batched_version_reads : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("batched-versions");
    private DocumentStore _store = null!;
    private DocumentStore _projectionStore = null!;
    private DocumentStore _stringStore = null!;
    private DocumentStore _conjoinedStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        _projectionStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "projected";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<VersionSnapshot>();
            options.Projections.Add(new VersionRecordingProjection(), ProjectionLifecycle.Inline);
        });

        _stringStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "strings";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
        });

        _conjoinedStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "conjoined";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _projectionStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _stringStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _conjoinedStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _projectionStore.DisposeAsync();
        await _stringStore.DisposeAsync();
        await _conjoinedStore.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task one_save_plans_new_existing_and_missing_streams_together()
    {
        var existingA = Guid.NewGuid();
        var existingB = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var started = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(existingA, new QuestStarted("A"), new MemberJoined("A1"));
            seed.Events.StartStream(existingB, new QuestStarted("B"));
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(existingA, new MonsterSlain("A troll"));
            session.Events.Append(existingB, new MemberJoined("B1"), new MemberJoined("B2"));

            // Appending to a stream that does not exist creates it — the batched read reports it
            // as missing, exactly as the per-stream read did.
            session.Events.Append(missing, new QuestStarted("M"));

            var startedAction = session.Events.StartStream(started, new QuestStarted("S"), new MemberJoined("S1"));

            await session.SaveChangesAsync(Token);

            // Versions were assigned from each stream's own current version, not from a neighbour's.
            startedAction.Events[0].Version.ShouldBe(1);
            startedAction.Events[1].Version.ShouldBe(2);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(existingA, Token))!.Version.ShouldBe(3);
        (await query.Events.FetchStreamStateAsync(existingB, Token))!.Version.ShouldBe(3);
        (await query.Events.FetchStreamStateAsync(missing, Token))!.Version.ShouldBe(1);
        (await query.Events.FetchStreamStateAsync(started, Token))!.Version.ShouldBe(2);

        var streamA = await query.Events.FetchStreamAsync(existingA, token: Token);
        streamA.Select(x => x.Version).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task a_start_collision_inside_a_multi_stream_save_is_still_rejected()
    {
        var taken = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(taken, new QuestStarted("Taken"));
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Fine"));
        session.Events.StartStream(taken, new QuestStarted("Collides"));

        await Should.ThrowAsync<Fisher.Exceptions.ExistingStreamIdCollisionException>(() => session.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task a_stale_expected_version_inside_a_multi_stream_save_is_still_rejected()
    {
        var guarded = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(guarded, new QuestStarted("Guarded"));
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Fine"));
        session.Events.Append(guarded, 5, new MemberJoined("Too eager"));

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(() => session.SaveChangesAsync(Token));
    }

    /// <summary>
    ///     The batched read still sees an archived stream — an archived stream's version is still its
    ///     version, and the read does not filter on <c>is_archived</c>. What the planner does with that
    ///     answer changed in fisher#184: the append is now refused rather than landing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This test used to append to the archived stream and assert the write succeeded, taking
    ///         version 3 as the evidence that the batched read had found the row. That was Fisher
    ///         diverging from both siblings, and <c>StreamArchivingCompliance</c>'s
    ///         <c>appending_to_an_archived_stream_is_rejected</c> is what said so — archiving is not a
    ///         soft delete you can keep writing through.
    ///     </para>
    ///     <para>
    ///         The read is still the subject, and the refusal is a better probe of it than the append
    ///         was: the planner can only refuse a stream whose row the batched read actually returned.
    ///         The unarchived stream in the same unit of work is what makes it a batch rather than a
    ///         single read, and it is left unwritten by the refusal, which is the second half of the
    ///         contract — a rejected append rolls the whole unit of work back.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task an_archived_stream_still_reads_its_version()
    {
        var archived = Guid.NewGuid();
        var fresh = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(archived, new QuestStarted("Old"), new MemberJoined("Old timer"));
            await seed.SaveChangesAsync(Token);
        }

        await using (var archiver = _store.LightweightSession())
        {
            archiver.Events.ArchiveStream(archived);
            await archiver.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(archived, new MonsterSlain("Posthumous"));
            session.Events.StartStream(fresh, new QuestStarted("New"));

            var refusal = await Should.ThrowAsync<Fisher.Exceptions.ArchivedStreamException>(
                () => session.SaveChangesAsync(Token));
            refusal.Id.ShouldBe(archived);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(archived, Token))!.Version.ShouldBe(2);
        (await query.Events.FetchStreamStateAsync(fresh, Token)).ShouldBeNull();

        // Unarchive and the same append lands, which is what makes the refusal a state rather than a
        // verdict on the events.
        await using (var reopened = _store.LightweightSession())
        {
            reopened.Events.UnArchiveStream(archived);
            await reopened.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(archived, new MonsterSlain("Posthumous"));
            await session.SaveChangesAsync(Token);
        }

        await using var after = _store.LightweightSession();
        (await after.Events.FetchStreamStateAsync(archived, Token))!.Version.ShouldBe(3);

        // And starting a stream over an archived id still collides — archived is not missing, and the
        // collision is deliberately the answer there rather than the archive refusal: a caller who
        // reused an id needs a different one, not an unarchive.
        await using (var rearchiver = _store.LightweightSession())
        {
            rearchiver.Events.ArchiveStream(archived);
            await rearchiver.SaveChangesAsync(Token);
        }

        await using var collider = _store.LightweightSession();
        collider.Events.StartStream(archived, new QuestStarted("Reused"));
        await Should.ThrowAsync<Fisher.Exceptions.ExistingStreamIdCollisionException>(() => collider.SaveChangesAsync(Token));
    }

    /// <summary>
    ///     The early best-effort pass — the one that exists so inline projections fold events that
    ///     already know their versions — batches too, and still hands every stream its own numbering.
    /// </summary>
    [Fact]
    public async Task inline_projections_receive_per_stream_versions_across_a_multi_stream_save()
    {
        var seeded = Guid.NewGuid();

        await using (var seed = _projectionStore.LightweightSession())
        {
            seed.Events.StartStream(seeded, new VersionRecorded("seeded-1"), new VersionRecorded("seeded-2"));
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _projectionStore.LightweightSession())
        {
            session.Events.Append(seeded, new VersionRecorded("seeded-3"));
            session.Events.StartStream(Guid.NewGuid(), new VersionRecorded("new-1"), new VersionRecorded("new-2"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _projectionStore.LightweightSession();

        (await query.LoadAsync<VersionSnapshot>("seeded-3", Token))!.Version.ShouldBe(3);
        (await query.LoadAsync<VersionSnapshot>("new-1", Token))!.Version.ShouldBe(1);
        (await query.LoadAsync<VersionSnapshot>("new-2", Token))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task string_identified_streams_are_planned_together_too()
    {
        await using (var seed = _stringStore.LightweightSession())
        {
            seed.Events.StartStream("existing-key", new QuestStarted("Existing"));
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.Append("existing-key", new MemberJoined("More"));
            session.Events.StartStream("started-key", new QuestStarted("Started"));
            session.Events.Append("missing-key", new QuestStarted("Missing"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _stringStore.LightweightSession();
        (await query.Events.FetchStreamStateAsync("existing-key", Token))!.Version.ShouldBe(2);
        (await query.Events.FetchStreamStateAsync("started-key", Token))!.Version.ShouldBe(1);
        (await query.Events.FetchStreamStateAsync("missing-key", Token))!.Version.ShouldBe(1);
    }

    /// <summary>
    ///     Under conjoined tenancy the row key is <c>(tenant_id, id)</c> and one save can span
    ///     tenants via <c>ForTenant</c> — so the same stream id must read each tenant's own version,
    ///     never a neighbour tenant's.
    /// </summary>
    [Fact]
    public async Task the_same_stream_id_reads_each_tenants_own_version_in_one_save()
    {
        var streamId = Guid.NewGuid();

        await using (var seed = _conjoinedStore.LightweightSession("north"))
        {
            seed.Events.StartStream(streamId, new QuestStarted("North 1"), new MemberJoined("North 2"));
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _conjoinedStore.LightweightSession("north"))
        {
            session.Events.Append(streamId, new MonsterSlain("North 3"));
            session.ForTenant("south").Events.StartStream(streamId, new QuestStarted("South 1"));
            await session.SaveChangesAsync(Token);
        }

        await using var north = _conjoinedStore.LightweightSession("north");
        await using var south = _conjoinedStore.LightweightSession("south");

        (await north.Events.FetchStreamStateAsync(streamId, Token))!.Version.ShouldBe(3);
        (await south.Events.FetchStreamStateAsync(streamId, Token))!.Version.ShouldBe(1);
    }
}

public record VersionRecorded(string Name);

// partial, because JasperFx's source generator emits the conventional-method dispatcher into it.
public partial class VersionRecordingProjection : EventProjection
{
    public VersionSnapshot Create(IEvent<VersionRecorded> e) => new()
    {
        Id = e.Data.Name,
        Version = e.Version,
        StreamId = e.StreamId
    };
}

public class VersionSnapshot
{
    public string Id { get; set; } = string.Empty;
    public long Version { get; set; }
    public Guid StreamId { get; set; }
}

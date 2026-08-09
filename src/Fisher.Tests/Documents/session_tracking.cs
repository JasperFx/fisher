using Fisher.Internal;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#31 — <see cref="DocumentTracking" />, the identity map, dirty tracking, and the
///     <c>Eject</c> family.
/// </summary>
/// <remarks>
///     <para>
///         The identity map's cheap half is the skipped read; <b>the half worth testing is reference
///         identity</b>, because its absence is what turns "load twice, mutate one, store both" into a
///         lost update that looks like last-write-wins. So most of these assert on
///         <c>ShouldBeSameAs</c> rather than on values.
///     </para>
///     <para>
///         The map covers <c>Query&lt;T&gt;()</c> as well as <c>LoadAsync</c>, which is Marten's
///         documented behaviour ("applied to all documents loaded by Id or Linq queries"). It falls out
///         of the LINQ provider resolving its storage through the session rather than being arranged.
///     </para>
/// </remarks>
public class session_tracking : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tracking");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<TrackedRod>();
            options.Schema.For<TrackedReel>().UseOptimisticConcurrency();
            options.Schema.For<TrackedTackle>().AddSubClass<TrackedSpinner>().AddSubClass<TrackedSpoon>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<TrackedRod> PersistAsync(string name = "Split cane", int length = 9)
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = name, Length = length };

        await using var session = _store.LightweightSession();
        session.Store(rod);
        await session.SaveChangesAsync(Token);

        return rod;
    }

    // ---- the identity map ----

    [Fact]
    public async Task two_loads_in_an_identity_session_return_the_same_instance()
    {
        var rod = await PersistAsync();

        await using var session = _store.IdentitySession();

        var first = await session.LoadAsync<TrackedRod>(rod.Id, Token);
        var second = await session.LoadAsync<TrackedRod>(rod.Id, Token);

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task a_lightweight_session_returns_a_fresh_instance_every_time()
    {
        var rod = await PersistAsync();

        await using var session = _store.LightweightSession();

        var first = await session.LoadAsync<TrackedRod>(rod.Id, Token);
        var second = await session.LoadAsync<TrackedRod>(rod.Id, Token);

        first.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);
    }

    /// <remarks>
    ///     Both directions, because "the query populates the map" and "the query reads the map" are
    ///     different claims and only one of them is about the selector.
    /// </remarks>
    [Fact]
    public async Task a_query_participates_in_the_identity_map()
    {
        var rod = await PersistAsync();

        await using var session = _store.IdentitySession();

        var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
        var queried = await session.Query<TrackedRod>().Where(x => x.Id == rod.Id).FirstAsync(Token);

        queried.ShouldBeSameAs(loaded);

        await using var reversed = _store.IdentitySession();

        var first = await reversed.Query<TrackedRod>().Where(x => x.Id == rod.Id).FirstAsync(Token);

        (await reversed.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeSameAs(first);
    }

    [Fact]
    public async Task a_stored_document_is_mapped_before_it_is_committed()
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = "Greenheart", Length = 12 };

        await using var session = _store.IdentitySession();
        session.Store(rod);

        // No row exists yet, so this can only be the map answering.
        (await session.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeSameAs(rod);
    }

    /// <remarks>
    ///     The row is removed out of band between the two calls, so a <c>LoadManyAsync</c> that still
    ///     returns it can only be answering from the map — which is the read the preselect exists to
    ///     skip. Reference identity alone would not tell the two apart, because the identity-map
    ///     selector returns the cached instance for a row it re-reads.
    /// </remarks>
    [Fact]
    public async Task load_many_answers_mapped_ids_without_reading_them()
    {
        var rod = await PersistAsync();

        await using var session = _store.IdentitySession();
        var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);

        await using (var other = _store.LightweightSession())
        {
            other.HardDelete<TrackedRod>(rod.Id);
            await other.SaveChangesAsync(Token);
        }

        var many = await session.LoadManyAsync<TrackedRod>(rod.Id);

        many.Count.ShouldBe(1);
        many[0].ShouldBeSameAs(loaded);
    }

    /// <remarks>
    ///     Marten's rule, and the reason an identity map is a safety feature rather than a cache:
    ///     storing two instances of one document silently writes one over the other.
    /// </remarks>
    [Fact]
    public async Task storing_a_second_instance_under_a_mapped_id_is_refused()
    {
        var rod = await PersistAsync();

        await using var session = _store.IdentitySession();
        await session.LoadAsync<TrackedRod>(rod.Id, Token);

        var impostor = new TrackedRod { Id = rod.Id, Name = "Different object, same id", Length = 1 };

        var ex = Should.Throw<InvalidOperationException>(() => session.Store(impostor));
        ex.Message.ShouldContain("identity map");
    }

    [Fact]
    public async Task a_lightweight_session_does_not_refuse_a_second_instance()
    {
        var rod = await PersistAsync();

        await using var session = _store.LightweightSession();
        await session.LoadAsync<TrackedRod>(rod.Id, Token);

        session.Store(new TrackedRod { Id = rod.Id, Name = "Second instance", Length = 1 });
        await session.SaveChangesAsync(Token);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Second instance");
    }

    // ---- Eject ----

    [Fact]
    public async Task ejecting_a_document_unqueues_its_write()
    {
        var kept = new TrackedRod { Id = Guid.NewGuid(), Name = "Kept", Length = 8 };
        var dropped = new TrackedRod { Id = Guid.NewGuid(), Name = "Dropped", Length = 8 };

        await using (var session = _store.IdentitySession())
        {
            session.Store(kept, dropped);
            session.Eject(dropped);

            (await session.LoadAsync<TrackedRod>(dropped.Id, Token)).ShouldBeNull();

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<TrackedRod>(kept.Id, Token)).ShouldNotBeNull();
        (await query.LoadAsync<TrackedRod>(dropped.Id, Token)).ShouldBeNull();
    }

    /// <remarks>
    ///     By reference, not by identity — otherwise ejecting a copy would take back a write the caller
    ///     made with a different instance, which is exactly the confusion the map exists to prevent.
    ///     A lightweight session, because an identity session refuses the second instance outright.
    /// </remarks>
    [Fact]
    public async Task ejecting_a_different_instance_leaves_the_queued_write_alone()
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = "Queued", Length = 7 };

        await using (var session = _store.LightweightSession())
        {
            session.Store(rod);
            session.Eject(new TrackedRod { Id = rod.Id, Name = "Impostor", Length = 7 });

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Queued");
    }

    [Fact]
    public async Task ejecting_a_document_does_not_touch_one_already_committed()
    {
        var rod = await PersistAsync();

        await using (var session = _store.IdentitySession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
            session.Eject(loaded!);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     A hierarchy shares one table and therefore one identity-map entry, keyed by the base type —
    ///     so ejecting one sub-class has to reach into that entry rather than drop it, or it would take
    ///     the siblings with it.
    /// </remarks>
    [Fact]
    public async Task ejecting_all_of_a_type_leaves_the_rest_of_a_hierarchy_mapped()
    {
        var spinner = new TrackedSpinner { Id = Guid.NewGuid(), Name = "Mepps", BladeSize = 3 };
        var spoon = new TrackedSpoon { Id = Guid.NewGuid(), Name = "Toby", Grams = 18 };

        await using (var seed = _store.LightweightSession())
        {
            seed.Store<TrackedTackle>(spinner, spoon);
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.IdentitySession();

        var loadedSpinner = await session.LoadAsync<TrackedTackle>(spinner.Id, Token);
        var loadedSpoon = await session.LoadAsync<TrackedTackle>(spoon.Id, Token);

        session.EjectAllOfType(typeof(TrackedSpinner));

        (await session.LoadAsync<TrackedTackle>(spoon.Id, Token)).ShouldBeSameAs(loadedSpoon);
        (await session.LoadAsync<TrackedTackle>(spinner.Id, Token)).ShouldNotBeSameAs(loadedSpinner);
    }

    [Fact]
    public async Task ejecting_all_of_a_type_unqueues_its_writes()
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = "Dropped", Length = 6 };

        await using (var session = _store.IdentitySession())
        {
            session.Store(rod);
            session.EjectAllOfType(typeof(TrackedRod));

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeNull();
    }

    /// <remarks>
    ///     Documents, events and the identity map all at once, because "pending changes" means every
    ///     kind of queued work and "not the identity map" is the qualification Marten states explicitly.
    /// </remarks>
    [Fact]
    public async Task ejecting_all_pending_changes_drops_the_work_and_keeps_the_map()
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = "Never written", Length = 5 };
        var streamId = Guid.NewGuid();

        await using var session = _store.IdentitySession();

        session.Store(rod);
        session.Events.StartStream(streamId, new RodBuilt(rod.Id));

        session.EjectAllPendingChanges();

        ((FisherSession)session).PendingOperations.Count.ShouldBe(0);

        // Nothing is written, and the session still knows what it holds.
        await session.SaveChangesAsync(Token);
        (await session.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeSameAs(rod);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeNull();
        (await query.Events.FetchStreamAsync(streamId, token: Token)).Count.ShouldBe(0);
    }

    // ---- dirty tracking ----

    [Fact]
    public async Task a_changed_document_is_written_without_store_being_called()
    {
        var rod = await PersistAsync("Original");

        await using (var session = _store.DirtyTrackedSession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
            loaded!.Name = "Changed";

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Changed");
    }

    [Fact]
    public async Task an_identity_session_does_not_write_a_changed_document()
    {
        var rod = await PersistAsync("Original");

        await using (var session = _store.IdentitySession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
            loaded!.Name = "Changed in memory only";

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Original");
    }

    /// <remarks>
    ///     Asserted by planting a competing write rather than by counting operations: an unchanged
    ///     document that is written anyway is not an error, it just silently overwrites whatever
    ///     happened in between. That is the failure this has to catch.
    /// </remarks>
    [Fact]
    public async Task an_unchanged_document_is_not_written()
    {
        var rod = await PersistAsync("Original");

        await using var session = _store.DirtyTrackedSession();
        await session.LoadAsync<TrackedRod>(rod.Id, Token);

        await using (var other = _store.LightweightSession())
        {
            other.Store(new TrackedRod { Id = rod.Id, Name = "Written by someone else", Length = 9 });
            await other.SaveChangesAsync(Token);
        }

        await session.SaveChangesAsync(Token);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Written by someone else");
    }

    /// <remarks>
    ///     <b>The reset, and it needs the competing writer to be visible at all.</b> A test that simply
    ///     changed the document twice passes without the reset — the baseline would be stale but the
    ///     document really did change, so the write happens anyway. What the reset actually prevents is
    ///     a <em>second</em> commit rewriting a document nothing has touched since the first, which is
    ///     only observable as somebody else's write disappearing. Verified by removing the reset: the
    ///     two-changes test still passed and this one did not.
    /// </remarks>
    [Fact]
    public async Task a_committed_document_is_not_written_again_by_the_next_commit()
    {
        var rod = await PersistAsync("First");

        await using var session = _store.DirtyTrackedSession();
        var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);

        loaded!.Name = "Second";
        await session.SaveChangesAsync(Token);

        await using (var other = _store.LightweightSession())
        {
            other.Store(new TrackedRod { Id = rod.Id, Name = "Written by someone else", Length = 9 });
            await other.SaveChangesAsync(Token);
        }

        await session.SaveChangesAsync(Token);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Written by someone else");
    }

    [Fact]
    public async Task a_second_change_after_a_commit_is_detected_too()
    {
        var rod = await PersistAsync("First");

        await using (var session = _store.DirtyTrackedSession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);

            loaded!.Name = "Second";
            await session.SaveChangesAsync(Token);

            loaded.Name = "Third";
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Third");
    }

    /// <remarks>
    ///     The other half of the reset: a document the session created rather than read has no tracker
    ///     until one is made for it after the commit that wrote it. Without that, dirty tracking would
    ///     apply only to documents that happened to pre-exist the session.
    /// </remarks>
    [Fact]
    public async Task a_document_stored_rather_than_loaded_becomes_tracked_after_its_commit()
    {
        var rod = new TrackedRod { Id = Guid.NewGuid(), Name = "Created", Length = 10 };

        await using (var session = _store.DirtyTrackedSession())
        {
            session.Store(rod);
            await session.SaveChangesAsync(Token);

            rod.Name = "Changed after creation";
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Changed after creation");
    }

    /// <remarks>
    ///     The tracker has to go with the delete, or change detection resurrects the row the caller just
    ///     removed — an outcome with nothing anywhere to report it.
    /// </remarks>
    [Fact]
    public async Task deleting_a_changed_document_by_id_does_not_resurrect_it()
    {
        var rod = await PersistAsync();

        await using (var session = _store.DirtyTrackedSession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
            loaded!.Name = "Changed, then deleted";

            session.Delete<TrackedRod>(rod.Id);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task ejecting_a_document_stops_it_being_change_tracked()
    {
        var rod = await PersistAsync("Original");

        await using (var session = _store.DirtyTrackedSession())
        {
            var loaded = await session.LoadAsync<TrackedRod>(rod.Id, Token);
            loaded!.Name = "Changed";

            session.Eject(loaded);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedRod>(rod.Id, Token))!.Name.ShouldBe("Original");
    }

    /// <remarks>
    ///     Optimistic concurrency and dirty tracking meeting is worth its own test: the detected write
    ///     is an upsert carrying the version the read captured, so it has to be the version tracker's
    ///     value rather than nothing at all.
    /// </remarks>
    [Fact]
    public async Task dirty_tracking_writes_through_the_optimistic_concurrency_guard()
    {
        var reel = new TrackedReel { Id = Guid.NewGuid(), Maker = "Hardy" };

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(reel);
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.DirtyTrackedSession();
        var loaded = await session.LoadAsync<TrackedReel>(reel.Id, Token);
        loaded!.Maker = "Orvis";

        // A competing write moves the version on, so the guard the detected upsert carries must fail.
        await using (var other = _store.LightweightSession())
        {
            var theirs = await other.LoadAsync<TrackedReel>(reel.Id, Token);
            theirs!.Maker = "Shakespeare";
            other.Store(theirs);
            await other.SaveChangesAsync(Token);
        }

        await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<TrackedReel>(reel.Id, Token))!.Maker.ShouldBe("Shakespeare");
    }

    // ---- the modes themselves ----

    [Fact]
    public void the_default_session_tracks_nothing()
    {
        new SessionOptions().Tracking.ShouldBe(DocumentTracking.None);

        using var lightweight = (FisherSession)_store.LightweightSession();
        lightweight.SessionOptions.Tracking.ShouldBe(DocumentTracking.None);

        using var opened = (FisherSession)_store.OpenSession(new SessionOptions());
        opened.SessionOptions.Tracking.ShouldBe(DocumentTracking.None);
    }

    /// <remarks>
    ///     <b>fisher#13's shape, one layer up.</b> The identity map and the change-tracker list are
    ///     per-session mutable state and are unguarded, which is only safe because the one caller that
    ///     drives a session from several threads — the async daemon — opens untracked sessions. This
    ///     pins that, so making the daemon's sessions tracked has to be a deliberate act.
    /// </remarks>
    [Fact]
    public void the_daemon_opens_untracked_sessions()
    {
        var store = (JasperFx.Events.IEventStore<IDocumentSession, IQuerySession>)_store;

        using var session = (FisherSession)store.OpenSession(_store.Database);
        session.SessionOptions.Tracking.ShouldBe(DocumentTracking.None);

        using var tenanted = (FisherSession)store.OpenSession(_store.Database, "north");
        tenanted.SessionOptions.Tracking.ShouldBe(DocumentTracking.None);
    }

    [Fact]
    public void the_tracked_session_factories_carry_their_tenant()
    {
        using var identity = (FisherSession)_store.IdentitySession("north");
        identity.SessionOptions.Tracking.ShouldBe(DocumentTracking.IdentityOnly);
        identity.TenantId.ShouldBe("north");

        using var dirty = (FisherSession)_store.DirtyTrackedSession("south");
        dirty.SessionOptions.Tracking.ShouldBe(DocumentTracking.DirtyTracking);
        dirty.TenantId.ShouldBe("south");
    }
}

public class TrackedRod
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Length { get; set; }
}

public class TrackedReel
{
    public Guid Id { get; set; }
    public string Maker { get; set; } = string.Empty;
}

public class TrackedTackle
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class TrackedSpinner : TrackedTackle
{
    public int BladeSize { get; set; }
}

public class TrackedSpoon : TrackedTackle
{
    public int Grams { get; set; }
}

public record RodBuilt(Guid RodId);

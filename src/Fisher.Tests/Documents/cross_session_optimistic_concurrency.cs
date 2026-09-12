using JasperFx;
using JasperFx.Metadata;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#245 — a guarded write that crosses a session boundary, which is the workflow optimistic
///     concurrency exists for.
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher's Guid guard used to work only inside one session.</b> It is fed from
///         <c>IStorageSession.Versions</c> — what <em>this</em> session read — so a document loaded in
///         one session and stored through another had no entry and failed its guard every time. Safe
///         about staleness, useless for load-in-one-request, save-in-the-next.
///     </para>
///     <para>
///         <b>Worse than the bug it was found next to.</b> marten#5372 was a mapped version member being
///         invisible while the <see cref="IVersioned" /> marker interface worked; Fisher consulted
///         neither, so both routes failed identically. That is why every fact here is asserted for both
///         — a fix that seeded only one would look right against whichever route the test happened to
///         use.
///     </para>
///     <para>
///         <b>And no shared suite reaches it, for any store.</b> The only document-concurrency suite is
///         <c>NumericRevisionCompliance</c>, which is numeric-only and written against
///         <see cref="IRevisioned" />; every other "optimistic" fact in the shared set is about the
///         event store. Fisher passed all fifty enrolled suites with this broken.
///     </para>
/// </remarks>
public class cross_session_optimistic_concurrency : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cross-session-concurrency");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // The second of the two routes: no marker interface, an ordinary member mapped onto the
            // version column. DocumentMetadata maps IVersioned.Version onto the same column by
            // convention, so the two arrive at the storage identically.
            options.Schema.For<MappedLedger>().UseOptimisticConcurrency(true)
                .Metadata(m => m.Version.MapTo(x => x.Revision));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the marker-interface route ----

    /// <summary>
    ///     Load in one session, store through another. This threw <c>ConcurrencyException</c> before.
    /// </summary>
    [Fact]
    public async Task a_versioned_document_can_be_stored_through_a_later_session()
    {
        var id = await CreateVersionedAsync("opening");

        VersionedLedger loaded;
        await using (var session = _store.LightweightSession())
        {
            loaded = (await session.LoadAsync<VersionedLedger>(id, Token))!;
        }

        loaded.Version.ShouldNotBe(Guid.Empty);
        loaded.Entry = "amended";

        await using (var session = _store.LightweightSession())
        {
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<VersionedLedger>(id, Token))!.Entry.ShouldBe("amended");
    }

    /// <summary>
    ///     And a genuinely stale write is still refused, which is the half that was never broken.
    /// </summary>
    /// <remarks>
    ///     Asserted alongside the one above rather than trusted, because the cheap wrong fix — seeding
    ///     nothing, or seeding whatever the row currently holds — makes the first test pass and this one
    ///     fail. The pair is what says the guard is real rather than absent.
    /// </remarks>
    [Fact]
    public async Task a_stale_versioned_write_is_still_refused()
    {
        var id = await CreateVersionedAsync("opening");

        VersionedLedger stale;
        await using (var session = _store.LightweightSession())
        {
            stale = (await session.LoadAsync<VersionedLedger>(id, Token))!;
        }

        // Somebody else moves the row on.
        await using (var session = _store.LightweightSession())
        {
            var current = (await session.LoadAsync<VersionedLedger>(id, Token))!;
            current.Entry = "theirs";
            session.Store(current);
            await session.SaveChangesAsync(Token);
        }

        stale.Entry = "mine";

        await using (var loser = _store.LightweightSession())
        {
            loser.Store(stale);
            await Should.ThrowAsync<ConcurrencyException>(() => loser.SaveChangesAsync(Token));
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<VersionedLedger>(id, Token))!.Entry.ShouldBe("theirs");
    }

    // ---- the mapped-member route ----

    /// <remarks>
    ///     The same two facts against a type that declares its version through the mapping DSL instead.
    ///     This is the route marten#5372 was filed about; on Fisher both routes were broken, so both are
    ///     pinned.
    /// </remarks>
    [Fact]
    public async Task a_mapped_version_member_behaves_exactly_as_the_interface_does()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new MappedLedger { Id = id, Entry = "opening" });
            await session.SaveChangesAsync(Token);
        }

        MappedLedger loaded;
        await using (var session = _store.LightweightSession())
        {
            loaded = (await session.LoadAsync<MappedLedger>(id, Token))!;
        }

        loaded.Revision.ShouldNotBe(Guid.Empty);
        loaded.Entry = "amended";

        await using (var session = _store.LightweightSession())
        {
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }

        await using (var query = _store.LightweightSession())
        {
            (await query.LoadAsync<MappedLedger>(id, Token))!.Entry.ShouldBe("amended");
        }

        // ...and a stale instance is refused. Loaded fresh rather than reusing the one above, because a
        // successful write moves that instance's version on — see
        // a_successful_write_moves_the_instances_own_version_on, which is what makes storing the same
        // instance twice in a row work at all.
        MappedLedger stale;
        await using (var reader = _store.LightweightSession())
        {
            stale = (await reader.LoadAsync<MappedLedger>(id, Token))!;
        }

        await using (var other = _store.LightweightSession())
        {
            var current = (await other.LoadAsync<MappedLedger>(id, Token))!;
            current.Entry = "theirs";
            other.Store(current);
            await other.SaveChangesAsync(Token);
        }

        stale.Entry = "mine";

        await using (var loser = _store.LightweightSession())
        {
            loser.Store(stale);
            await Should.ThrowAsync<ConcurrencyException>(() => loser.SaveChangesAsync(Token));
        }
    }

    /// <summary>
    ///     A successful guarded write moves the stored instance's own version member on.
    /// </summary>
    /// <remarks>
    ///     <b>Not a detail — it is what makes seeding safe to do on every store.</b> Without it, storing
    ///     the same instance twice in one process would seed the second write with the version the first
    ///     one superseded, and a perfectly ordinary "save, edit, save again" would fail its guard. It is
    ///     also the behaviour that made the first version of the mapped-member test above wrong, which is
    ///     why it is now pinned rather than relied on silently.
    /// </remarks>
    [Fact]
    public async Task a_successful_write_moves_the_instances_own_version_on()
    {
        var id = await CreateVersionedAsync("opening");

        VersionedLedger loaded;
        await using (var reader = _store.LightweightSession())
        {
            loaded = (await reader.LoadAsync<VersionedLedger>(id, Token))!;
        }

        var asLoaded = loaded.Version;

        await using (var session = _store.LightweightSession())
        {
            loaded.Entry = "second";
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }

        loaded.Version.ShouldNotBe(asLoaded);

        // So the same instance can be stored again without tripping its own guard.
        await using (var session = _store.LightweightSession())
        {
            loaded.Entry = "third";
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<VersionedLedger>(id, Token))!.Entry.ShouldBe("third");
    }

    // ---- the conditions the seeding turns on ----

    /// <summary>
    ///     Creating a versioned document still works — the seeding does not turn every insert into a
    ///     guarded write.
    /// </summary>
    /// <remarks>
    ///     <b>This does not pin the <c>!= Guid.Empty</c> condition, and saying so is the point.</b> The
    ///     obvious reading is that the condition is what keeps creates working, and it is not: removing
    ///     it changes no outcome, because an expectation of <see cref="Guid.Empty" /> matches no stored
    ///     version, so a blank version inserts over a missing row and raises
    ///     <c>ConcurrencyException</c> over an existing one either way. Measured both ways rather than
    ///     reasoned about. What this test does pin is the behaviour a reader actually cares about —
    ///     that a create on a versioned type is not collateral damage from the fix.
    /// </remarks>
    [Fact]
    public async Task a_document_that_has_never_been_stored_is_not_guarded()
    {
        var fresh = new VersionedLedger { Id = Guid.NewGuid(), Entry = "first" };
        fresh.Version.ShouldBe(Guid.Empty);

        await using var session = _store.LightweightSession();
        session.Store(fresh);
        await session.SaveChangesAsync(Token);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<VersionedLedger>(fresh.Id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     <c>Update</c> seeds too, and <c>Insert</c> deliberately does not — an insert writes a new row,
    ///     so there is no stored version for a guard to compare against. Marten draws the line in the
    ///     same place.
    /// </remarks>
    [Fact]
    public async Task update_crosses_a_session_boundary_as_well_as_store()
    {
        var id = await CreateVersionedAsync("opening");

        VersionedLedger loaded;
        await using (var session = _store.LightweightSession())
        {
            loaded = (await session.LoadAsync<VersionedLedger>(id, Token))!;
        }

        loaded.Entry = "amended";

        await using (var session = _store.LightweightSession())
        {
            session.Update(loaded);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<VersionedLedger>(id, Token))!.Entry.ShouldBe("amended");
    }

    /// <remarks>
    ///     A type with no version member at all is untouched by any of this — nothing to read, nothing
    ///     to seed, and no guard in its SQL.
    /// </remarks>
    [Fact]
    public async Task a_type_with_no_version_member_is_unaffected()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new PlainLedger { Id = id, Entry = "opening" });
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Store(new PlainLedger { Id = id, Entry = "overwritten" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<PlainLedger>(id, Token))!.Entry.ShouldBe("overwritten");
    }

    private async Task<Guid> CreateVersionedAsync(string entry)
    {
        var id = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Store(new VersionedLedger { Id = id, Entry = entry });
        await session.SaveChangesAsync(Token);

        return id;
    }
}

public class VersionedLedger : IVersioned
{
    public Guid Id { get; set; }
    public string Entry { get; set; } = string.Empty;
    public Guid Version { get; set; }
}

public class MappedLedger
{
    public Guid Id { get; set; }
    public string Entry { get; set; } = string.Empty;
    public Guid Revision { get; set; }
}

public class PlainLedger
{
    public Guid Id { get; set; }
    public string Entry { get; set; } = string.Empty;
}

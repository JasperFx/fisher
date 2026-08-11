using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#63 — a composite's members are torn down on rebuild, including the ones registered
///     through a wrapper. Sibling of marten#5175.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect class:</b> a composite's rebuild teardown asks each member what it publishes.
///         A bare <see cref="Fisher.Projections.IProjection" /> is held by
///         <c>CompositeIProjectionSource</c>, which was constructed with a fresh, empty
///         <c>AsyncOptions</c> and never told what the projection it wraps writes — so the teardown saw
///         the wrapper and enumerated nothing. Progression rows were still deleted, so the rebuild
///         restarted from sequence zero and replayed <em>on top of</em> the previous run's rows.
///     </para>
///     <para>
///         <b>Why an ordinary rebuild test cannot catch it.</b> A replay rewrites every row it can
///         still produce, so a surviving row is invisible for every stream whose events are still
///         there — <c>composite_projections.a_rebuild_reproduces_every_stage</c> passes either way.
///         What the teardown is for is the row a replay <em>cannot</em> recreate, so each test here
///         plants one against an id no event mentions. Same discipline the flat-table
///         (<c>IPublishesTables</c>) and EF Core teardowns already needed.
///     </para>
///     <para>
///         <b>What a raw projection can and cannot declare.</b> A bare <c>IProjection</c> that is not a
///         <c>ProjectionBase</c> describes neither its storage nor its teardown, and a composite cannot
///         invent one — so its rows survive a rebuild unless the registration says otherwise, which is
///         what the <c>Add(IProjection, Action&lt;AsyncOptions&gt;, int)</c> overload is for. Both
///         halves are pinned below so the silent one reads as a decision rather than as this bug
///         again.
///     </para>
/// </remarks>
public class composite_member_teardown : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("composite-teardown");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            // A bare IProjection has no mapping of its own, and Fisher only creates tables for types
            // the schema has mapped.
            o.Schema.For<Tally>();
            o.Schema.For<Ledger>();
            o.Schema.For<Note>();
            o.Schema.For<Sketch>();

            o.Projections.CompositeProjectionFor("ledger", composite =>
            {
                // Declared at registration, which is the only place a raw IProjection's teardown can
                // be said.
                composite.Add(new TallyProjection(), options => options.DeleteViewTypeOnTeardown<Tally>());

                // Declares its own, on itself — the wrapper has to adopt them.
                composite.Add(new LedgerProjection(), stageNumber: 2);

                // Declares nothing anywhere: its rows are expected to survive.
                composite.Add(new SketchProjection(), stageNumber: 2);

                // The composite's own teardown rules, which are not any member's.
                composite.Options.DeleteViewTypeOnTeardown<Note>();
            });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     One real stream, plus a row against an id no event mentions — which only a teardown can
    ///     remove.
    /// </summary>
    private async Task<(Guid Real, Guid Orphan)> SeedAsync()
    {
        var real = Guid.NewGuid();
        var orphan = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(real, new Landed("Trout"), new Landed("Pike"));
            await session.SaveChangesAsync(Token);
        }

        using (var daemon = await _store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();
            await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(20));
        }

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Tally { Id = orphan, Doubled = 999 });
            session.Store(new Ledger { Id = orphan, Entries = 999 });
            session.Store(new Note { Id = orphan, Text = "stale" });
            session.Store(new Sketch { Id = orphan, Lines = 999 });
            await session.SaveChangesAsync(Token);
        }

        return (real, orphan);
    }

    private async Task RebuildAsync()
    {
        using var daemon = await _store.BuildProjectionDaemonAsync();
        await daemon.RebuildProjectionAsync("ledger", TimeSpan.FromSeconds(30), Token);
    }

    /// <summary>
    ///     A member registered as a raw <c>IProjection</c> with its teardown declared at registration.
    /// </summary>
    [Fact]
    public async Task a_wrapped_members_declared_teardown_runs()
    {
        var (real, orphan) = await SeedAsync();

        await RebuildAsync();

        await using var session = _store.LightweightSession();
        (await session.LoadAsync<Tally>(orphan, Token)).ShouldBeNull();
        (await session.LoadAsync<Tally>(real, Token)).ShouldNotBeNull().Doubled.ShouldBe(4);
    }

    /// <summary>
    ///     A member that carries its own <c>AsyncOptions</c> — the wrapper adopts them rather than
    ///     keeping the empty set it was constructed with.
    /// </summary>
    [Fact]
    public async Task a_wrapped_members_own_options_are_adopted()
    {
        var (real, orphan) = await SeedAsync();

        await RebuildAsync();

        await using var session = _store.LightweightSession();
        (await session.LoadAsync<Ledger>(orphan, Token)).ShouldBeNull();
        (await session.LoadAsync<Ledger>(real, Token)).ShouldNotBeNull().Entries.ShouldBe(2);
    }

    /// <summary>
    ///     The composite's own teardown rules are its own — no member declares <c>Note</c>.
    /// </summary>
    /// <remarks>
    ///     JasperFx's <c>CompositeProjection.PublishedTypes()</c> overrides the base to return the
    ///     stages' types, which drops the composite's own <c>Options.StorageTypes</c> — so
    ///     <c>composite.Options.DeleteViewTypeOnTeardown&lt;T&gt;()</c> was a silent no-op. The second
    ///     defect in marten#5175's branch, in Fisher's idiom.
    /// </remarks>
    [Fact]
    public async Task the_composites_own_teardown_rules_run()
    {
        var (_, orphan) = await SeedAsync();

        await RebuildAsync();

        await using var session = _store.LightweightSession();
        (await session.LoadAsync<Note>(orphan, Token)).ShouldBeNull();
    }

    /// <summary>
    ///     A raw projection declaring nothing keeps its rows, deliberately.
    /// </summary>
    /// <remarks>
    ///     There is nothing for the composite to tear down and nothing it could invent without guessing
    ///     at what the projection writes. Marten decided the same. This is here so the silence is a
    ///     pinned decision rather than the bug above wearing a different hat.
    /// </remarks>
    [Fact]
    public async Task a_raw_projection_that_declares_nothing_keeps_its_rows()
    {
        var (_, orphan) = await SeedAsync();

        await RebuildAsync();

        await using var session = _store.LightweightSession();
        (await session.LoadAsync<Sketch>(orphan, Token)).ShouldNotBeNull().Lines.ShouldBe(999);
    }

    public record Landed(string Species);

    public class Tally
    {
        public Guid Id { get; set; }
        public int Doubled { get; set; }
    }

    public class Ledger
    {
        public Guid Id { get; set; }
        public int Entries { get; set; }
    }

    public class Note
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public class Sketch
    {
        public Guid Id { get; set; }
        public int Lines { get; set; }
    }

    public class TallyProjection : Fisher.Projections.IProjection
    {
        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var group in events.GroupBy(x => x.StreamId))
            {
                operations.Store(new Tally { Id = group.Key, Doubled = group.Count() * 2 });
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     A projection that is both a <c>ProjectionBase</c> and a bare <c>IProjection</c>, so it has
    ///     options of its own for the wrapper to adopt.
    /// </summary>
    public class LedgerProjection : ProjectionBase, Fisher.Projections.IProjection
    {
        public LedgerProjection() => Options.DeleteViewTypeOnTeardown<Ledger>();

        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var group in events.GroupBy(x => x.StreamId))
            {
                operations.Store(new Ledger { Id = group.Key, Entries = group.Count() });
                operations.Store(new Note { Id = group.Key, Text = "seen" });
            }

            return Task.CompletedTask;
        }
    }

    public class SketchProjection : Fisher.Projections.IProjection
    {
        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var group in events.GroupBy(x => x.StreamId))
            {
                operations.Store(new Sketch { Id = group.Key, Lines = group.Count() });
            }

            return Task.CompletedTask;
        }
    }
}

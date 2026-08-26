using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace Fisher.Tests.Events;

/// <summary>
///     Two projections publishing one document type, and what a rebuild of either does to the other —
///     fisher#122, reported during a Marten to Fisher migration.
/// </summary>
/// <remarks>
///     <para>
///         <b>The behaviour is by design and is not changed here.</b> Teardown deletes the whole of
///         every table the named projection publishes into and the rebuild then replays only that
///         projection, so a shared <c>fi_doc_*</c> table loses the other projection's rows.
///         <b>Marten behaves identically.</b> What was missing is that nothing said so: the rebuild
///         succeeds, the rebuilt projection is correct, and the damage is to a read model nobody is
///         looking at.
///     </para>
///     <para>
///         So the first test below <em>documents the loss</em> rather than asserting against it, and
///         the rest are about the warning. Changing the semantics would break a configuration that
///         works fine as long as both projections are rebuilt together, which is a real way to run
///         this — see <c>DocumentStore.WarnAboutTablesSharedWithAnotherProjection</c>.
///     </para>
///     <para>
///         <b>Same family as the teardown gaps #63 and the flat-table <c>IPublishesTables</c> work, and
///         the opposite direction.</b> Those were the sweep clearing too little; this is it clearing
///         too much. Both are "what gets cleared does not match what gets rewritten".
///     </para>
/// </remarks>
public class shared_published_table_rebuild : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("shared-teardown");
    private DocumentStore _store = null!;
    private readonly RecordingLogger _logger = new();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            o.Schema.For<Tally>();

            // Both publish Tally, so both write into fi_doc_tally.
            o.Projections.Add(new LandedTally(), ProjectionLifecycle.Async);
            o.Projections.Add(new ReleasedTally(), ProjectionLifecycle.Async);

            // Publishes a type of its own, so the warning has a negative case to be silent about.
            o.Projections.Add(new BoatLog(), ProjectionLifecycle.Async);
            o.Schema.For<Boat>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(Guid Landed, Guid Released)> SeedAsync()
    {
        var landed = Guid.NewGuid();
        var released = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(landed, new Landed("Trout"));
            session.Events.StartStream(released, new Released("Pike"));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await _store.BuildProjectionDaemonAsync(logger: _logger);
        await daemon.StartAllAsync();
        await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(20));

        return (landed, released);
    }

    private async Task RebuildAsync(string projectionName)
    {
        using var daemon = await _store.BuildProjectionDaemonAsync(logger: _logger);
        await daemon.RebuildProjectionAsync(projectionName, TimeSpan.FromSeconds(30), Token);
    }

    /// <summary>
    ///     The reported behaviour, reproduced. Rebuilding one projection leaves the other's row gone —
    ///     and the rebuild reports success.
    /// </summary>
    [Fact]
    public async Task rebuilding_one_projection_clears_the_rows_of_another_sharing_its_table()
    {
        var (landed, released) = await SeedAsync();

        await using (var before = _store.LightweightSession())
        {
            (await before.LoadAsync<Tally>(landed, Token)).ShouldNotBeNull();
            (await before.LoadAsync<Tally>(released, Token)).ShouldNotBeNull();
        }

        await RebuildAsync(nameof(LandedTally));

        await using var after = _store.LightweightSession();

        // The rebuilt projection is correct.
        (await after.LoadAsync<Tally>(landed, Token)).ShouldNotBeNull().Count.ShouldBe(1);

        // The other one's row is gone. Documented, not asserted against -- changing this would break
        // a configuration that works when both are rebuilt together.
        (await after.LoadAsync<Tally>(released, Token)).ShouldBeNull();
    }

    /// <summary>
    ///     The point of fisher#122: the operator is told, at the moment they are present and the
    ///     information is actionable.
    /// </summary>
    [Fact]
    public async Task the_rebuild_warns_and_names_the_other_projection()
    {
        await SeedAsync();
        _logger.Warnings.Clear();

        await RebuildAsync(nameof(LandedTally));

        var warning = _logger.Warnings.ShouldHaveSingleItem();

        warning.ShouldContain("fi_doc_tally");
        warning.ShouldContain(nameof(ReleasedTally));
        warning.ShouldContain(nameof(LandedTally));

        // The projection that shares nothing must not be dragged in.
        warning.ShouldNotContain(nameof(BoatLog));
    }

    /// <summary>
    ///     <b>Rebuilding each in turn does not restore both, and that is the sharp edge.</b>
    /// </summary>
    /// <remarks>
    ///     The obvious remedy — and the one fisher#122 and its reporter both assumed — is to rebuild
    ///     the sharing projections together. There is no such operation: every
    ///     <c>RebuildProjectionAsync</c> overload names one projection or one type, so "together"
    ///     means "one after the other", and the second rebuild's teardown clears the whole shared
    ///     table again, discarding what the first rebuild just wrote. The end state has only the
    ///     projection rebuilt last.
    /// </remarks>
    [Fact]
    public async Task rebuilding_each_in_turn_still_leaves_one_of_them_empty()
    {
        var (landed, released) = await SeedAsync();

        await RebuildAsync(nameof(LandedTally));
        await RebuildAsync(nameof(ReleasedTally));

        await using var after = _store.LightweightSession();

        (await after.LoadAsync<Tally>(released, Token)).ShouldNotBeNull().Count.ShouldBe(1);
        (await after.LoadAsync<Tally>(landed, Token)).ShouldBeNull();
    }

    /// <summary>
    ///     <b>A rewind is the remedy, because it replays without a teardown.</b>
    /// </summary>
    /// <remarks>
    ///     <c>RewindSubscriptionAsync</c> moves a projection's progression back and lets it replay onto
    ///     the rows that are there, where a rebuild clears first. That is exactly what the projection
    ///     collaterally emptied above needs, and it is what the warning should send an operator to —
    ///     "rebuild them together" is advice that cannot be followed.
    /// </remarks>
    [Fact]
    public async Task rewinding_the_collaterally_emptied_projection_restores_it()
    {
        var (landed, released) = await SeedAsync();

        await RebuildAsync(nameof(LandedTally));

        using (var daemon = await _store.BuildProjectionDaemonAsync(logger: _logger))
        {
            await daemon.StartAllAsync();
            await daemon.RewindSubscriptionAsync(nameof(ReleasedTally), Token, sequenceFloor: 0);
            await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(20));
        }

        await using var after = _store.LightweightSession();

        (await after.LoadAsync<Tally>(landed, Token)).ShouldNotBeNull().Count.ShouldBe(1);
        (await after.LoadAsync<Tally>(released, Token)).ShouldNotBeNull().Count.ShouldBe(1);
    }

    /// <summary>
    ///     A projection whose table nothing else publishes into says nothing. Without this the warning
    ///     could be unconditional and every test above would still pass.
    /// </summary>
    [Fact]
    public async Task a_projection_that_shares_no_table_does_not_warn()
    {
        await SeedAsync();
        _logger.Warnings.Clear();

        await RebuildAsync(nameof(BoatLog));

        _logger.Warnings.ShouldBeEmpty();
    }

    // ---- fixtures ----

    public record Landed(string Species);

    public record Released(string Species);

    public class Tally
    {
        public Guid Id { get; set; }
        public int Count { get; set; }
    }

    public class Boat
    {
        public Guid Id { get; set; }
        public int Trips { get; set; }
    }

    /// <summary>
    ///     Bare <c>IProjection</c>s over <c>ProjectionBase</c> rather than conventional
    ///     <c>EventProjection</c>s, so the dispatcher source generator is not involved and the
    ///     published type is declared outright — which is what <c>PublishedTableNamesFor</c> reads.
    /// </summary>
    public class LandedTally : ProjectionBase, Fisher.Projections.IProjection
    {
        public LandedTally()
        {
            Name = nameof(LandedTally);
            Options.DeleteViewTypeOnTeardown<Tally>();
        }

        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var e in events.Where(x => x.Data is Landed))
            {
                operations.Store(new Tally { Id = e.StreamId, Count = 1 });
            }

            return Task.CompletedTask;
        }
    }

    public class ReleasedTally : ProjectionBase, Fisher.Projections.IProjection
    {
        public ReleasedTally()
        {
            Name = nameof(ReleasedTally);
            Options.DeleteViewTypeOnTeardown<Tally>();
        }

        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var e in events.Where(x => x.Data is Released))
            {
                operations.Store(new Tally { Id = e.StreamId, Count = 1 });
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Publishes a type of its own, so the warning has a negative case.</summary>
    public class BoatLog : ProjectionBase, Fisher.Projections.IProjection
    {
        public BoatLog()
        {
            Name = nameof(BoatLog);
            Options.DeleteViewTypeOnTeardown<Boat>();
        }

        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var e in events.Where(x => x.Data is Landed))
            {
                operations.Store(new Boat { Id = e.StreamId, Trips = 1 });
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Captures warnings only. A real <c>ILoggerFactory</c> would work; this keeps the assertion
    ///     about the message rather than about logging infrastructure.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (Warnings)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}

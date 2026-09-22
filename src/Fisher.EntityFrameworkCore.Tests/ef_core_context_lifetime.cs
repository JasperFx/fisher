using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fisher.EntityFrameworkCore.Tests;

/// <summary>
///     Every <c>DbContext</c> Fisher creates for a projection is disposed, on every path (fisher#305,
///     the marten#5457 class).
/// </summary>
/// <remarks>
///     <para>
///         <b>The assertion is construction counted against disposal, not backends counted in the
///         database</b> — the choice marten#5466 made and the main thing worth carrying over. The
///         reported symptom over there was a leaked connection per batch, which only surfaces when the
///         context owns its connection; the defect underneath is present either way and pins
///         deterministically, with no pool timing and no sampling. Marten's first attempt counted
///         connections, passed on unfixed master, and proved nothing.
///     </para>
///     <para>
///         <b>Every test also asserts a context WAS built</b>, or it passes vacuously against a store
///         that created none — which is the other half of that lesson, and the half that is easy to
///         leave out.
///     </para>
///     <para>
///         Fisher diverges from Marten in two ways that are why this is a "confirm and pin" rather than
///         a port: <c>DbContextTransactionParticipant</c> is <c>IAsyncDisposable</c> and owns the
///         context in the moving-onto-Fisher's-connection mode, and registration is unconditional
///         rather than gated on <c>session is ITransactionParticipantRegistrar</c> — which was Marten's
///         second leak. What these tests establish is that the <em>owner</em> is actually drained, on
///         both the committed and the failed path, and on both the async and the inline lifecycle.
///     </para>
/// </remarks>
public class ef_core_context_lifetime : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-context-lifetime");
    private readonly ContextCounter _counter = new();
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "create table if not exists Tallies (Id text primary key, Members integer not null, "
            + "MonstersSlain integer not null)";
        await command.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        _database.Dispose();
    }

    private CountingTallyContext NewContext()
        => new(new DbContextOptionsBuilder<CountingTallyContext>()
            .UseSqlite(_database.ConnectionString).Options, _counter);

    private DocumentStore StoreWith(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    /// <summary>
    ///     The headline, on the storage-backed path: a daemon batch that commits disposes every context
    ///     it built.
    /// </summary>
    [Fact]
    public async Task an_async_batch_disposes_the_context_it_created()
    {
        await using var store = StoreWith(options =>
        {
            options.ProjectToEfCore<TallyEntity, Guid, CountingTallyContext>("Tallies", NewContext);
            options.Projections.Snapshot<TallyEntity>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<TallyEntity>(Guid.NewGuid(), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(Token);
        }

        _daemon = await store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await _daemon.StopAllAsync();

        _counter.Created.ShouldBeGreaterThan(0);
        _counter.Disposed.ShouldBe(_counter.Created);
    }

    /// <summary>
    ///     The same on the per-event path, which creates its context in <c>ApplyAsync</c> rather than in
    ///     the storage factory and deliberately does not dispose it there.
    /// </summary>
    [Fact]
    public async Task an_async_event_projection_disposes_the_context_it_created()
    {
        await using var store = StoreWith(options =>
        {
            options.Schema.For<AuditNote>();
            options.Projections.Add(new CountingAuditProjection(NewContext), ProjectionLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(Token);
        }

        _daemon = await store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await _daemon.StopAllAsync();

        _counter.Created.ShouldBeGreaterThan(0);
        _counter.Disposed.ShouldBe(_counter.Created);

        // ⚠️ And the entity actually landed, which is what makes this discriminating rather than just
        // a count. A fix that released participants on SESSION disposal satisfies the two assertions
        // above and destroys the context between the apply and the write — JasperFx's
        // ProjectionExecution takes the batch's session with `await using` and disposes it as soon as
        // the projection has been applied. The rows silently never appear, with no error anywhere.
        await using var context = NewContext();
        (await context.Tallies.CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     The failed path, which is the one marten#5228 had to add separately.
    /// </summary>
    /// <remarks>
    ///     A batch that never commits still has to release its contexts, or a persistently failing shard
    ///     leaks one per attempt — the failure mode that compounds rather than merely persisting.
    /// </remarks>
    [Fact]
    public async Task a_failed_projection_still_disposes_the_context()
    {
        await using var store = StoreWith(options =>
        {
            options.Schema.For<AuditNote>();
            options.Projections.Add(new ThrowingAuditProjection(NewContext), ProjectionLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(Token);
        }

        _daemon = await store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();

        // The shard pauses rather than recovering, so wait on the context having been built and let
        // the daemon's own shutdown be what ends the batch. Polling beats a fixed delay: a loaded host
        // makes this slower rather than wrong.
        await WaitUntilAsync(() => _counter.Created > 0);
        await _daemon.StopAllAsync();

        _counter.Created.ShouldBeGreaterThan(0);
        _counter.Disposed.ShouldBe(_counter.Created);
    }

    /// <summary>
    ///     The inline lifecycle, where the owner is a user's session rather than a projection batch.
    /// </summary>
    /// <remarks>
    ///     This is the path the source read behind fisher#305 did not reach. An inline projection runs
    ///     inside <c>FisherSession.SaveChangesAsync</c>, so there is no <c>FisherProjectionBatch</c> to
    ///     drain the participants it enlisted — the session has to do it itself.
    /// </remarks>
    [Fact]
    public async Task an_inline_event_projection_disposes_the_context()
    {
        await using var store = StoreWith(options =>
        {
            options.Schema.For<AuditNote>();
            options.Projections.Add(new CountingAuditProjection(NewContext), ProjectionLifecycle.Inline);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(Token);
        }

        _counter.Created.ShouldBeGreaterThan(0);
        _counter.Disposed.ShouldBe(_counter.Created);
    }

    /// <summary>
    ///     A session that enlists a participant and then fails its commit still releases it.
    /// </summary>
    [Fact]
    public async Task a_failed_inline_commit_still_disposes_the_context()
    {
        await using var store = StoreWith(options =>
        {
            options.Schema.For<AuditNote>();
            options.Projections.Add(new ThrowingAuditProjection(NewContext), ProjectionLifecycle.Inline);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new MemberJoined("Frodo"));

            await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));
        }

        _counter.Created.ShouldBeGreaterThan(0);
        _counter.Disposed.ShouldBe(_counter.Created);
    }

    /// <summary>
    ///     The ownership rule itself, asserted directly on the participant.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other side of every test above, and the one a blunt "dispose everything" fix would
    ///         break: a <c>DbContext</c> resolved from DI belongs to its scope, so the plain
    ///         constructor's participant leaves it alone however the batch ends. Only
    ///         <c>MovingOntoFishersConnection</c> hands Fisher ownership, and that is the mode the
    ///         EF-backed projection registrations use.
    ///     </para>
    ///     <para>
    ///         Asserted on the participant rather than through a session because a caller-supplied
    ///         context has to be on Fisher's own connection, which a test has no public way to obtain
    ///         before the session opens it. The rule under test is the <c>_context is null</c> /
    ///         <c>_movesOntoFishersConnection</c> pair, and this reaches it exactly.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task disposal_ownership_follows_who_created_the_context()
    {
        await using (var supplied = NewContext())
        {
            await new DbContextTransactionParticipant<CountingTallyContext>(supplied).DisposeAsync();

            _counter.Created.ShouldBe(1);
            _counter.Disposed.ShouldBe(0);
        }

        // The `await using` above is the caller doing what the caller is supposed to do.
        _counter.Disposed.ShouldBe(1);

        var owned = NewContext();
        await DbContextTransactionParticipant<CountingTallyContext>
            .MovingOntoFishersConnection(owned).DisposeAsync();

        _counter.Created.ShouldBe(2);
        _counter.Disposed.ShouldBe(2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Token);
        }
    }
}

/// <summary>
///     Counts construction against disposal. An instance rather than statics, because xUnit runs test
///     classes in parallel and a shared counter would make every assertion here depend on what else
///     was running.
/// </summary>
public class ContextCounter
{
    private int _created;
    private int _disposed;

    public int Created => Volatile.Read(ref _created);
    public int Disposed => Volatile.Read(ref _disposed);

    public void RecordCreated() => Interlocked.Increment(ref _created);
    public void RecordDisposed() => Interlocked.Increment(ref _disposed);
}

public class CountingTallyContext : DbContext
{
    private readonly ContextCounter _counter;
    private int _alreadyCounted;

    public CountingTallyContext(DbContextOptions<CountingTallyContext> options, ContextCounter counter)
        : base(options)
    {
        _counter = counter;
        counter.RecordCreated();
    }

    public DbSet<TallyEntity> Tallies => Set<TallyEntity>();

    public DbSet<AuditNote> AuditNotes => Set<AuditNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // AuditNote is a Fisher document, not an EF entity — it is on the context only so the two
        // halves of the dual-write projection compile against one type.
        modelBuilder.Ignore<AuditNote>();
        base.OnModelCreating(modelBuilder);
    }

    // Counted once however it is released: EF's DisposeAsync does not call Dispose, and a double
    // count would make the assertion pass against a context disposed twice and never against one
    // disposed correctly.
    public override void Dispose()
    {
        CountOnce();
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        CountOnce();
        return base.DisposeAsync();
    }

    private void CountOnce()
    {
        if (Interlocked.Exchange(ref _alreadyCounted, 1) == 0)
        {
            _counter.RecordDisposed();
        }
    }
}

public class CountingAuditProjection : EfCoreEventProjection<CountingTallyContext>
{
    public CountingAuditProjection(Func<CountingTallyContext> contextFactory) : base(contextFactory)
    {
        Name = nameof(CountingAuditProjection);
        IncludeType<MemberJoined>();
    }

    protected override Task ProjectAsync(IEvent @event, CountingTallyContext context,
        IDocumentOperations operations, CancellationToken token)
    {
        if (@event.Data is MemberJoined joined)
        {
            context.Tallies.Add(new TallyEntity { Id = Guid.NewGuid(), Members = 1 });
            operations.Store(new AuditNote { Id = Guid.NewGuid(), Name = joined.Name });
        }

        return Task.CompletedTask;
    }
}

/// <summary>
///     Enlists its context and then fails, so the batch ends without committing.
/// </summary>
public class ThrowingAuditProjection : EfCoreEventProjection<CountingTallyContext>
{
    public ThrowingAuditProjection(Func<CountingTallyContext> contextFactory) : base(contextFactory)
    {
        Name = nameof(ThrowingAuditProjection);
        IncludeType<MemberJoined>();
    }

    protected override Task ProjectAsync(IEvent @event, CountingTallyContext context,
        IDocumentOperations operations, CancellationToken token)
        => throw new InvalidOperationException("Deliberate failure from a projection under test.");
}

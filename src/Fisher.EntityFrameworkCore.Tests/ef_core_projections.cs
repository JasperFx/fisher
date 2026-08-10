using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore.Tests;

/// <summary>
///     fisher#50, half two — a projection whose documents are EF Core entities rather than rows in a
///     Fisher document table.
/// </summary>
/// <remarks>
///     <para>
///         <b>The registration is the whole seam</b>, which is the divergence from Polecat worth
///         checking here: the projection below is an ordinary
///         <c>SingleStreamProjection&lt;TDoc, TId&gt;</c> with conventional <c>Apply</c> methods and
///         knows nothing about EF. Polecat's equivalent has to derive from
///         <c>EfCoreSingleStreamProjection</c> to reach EF at all.
///     </para>
///     <para>
///         The entity's table is created by hand here rather than by an EF migration, because what is
///         under test is the storage and the transaction rather than the migration story — and Fisher
///         deliberately does not create it, since an entity's shape is the <c>DbContext</c>'s.
///     </para>
/// </remarks>
public class ef_core_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-projections");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // Before the projection, which is the ordering the registry checks.
            options.ProjectToEfCore<TallyEntity, Guid, TallyContext>("Tallies", ContextForReads);
            options.Projections.Snapshot<TallyEntity>(SnapshotLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

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

        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TallyContext ContextForReads()
        => new(new DbContextOptionsBuilder<TallyContext>().UseSqlite(_database.ConnectionString).Options);

    private async Task<Guid> AppendAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<TallyEntity>(streamId, events);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task RunDaemonAsync()
    {
        _daemon ??= await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
    }

    private async Task<List<TallyEntity>> TalliesAsync()
    {
        await using var context = ContextForReads();

        return await context.Tallies.OrderBy(x => x.Members).ToListAsync(Token);
    }

    /// <summary>
    ///     The headline: events fold into an EF entity, written by the daemon.
    /// </summary>
    [Fact]
    public async Task an_ef_backed_projection_is_written_by_the_daemon()
    {
        var streamId = await AppendAsync(new MemberJoined("Frodo"), new MemberJoined("Sam"),
            new MonsterSlain("Balrog"));

        await RunDaemonAsync();

        var tally = (await TalliesAsync()).ShouldHaveSingleItem();

        tally.Id.ShouldBe(streamId);
        tally.Members.ShouldBe(2);
        tally.MonstersSlain.ShouldBe(1);
    }

    /// <remarks>
    ///     A registered type must not also get a Fisher document table. Two homes for one projection is
    ///     the confusion the ordering check exists to prevent, and this is the half of it that would
    ///     otherwise be silent — the projection would work, and a stray empty table would sit in the
    ///     schema forever.
    /// </remarks>
    [Fact]
    public async Task an_ef_backed_type_gets_no_fisher_document_table()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table' and name like 'fi_doc%'";

        var tables = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            tables.Add(reader.GetString(0));
        }

        tables.ShouldBeEmpty();
    }

    /// <summary>
    ///     The entity write and the progression row commit together, or the projection would replay onto
    ///     rows it had already written.
    /// </summary>
    /// <remarks>
    ///     Probed the way fisher#4 pinned the outbox's hooks and fisher#50's half one re-probed them: the
    ///     entity is invisible over a separate connection until the batch commits. Asserted here through
    ///     the progression row instead, because the daemon owns the timing — a shard that reports
    ///     non-stale has committed, and both halves being in one transaction is what makes that mean the
    ///     entity is there.
    /// </remarks>
    [Fact]
    public async Task the_entity_and_the_progression_row_commit_together()
    {
        await AppendAsync(new MemberJoined("Frodo"));

        await RunDaemonAsync();

        var progress = await _store.Database.AllProjectionProgress(Token);
        var highest = await _store.Database.FetchHighestEventSequenceNumber(Token);

        progress.ShouldContain(x => x.ShardName.StartsWith("TallyEntity", StringComparison.Ordinal)
                                    && x.Sequence == highest);

        (await TalliesAsync()).ShouldHaveSingleItem().Members.ShouldBe(1);
    }

    /// <summary>
    ///     A rebuild clears the EF table first.
    /// </summary>
    /// <remarks>
    ///     <b>This is the flat-table lesson one layer over</b> — the sweep that finds a projection's
    ///     tables looks at <em>mapped</em> types, and a type stored in EF is deliberately not one, so
    ///     without the registry's table name a rebuild replays on top of the rows the previous run left.
    ///     Planted with a row the replay cannot recreate, which is the only shape that tells "cleared
    ///     and rebuilt" apart from "written over".
    /// </remarks>
    [Fact]
    public async Task a_rebuild_clears_the_ef_table_first()
    {
        await AppendAsync(new MemberJoined("Frodo"));
        await RunDaemonAsync();

        await using (var seed = ContextForReads())
        {
            seed.Tallies.Add(new TallyEntity { Id = Guid.NewGuid(), Members = 99 });
            await seed.SaveChangesAsync(Token);
        }

        (await TalliesAsync()).Count.ShouldBe(2);

        await _daemon!.RebuildProjectionAsync("TallyEntity", TimeSpan.FromSeconds(30), Token);

        // The planted row has no events behind it, so a rebuild that cleared the table cannot bring it
        // back and a rebuild that merely replayed would leave it.
        var tallies = await TalliesAsync();

        tallies.ShouldHaveSingleItem().Members.ShouldBe(1);
    }
}

/// <summary>
///     fisher#50 — the per-event shape, which is the one that needs a base class.
/// </summary>
/// <remarks>
///     An aggregation projection reaches EF by having its storage swapped, so it needs no base class at
///     all. A per-event projection decides for itself what each event means and has no storage
///     indirection, so the <c>DbContext</c> has to be handed to it.
/// </remarks>
public class ef_core_event_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-event-projection");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TallyContext ContextFor()
        => new(new DbContextOptionsBuilder<TallyContext>().UseSqlite(_database.ConnectionString).Options);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<AuditNote>();
            options.Projections.Add(new MemberAuditProjection(ContextFor), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

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

        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     Both sides of one event's projection — an EF entity and a Fisher document — in one
    ///     transaction.
    /// </summary>
    /// <remarks>
    ///     The property the whole package exists for, and the one that is impossible without it: two
    ///     transactions on one SQLite file are two writers, so one would wait or fail.
    /// </remarks>
    [Fact]
    public async Task an_event_projection_writes_to_ef_and_fisher_in_one_transaction()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new MemberJoined("Frodo"), new MemberJoined("Sam"));
            await session.SaveChangesAsync(Token);
        }

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        // EF's side.
        await using (var context = ContextFor())
        {
            (await context.Tallies.CountAsync(Token)).ShouldBe(2);
        }

        // And Fisher's, from the same batch.
        await using var query = _store.LightweightSession();

        // Fisher's ToListAsync by name, not EF's — both are extensions on IQueryable<T> and this file
        // imports both namespaces, so the unqualified call binds to EF's and fails at runtime asking
        // for IAsyncEnumerable.
        var notes = await Fisher.Linq.QueryableExtensions.ToListAsync(query.Query<AuditNote>(), Token);

        notes.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Frodo", "Sam"]);
    }
}

/// <remarks>
///     Writes an EF entity per member and a Fisher document beside it, which is what makes this the
///     dual-write shape rather than just another way to reach EF.
/// </remarks>
public class MemberAuditProjection : EfCoreEventProjection<TallyContext>
{
    public MemberAuditProjection(Func<TallyContext> contextFactory) : base(contextFactory)
        => IncludeType<MemberJoined>();

    protected override Task ProjectAsync(IEvent @event, TallyContext context, IDocumentOperations operations,
        CancellationToken token)
    {
        if (@event.Data is MemberJoined joined)
        {
            context.Tallies.Add(new TallyEntity { Id = Guid.NewGuid(), Members = 1 });
            operations.Store(new AuditNote { Id = Guid.NewGuid(), Name = joined.Name });
        }

        return Task.CompletedTask;
    }
}

public class AuditNote
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class TallyContext : DbContext
{
    public TallyContext(DbContextOptions<TallyContext> options) : base(options)
    {
    }

    public DbSet<TallyEntity> Tallies => Set<TallyEntity>();
}

public class TallyEntity
{
    public Guid Id { get; set; }
    public int Members { get; set; }
    public int MonstersSlain { get; set; }

    public void Apply(MemberJoined _) => Members++;

    public void Apply(MonsterSlain _) => MonstersSlain++;
}

public record MemberJoined(string Name);

public record MonsterSlain(string Name);

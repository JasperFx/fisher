using Fisher.Linq;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#665, the port of marten#6011 and marten#6070: <c>Advanced.QueryAcrossAllDatabasesAsync</c> over
///     a SQLite file per tenant.
/// </summary>
public class query_across_all_databases : IAsyncLifetime
{
    private static readonly string[] Tenants = ["red", "green", "blue"];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-query-all-" + Guid.NewGuid().ToString("n")[..8]);

    private DocumentStore theStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Three tenants with documents, and "empty", whose file has the tables and nothing in them
        theStore = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Customer>();
            options.MultiTenantedDatabases(x => x.InDirectory(_directory).AddTenants([.. Tenants, "empty"]));
        });

        await theStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var tenantNumber = 0;
        foreach (var tenant in Tenants)
        {
            tenantNumber++;
            await using var session = theStore.LightweightSession(tenant);
            for (var i = 0; i < tenantNumber; i++)
            {
                session.Store(new Customer { Name = $"{tenant}-{i}" });
            }

            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A tenant file still held by a pooled connection; it is under the temp path.
        }
    }

    [Fact]
    public async Task gathers_the_results_of_every_database()
    {
        #region sample_query_across_all_databases
        // One query session per tenant file, at most four at once, and the results flattened
        var customers = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Customer>().ToListAsync(ct),
            maxParallelism: 4,
            token: Token);
        #endregion

        customers.Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(["blue-0", "blue-1", "blue-2", "green-0", "green-1", "red-0"]);
    }

    [Fact]
    public async Task an_empty_database_adds_nothing_and_fails_nothing()
    {
        var counts = await theStore.Advanced.QueryAcrossAllDatabasesAsync<(string, int)>(
            async (session, database, ct) => [(database.Identifier, await session.Query<Customer>().CountAsync(ct))],
            token: Token);

        counts.ShouldContain(("empty", 0));
        counts.Sum(x => x.Item2).ShouldBe(6);
    }

    [Fact]
    public async Task keeps_each_databases_results_together()
    {
        var customers = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Customer>().ToListAsync(ct), 3, Token);

        // The names are "<tenant>-<n>": once a tenant's run ends, it must not come back
        var tenants = customers.Select(x => x.Name[..x.Name.LastIndexOf('-')]).ToList();
        var runs = tenants.Where((tenant, i) => i == 0 || tenants[i - 1] != tenant).ToList();
        runs.ShouldBe(runs.Distinct().ToList());
        runs.Count.ShouldBe(3);
    }

    [Fact]
    public async Task hands_each_database_to_the_query_once()
    {
        var seen = await theStore.Advanced.QueryAcrossAllDatabasesAsync<string>(
            (_, database, _) => Task.FromResult<IReadOnlyList<string>>([database.Identifier]), token: Token);

        seen.ShouldBe(theStore.Tenancy.AllDatabases().Select(x => x.Identifier), ignoreOrder: true);
        seen.Count.ShouldBe(4);
    }

    [Fact]
    public async Task tenant_ids_visit_only_the_databases_of_those_tenants()
    {
        var seen = await theStore.Advanced.QueryAcrossAllDatabasesAsync<string>(
            (_, database, _) => Task.FromResult<IReadOnlyList<string>>([database.Identifier]),
            8, Token, "green");

        seen.ShouldBe(["green"]);

        #region sample_query_across_some_tenants
        // Only the files holding green and blue are visited
        var customers = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Customer>().ToListAsync(ct),
            8, Token, "green", "blue");
        #endregion

        customers.Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(["blue-0", "blue-1", "blue-2", "green-0", "green-1"]);
    }

    [Fact]
    public async Task a_tenant_named_twice_is_read_once()
    {
        var customers = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Customer>().ToListAsync(ct),
            8, Token, "green", "green");

        customers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task never_runs_more_queries_at_once_than_the_limit()
    {
        var running = 0;
        var peak = 0;

        await theStore.Advanced.QueryAcrossAllDatabasesAsync<int>(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            interlockedMax(ref peak, now);
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref running);
            return [];
        }, 2, Token);

        peak.ShouldBe(2);
    }

    [Fact]
    public async Task a_failing_database_fails_the_whole_query()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await theStore.Advanced.QueryAcrossAllDatabasesAsync<int>(
                (_, _) => throw new InvalidOperationException("boom"), token: Token));
    }

    [Fact]
    public async Task rejects_a_parallelism_below_one()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await theStore.Advanced.QueryAcrossAllDatabasesAsync<int>(
                (_, _) => Task.FromResult<IReadOnlyList<int>>([]), 0, Token));
    }

    [Fact]
    public async Task an_unknown_tenant_id_is_refused_before_any_database_is_queried()
    {
        var queried = 0;

        var ex = await Should.ThrowAsync<UnknownTenantIdException>(async () =>
            await theStore.Advanced.QueryAcrossAllDatabasesAsync<Customer>((_, _) =>
            {
                Interlocked.Increment(ref queried);
                return Task.FromResult<IReadOnlyList<Customer>>([]);
            }, 8, Token, "red", "nobody"));

        ex.Message.ShouldContain("nobody");
        queried.ShouldBe(0);
    }

    [Fact]
    public async Task no_tenant_ids_is_an_empty_result()
    {
        var customers = await theStore.Advanced.QueryAcrossAllDatabasesAsync<Customer>(
            (_, _) => throw new InvalidOperationException("never runs"), 8, Token, []);

        customers.ShouldBeEmpty();
    }

    private static void interlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) break;
        }
    }

    public class Customer
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}

/// <summary>
///     Sharded tenancy: red and blue share one file, green has its own, and documents and events are conjoined.
///     A query across all databases reads every tenant and reads the shared file once; the tenant id overload
///     visits only the files that hold those tenants and reads only those tenants.
/// </summary>
public class query_across_all_sharded_databases : IAsyncLifetime
{
    private readonly TemporaryDatabase _shared = TemporaryDatabase.Create("query-all-shared");
    private readonly TemporaryDatabase _green = TemporaryDatabase.Create("query-all-green");

    private readonly Dictionary<string, Guid> _ids = new();
    private readonly Dictionary<string, Guid> _streams = new();

    private DocumentStore theStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.AddEventType<Greeted>();
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Schema.For<Member>();
            options.MultiTenantedDatabases(x => x
                .AddTenant("red", _shared.ConnectionString)
                .AddTenant("blue", _shared.ConnectionString)
                .AddTenant("green", _green.ConnectionString));
        });

        await theStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        foreach (var tenant in new[] { "red", "blue", "green" })
        {
            await using var session = theStore.LightweightSession(tenant);
            var member = new Member { Name = tenant };
            session.Store(member);
            _ids[tenant] = member.Id;
            _streams[tenant] = session.Events.StartStream(new Greeted(tenant)).Id;
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();
        _shared.Dispose();
        _green.Dispose();
    }

    private Task<IReadOnlyList<T>> queryAll<T>(Func<IQuerySession, CancellationToken, Task<IReadOnlyList<T>>> query)
        => theStore.Advanced.QueryAcrossAllDatabasesAsync(query, token: Token);

    private Task<IReadOnlyList<T>> queryTenants<T>(Func<IQuerySession, CancellationToken, Task<IReadOnlyList<T>>> query,
        params string[] tenantIds)
        => theStore.Advanced.QueryAcrossAllDatabasesAsync(query, 8, Token, tenantIds);

    [Fact]
    public async Task a_plain_query_reads_every_tenant_in_every_file_once()
    {
        var visited = 0;
        var members = await theStore.Advanced.QueryAcrossAllDatabasesAsync(async (session, _, ct) =>
        {
            Interlocked.Increment(ref visited);
            return await session.Query<Member>().ToListAsync(ct);
        }, token: Token);

        visited.ShouldBe(2);
        members.Select(x => x.Name).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);
    }

    [Fact]
    public async Task any_tenant_and_tenant_is_one_of_in_the_query_still_apply()
    {
        var any = await queryAll(async (session, ct) => await session.Query<Member>().AnyTenant().ToListAsync(ct));
        any.Select(x => x.Name).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);

        var some = await queryAll(async (session, ct) =>
            await session.Query<Member>().TenantIsOneOf("blue", "green").ToListAsync(ct));
        some.Select(x => x.Name).OrderBy(x => x).ShouldBe(["blue", "green"]);
    }

    [Fact]
    public async Task load_by_id_reads_every_tenant()
    {
        var one = await queryAll<Member>(async (session, ct) =>
            await session.LoadAsync<Member>(_ids["blue"], ct) is { } member ? [member] : []);
        one.Single().Name.ShouldBe("blue");

        var many = await queryAll(async (session, ct) => await session.LoadManyAsync<Member>(ct, [.. _ids.Values]));
        many.Select(x => x.Name).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);

        var json = await queryAll<string>(async (session, ct) =>
            await session.LoadJsonAsync<Member>(_ids["red"], ct) is { } found ? [found] : []);
        json.Single().ShouldContain("red");

        var exists = await queryAll<bool>(async (session, ct) =>
            [await session.CheckExistsAsync<Member>(_ids["blue"], ct)]);
        exists.Count(x => x).ShouldBe(1);
    }

    [Fact]
    public async Task event_reads_cover_every_tenant_and_keep_each_events_own_tenant()
    {
        var events = await queryAll(async (session, ct) =>
            await session.Events.QueryEventsAsync(x => x.Version == 1, ct));
        events.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);
        events.ShouldAllBe(x => ((Greeted)x.Data).Tenant == x.TenantId);

        var greeted = await queryAll(async (session, ct) =>
            await session.Events.QueryEventDataAsync<Greeted>(x => x.Tenant != "", ct));
        greeted.Select(x => x.Tenant).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);

        var paged = await queryAll(async (session, ct) =>
            (await session.Events.QueryEventsAsync(new EventQuery { PageSize = 10 }, ct)).Events);
        paged.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(["blue", "green", "red"]);

        var stream = await queryAll(async (session, ct) =>
            await session.Events.FetchStreamAsync(_streams["blue"], token: ct));
        stream.Single().TenantId.ShouldBe("blue");

        var aggregate = await queryAll<Greetings>(async (session, ct) =>
            await session.Events.AggregateStreamAsync<Greetings>(_streams["red"], token: ct) is { } found ? [found] : []);
        aggregate.Single().Tenants.ShouldBe(["red"]);
    }

    [Fact]
    public async Task tenant_ids_read_only_those_tenants_in_only_their_files()
    {
        var visited = 0;
        var members = await theStore.Advanced.QueryAcrossAllDatabasesAsync(async (session, _, ct) =>
        {
            Interlocked.Increment(ref visited);
            return await session.Query<Member>().ToListAsync(ct);
        }, 8, Token, "blue");

        visited.ShouldBe(1);
        members.Select(x => x.Name).ShouldBe(["blue"]);

        var loaded = await queryTenants(async (session, ct) =>
            await session.LoadManyAsync<Member>(ct, [.. _ids.Values]), "blue", "green");
        loaded.Select(x => x.Name).OrderBy(x => x).ShouldBe(["blue", "green"]);

        var red = await queryTenants<Member>(async (session, ct) =>
            await session.LoadAsync<Member>(_ids["red"], ct) is { } member ? [member] : [], "blue", "green");
        red.ShouldBeEmpty();

        var events = await queryTenants(async (session, ct) =>
            await session.Events.QueryEventsAsync(x => x.Version == 1, ct), "blue", "green");
        events.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(["blue", "green"]);

        var redStream = await queryTenants(async (session, ct) =>
            await session.Events.FetchStreamAsync(_streams["red"], token: ct), "blue", "green");
        redStream.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_for_tenant_scope_reads_its_own_tenant()
    {
        // The session reads blue in the shared file, and its ForTenant("red") scope reads red, not blue
        var members = await queryTenants(async (session, ct) =>
            await ((IDocumentSession)session).ForTenant("red").Query<Member>().ToListAsync(ct), "blue");

        members.Select(x => x.Name).ShouldBe(["red"]);
    }

    [Fact]
    public async Task ordinary_sessions_still_read_their_own_tenant()
    {
        await queryAll(async (session, ct) => await session.Query<Member>().ToListAsync(ct));

        await using var red = theStore.QuerySession("red");
        (await red.Query<Member>().ToListAsync(Token)).Select(x => x.Name).ShouldBe(["red"]);
        (await red.LoadAsync<Member>(_ids["blue"], Token)).ShouldBeNull();
        (await red.Events.FetchStreamAsync(_streams["blue"], token: Token)).ShouldBeEmpty();
    }

    public class Member
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public record Greeted(string Tenant);

    public class Greetings
    {
        public Guid Id { get; set; }
        public List<string> Tenants { get; set; } = [];

        public void Apply(Greeted e) => Tenants.Add(e.Tenant);
    }
}

/// <summary>
///     A single-database store answers from its one file, and with conjoined documents it reads every tenant in it.
/// </summary>
public class query_across_all_databases_of_a_single_database_store : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("query-all-single");
    private DocumentStore theStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Note>().MultiTenanted();
        });

        foreach (var tenant in new[] { "one", "two", "three" })
        {
            await using var session = theStore.LightweightSession(tenant);
            session.Store(new Note { Text = tenant });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task answers_from_its_one_file()
    {
        var visited = new List<FisherDatabase>();
        var notes = await theStore.Advanced.QueryAcrossAllDatabasesAsync(async (session, database, ct) =>
        {
            lock (visited) visited.Add(database);
            return await session.Query<Note>().ToListAsync(ct);
        }, token: Token);

        visited.ShouldBe([theStore.Database]);
        notes.Select(x => x.Text).OrderBy(x => x).ShouldBe(["one", "three", "two"]);
    }

    [Fact]
    public async Task tenant_ids_read_only_those_tenants()
    {
        var notes = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Note>().ToListAsync(ct), 8, Token, "one", "three");

        notes.Select(x => x.Text).OrderBy(x => x).ShouldBe(["one", "three"]);
    }

    public class Note
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
    }
}

/// <summary>
///     Tenants that appear at runtime under the directory convention: every file is read, and an unknown tenant id is
///     refused without creating its file.
/// </summary>
public class query_across_all_databases_in_a_directory : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-query-dir-" + Guid.NewGuid().ToString("n")[..8]);

    private DocumentStore theStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theStore = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Visit>();
            options.MultiTenantedDatabasesInDirectory(_directory);
        });

        foreach (var tenant in new[] { "north", "south" })
        {
            await using var session = theStore.LightweightSession(tenant);
            session.Store(new Visit { Place = tenant });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A tenant file still held by a pooled connection; it is under the temp path.
        }
    }

    [Fact]
    public async Task reads_every_tenant_file()
    {
        var visits = await theStore.Advanced.QueryAcrossAllDatabasesAsync(
            async (session, ct) => await session.Query<Visit>().ToListAsync(ct), token: Token);

        visits.Select(x => x.Place).OrderBy(x => x).ShouldBe(["north", "south"]);
    }

    [Fact]
    public async Task an_unknown_tenant_id_creates_no_file()
    {
        await Should.ThrowAsync<UnknownTenantIdException>(async () =>
            await theStore.Advanced.QueryAcrossAllDatabasesAsync<Visit>(
                (_, _) => Task.FromResult<IReadOnlyList<Visit>>([]), 8, Token, "north", "west"));

        Directory.GetFiles(_directory).Select(Path.GetFileNameWithoutExtension).ShouldNotContain("west");
    }

    public class Visit
    {
        public Guid Id { get; set; }
        public string Place { get; set; } = "";
    }
}

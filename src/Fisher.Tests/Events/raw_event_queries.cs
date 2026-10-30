// Deliberately NOT importing Fisher.Linq, for the reason stream_state_queries gives.
using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Linq;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     <c>IReadOnlyEventStore.QueryRawEvents</c> (fisher#480 / jasperfx#1094): the same Dynamic LINQ
///     <c>Where</c> and <c>OrderBy</c> text over <see cref="IEvent" /> metadata that the Marten and
///     Polecat tests run, against the same seeded events, expecting the same rows in the same order.
/// </summary>
/// <remarks>
///     Every refusal fact seeds data first, so it fails against a provider that matches everything.
/// </remarks>
public class raw_event_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("raw_event_queries");
    private DocumentStore _store = null!;
    private readonly Guid _one = Guid.NewGuid();
    private readonly Guid _two = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableCorrelationId = true;
            options.Events.EnableCausationId = true;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // One save per call, so sequences are 1..5 and alternate between the streams.
        await AppendAsync(_one, "c1", "k1", new QuestStarted("Find the ring"));
        await AppendAsync(_two, "c2", "k2", new QuestStarted("Guard the shire"));
        await AppendAsync(_one, "c1", "k3", new MemberJoined("Frodo"));
        await AppendAsync(_two, "c2", "k4", new MemberJoined("Sam"));
        await AppendAsync(_one, "c1", "k5", new MemberJoined("Gandalf"));
    }

    private async Task AppendAsync(Guid stream, string correlation, string causation, object @event)
    {
        await using var session = _store.LightweightSession();
        session.CorrelationId = correlation;
        session.CausationId = causation;
        session.Events.Append(stream, @event);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQueryable<IEvent> Events => ((IEventStore)_store).OpenReadOnlyEventStore().QueryRawEvents();

    [RequiresDynamicCode("Dynamic LINQ")]
    [RequiresUnreferencedCode("Dynamic LINQ")]
    private async Task<long[]> Sequences(string? where, string? orderBy = null, object?[]? args = null)
    {
        var query = DynamicQuery.Apply(Events, new DynamicQueryText(where, orderBy, args));
        return (await query.ToListAsync(Token)).Select(x => x.Sequence).ToArray();
    }

    // The shared parity shapes: text, expected sequences. Marten and Polecat assert these same rows.
    public static TheoryData<string> Shapes =>
    [
        "none", "sequence", "stream", "type", "correlation", "causation", "timestamp", "order-desc",
        "order-two-keys"
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    [RequiresDynamicCode("Dynamic LINQ")]
    [RequiresUnreferencedCode("Dynamic LINQ")]
    public async Task the_same_text_returns_the_same_rows(string shape)
    {
        var cutoff = (await Events.Where(x => x.Sequence == 3).ToListAsync(Token)).Single().Timestamp;

        var (actual, expected) = shape switch
        {
            "none" => (await Sequences(null), new long[] { 1, 2, 3, 4, 5 }),
            "sequence" => (await Sequences("Sequence > 1 and Sequence <= 4"), [2, 3, 4]),
            "stream" => (await Sequences("StreamId == @0", null, [_one]), [1, 3, 5]),
            "type" => (await Sequences("EventTypeName == \"member_joined\""), [3, 4, 5]),
            "correlation" => (await Sequences("CorrelationId == \"c2\""), [2, 4]),
            "causation" => (await Sequences("CausationId == \"k3\" or CausationId == \"k5\""), [3, 5]),
            "timestamp" => (await Sequences("Timestamp >= @0 and Sequence > 3", null, [cutoff]), [4, 5]),
            "order-desc" => (await Sequences(null, "Sequence desc"), [5, 4, 3, 2, 1]),
            "order-two-keys" => (await Sequences(null, "CorrelationId desc, Sequence"), [2, 4, 1, 3, 5]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task an_untranslatable_member_fails_the_query_naming_it_rather_than_matching_all()
    {
        (await Events.CountAsync(Token)).ShouldBe(5);

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(
            () => Events.Where(x => x.Data.ToString() == "x").ToListAsync(Token));
        exception.Message.ShouldNotBeNullOrEmpty();

        var named = await Should.ThrowAsync<BadLinqExpressionException>(
            () => Events.Where(x => x.Headers != null).ToListAsync(Token));
        named.Message.ShouldContain("Headers");
    }

    [Fact]
    public async Task the_shared_terminators_work()
    {
        (await Events.Where(x => x.Sequence > 2).CountAsync(Token)).ShouldBe(3);
        (await Events.AnyAsync(Token)).ShouldBeTrue();
        (await Events.OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(Token))!.Sequence.ShouldBe(5);
        (await Events.OrderBy(x => x.Sequence).Skip(1).Take(2).ToListAsync(Token)).Select(x => x.Sequence)
            .ShouldBe([2L, 3L]);
    }

    [Fact]
    public async Task a_tenant_is_refused_on_a_store_with_no_tenant_dimension()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => ((IEventStore)_store).OpenReadOnlyEventStore().QueryRawEvents("north"));

        exception.Message.ShouldContain("north");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task a_member_whose_column_is_not_enabled_is_refused_by_name()
    {
        using var database = TemporaryDatabase.Create("raw_event_queries_no_correlation");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("x"));
            await session.SaveChangesAsync(Token);
        }

        var events = ((IEventStore)store).OpenReadOnlyEventStore().QueryRawEvents();
        (await events.CountAsync(Token)).ShouldBe(1);

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(
            () => events.Where(x => x.CorrelationId == "c").ToListAsync(Token));
        exception.Message.ShouldContain("CorrelationId");
    }

    [Fact]
    public async Task a_tenant_scoped_read_returns_only_that_tenants_events_with_a_shared_stream_id()
    {
        using var database = TemporaryDatabase.Create("raw_event_queries_conjoined");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var shared = Guid.NewGuid();
        await Seed("north", new QuestStarted("n1"), new MemberJoined("n2"));
        await Seed("south", new QuestStarted("s1"));

        async Task Seed(string tenant, params object[] events)
        {
            await using var session = store.LightweightSession(tenant);
            session.Events.StartStream<Quest>(shared, events);
            await session.SaveChangesAsync(Token);
        }

        var reader = ((IEventStore)store).OpenReadOnlyEventStore();

        var north = await reader.QueryRawEvents("north").OrderBy(x => x.Sequence).ToListAsync(Token);
        north.Select(x => x.Sequence).ShouldBe([1L, 2L]);
        north.ShouldAllBe(x => x.TenantId == "north");

        var south = await reader.QueryRawEvents("south").ToListAsync(Token);
        south.Single().TenantId.ShouldBe("south");

        // The tier opened for a tenant inherits it.
        var viaTier = await ((IEventStore)store).OpenReadOnlyEventStore("south").QueryRawEvents()
            .ToListAsync(Token);
        viaTier.Select(x => x.Sequence).ShouldBe([3L]);
    }
}

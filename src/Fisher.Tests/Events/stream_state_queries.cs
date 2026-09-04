// Deliberately NOT importing the Fisher.Linq namespace here: its session-query terminators and the
// shared JasperFx.Events.Documents ones overlap by name, and a file using both namespaces cannot
// call either without qualifying. Stream-state queries execute through the shared hook, so the
// shared namespace is the one imported and the two Fisher.Linq types come in as aliases.
using System.Linq.Expressions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using BadLinqExpressionException = Fisher.Linq.BadLinqExpressionException;
using StreamStateMemberFactory = Fisher.Linq.Members.StreamStateMemberFactory;

namespace Fisher.Tests.Events;

/// <summary>
///     Fisher's own pins on <c>IReadOnlyEventStore.QueryStreamStates</c> (fisher#151 / jasperfx#740) —
///     the REFUSAL half of the contract, which the shared <c>StreamStateQueryCompliance</c> suite
///     deliberately cannot cover: both reference stores translate the full member set, so only a
///     store's own tests can prove that whatever falls outside it fails loudly instead of silently
///     matching every row.
/// </summary>
/// <remarks>
///     Every refusal test seeds data first and would therefore FAIL against a silently-match-all
///     provider even if the throw assertion were removed — the refusal and the "unfiltered streams
///     read as filtered" failure mode it exists to prevent are pinned together.
/// </remarks>
public class stream_state_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("stream_state_queries");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Find the ring"));
        session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Guard the shire"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQueryable<StreamState> Streams
        => ((IEventStore)_store).OpenReadOnlyEventStore().QueryStreamStates();

    /// <summary>
    ///     The tenant half of the jasperfx#740 contract: this store has no tenant dimension, so a
    ///     tenant-scoped read cannot be honored — and returning the unscoped streams would present
    ///     every tenant's data as one tenant's. Refused at the call, naming the tenant.
    /// </summary>
    [Fact]
    public void a_tenant_scope_on_a_tenantless_store_is_refused_not_unscoped()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => ((IEventStore)_store).OpenReadOnlyEventStore().QueryStreamStates("acme"));

        exception.Message.ShouldContain("acme");
        exception.Message.ShouldContain("Conjoined");
    }

    /// <summary>
    ///     The member-translation refusal names the member. Exercised at the factory seam because
    ///     <see cref="StreamState" /> currently has no public get member Fisher does not translate —
    ///     this is the arm that catches the NEXT member upstream adds, on the day it is added, rather
    ///     than silently matching all rows until someone notices.
    /// </summary>
    [Fact]
    public void an_untranslatable_member_is_refused_by_name()
    {
        var parameter = Expression.Parameter(typeof(NotStreamState), "x");
        var member = Expression.Property(parameter, nameof(NotStreamState.Bogus));

        var exception = Should.Throw<BadLinqExpressionException>(
            () => new StreamStateMemberFactory(_store.Options.EventGraph).ResolveMember(member));

        exception.Message.ShouldContain("Bogus");
        exception.Message.ShouldContain("CompactedVersion"); // the supported list is part of the message
    }

    /// <summary>
    ///     A predicate the provider cannot translate fails the QUERY, with rows demonstrably present —
    ///     the end-to-end shape of the same rule. A provider that dropped the clause would return the
    ///     two seeded streams here and fail this test before the assertion on the throw even ran.
    /// </summary>
    [Fact]
    public async Task an_untranslatable_predicate_fails_the_query_not_matches_all()
    {
        (await Streams.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        await Should.ThrowAsync<BadLinqExpressionException>(
            () => Streams.Where(x => x.GetHashCode() == 42)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task an_unsupported_operator_is_refused_by_name()
    {
        var exception = await Should.ThrowAsync<BadLinqExpressionException>(
            () => Streams.Distinct().ToListAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(nameof(Queryable.Distinct));
    }

    /// <summary>
    ///     A projection changes the element type, and this queryable answers <see cref="StreamState" />
    ///     rows only — refused when the query is composed, not at execution, so the failure points at
    ///     the Select that caused it.
    /// </summary>
    [Fact]
    public void a_projection_is_refused_when_composed()
    {
        Should.Throw<BadLinqExpressionException>(() => Streams.Select(x => x.Version))
            .Message.ShouldContain("StreamState");
    }

    /// <summary>
    ///     The stored aggregate-type identity is a simple-name alias, and names order alphabetically
    ///     rather than meaningfully — the same rule that refuses ordering a string-stored enum.
    ///     Equality (the compaction-policy selector) is the whole supported surface.
    /// </summary>
    [Fact]
    public async Task ordering_by_aggregate_type_is_refused()
    {
        var exception = await Should.ThrowAsync<BadLinqExpressionException>(
            () => Streams.OrderBy(x => x.AggregateType)
                .ToListAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(nameof(StreamState.AggregateType));
    }

    /// <summary>
    ///     Not a <see cref="StreamState" />, on purpose: the stand-in for a member the factory has no
    ///     arm for.
    /// </summary>
    private sealed class NotStreamState
    {
        public int Bogus { get; set; }
    }
}

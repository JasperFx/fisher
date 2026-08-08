using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Tenant scoping in the LINQ layer — fisher#51, and the <c>AnyTenant</c> / <c>TenantIsOneOf</c>
///     operators from fisher#26 that share its seam.
/// </summary>
/// <remarks>
///     <para>
///         fisher#51 was a cross-tenant read: the tenant filter was applied by wrapping each caller
///         predicate, so a query with no <c>Where</c> got no tenant term at all. Every shape below is
///         asserted in <b>both</b> directions, which is the discipline
///         <c>ConjoinedEventTenancyCompliance</c> applies to events and which a one-sided assertion
///         would not have caught — the tenant owning most of the data sees a correct-looking answer
///         with extras.
///     </para>
///     <para>
///         The shapes matter because each builds its statement through a different branch: no
///         predicate, one, two, a projection, a grouping, a <c>DistinctBy</c>, an aggregate and a count.
///     </para>
/// </remarks>
public class tenanted_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tenanted");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Note>().MultiTenanted();
            o.Schema.For<Memo>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await Write("alpha", "alpha-one", 1);
        await Write("alpha", "alpha-two", 2);
        await Write("beta", "beta-one", 30);
        await Write("gamma", "gamma-one", 100);
    }

    private async Task Write(string tenant, string text, int size)
    {
        await using var session = _store.LightweightSession(tenant);
        session.Store(new Note { Id = Guid.NewGuid(), Text = text, Size = size });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IQuerySession For(string tenant) => _store.LightweightSession(tenant);

    // ---- fisher#51: every query shape is scoped, in both directions ----

    [Theory]
    [InlineData("alpha", 2)]
    [InlineData("beta", 1)]
    [InlineData("gamma", 1)]
    public async Task a_query_with_no_where_is_scoped(string tenant, int expected)
    {
        await using var session = For(tenant);

        (await session.Query<Note>().ToListAsync(Token)).Count.ShouldBe(expected);
    }

    [Theory]
    [InlineData("alpha", 2)]
    [InlineData("beta", 1)]
    public async Task a_query_with_one_and_two_predicates_is_scoped(string tenant, int expected)
    {
        await using var session = For(tenant);

        (await session.Query<Note>().Where(x => x.Size > 0).ToListAsync(Token)).Count.ShouldBe(expected);

        (await session.Query<Note>().Where(x => x.Size > 0).Where(x => x.Text != "")
            .ToListAsync(Token)).Count.ShouldBe(expected);
    }

    [Theory]
    [InlineData("alpha", 2)]
    [InlineData("beta", 1)]
    public async Task the_other_statement_shapes_are_scoped(string tenant, int expected)
    {
        await using var session = For(tenant);

        (await session.Query<Note>().CountAsync(Token)).ShouldBe(expected);
        (await session.Query<Note>().Select(x => x.Text).ToListAsync(Token)).Count.ShouldBe(expected);
        (await session.Query<Note>().DistinctBy(x => x.Id).ToListAsync(Token)).Count.ShouldBe(expected);

        (await session.Query<Note>().GroupBy(x => x.Text).Select(g => g.Key)
            .ToListAsync(Token)).Count.ShouldBe(expected);

        (await session.Query<Note>().OrderBy(x => x.Size).Take(10)
            .ToListAsync(Token)).Count.ShouldBe(expected);
    }

    [Fact]
    public async Task an_aggregate_is_scoped()
    {
        await using var alpha = For("alpha");
        (await alpha.Query<Note>().SumAsync(x => x.Size, Token)).ShouldBe(3);

        await using var beta = For("beta");
        (await beta.Query<Note>().SumAsync(x => x.Size, Token)).ShouldBe(30);
    }

    // ---- fisher#26: the operators that change the scope ----

    [Fact]
    public async Task any_tenant_sees_every_tenants_rows()
    {
        await using var session = For("alpha");

        (await session.Query<Note>().AnyTenant().ToListAsync(Token)).Count.ShouldBe(4);
        (await session.Query<Note>().AnyTenant().SumAsync(x => x.Size, Token)).ShouldBe(133);
    }

    [Fact]
    public async Task tenant_is_one_of_names_the_tenants()
    {
        await using var session = For("alpha");

        (await session.Query<Note>().TenantIsOneOf("beta", "gamma")
            .ToListAsync(Token)).Select(x => x.Text).OrderBy(x => x)
            .ShouldBe(["beta-one", "gamma-one"]);

        // The session's own tenant has no special standing once the scope is named.
        (await session.Query<Note>().TenantIsOneOf("gamma").ToListAsync(Token))
            .ShouldHaveSingleItem().Text.ShouldBe("gamma-one");
    }

    [Fact]
    public async Task tenant_is_one_of_naming_nothing_matches_nothing()
    {
        await using var session = For("alpha");

        (await session.Query<Note>().TenantIsOneOf().ToListAsync(Token)).ShouldBeEmpty();
    }

    /// <summary>
    ///     There is no <c>tenant_id</c> column to have an opinion about, so silently doing nothing
    ///     would look like it worked — the same rule the soft-delete operators follow.
    /// </summary>
    [Fact]
    public async Task the_tenant_operators_are_refused_against_a_single_tenant_type()
    {
        await using var session = For("alpha");

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Memo>().AnyTenant().ToListAsync(Token)))
            .Message.ShouldContain("not multi-tenanted");

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Memo>().TenantIsOneOf("beta").ToListAsync(Token));
    }

    public class Note
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public int Size { get; set; }
    }

    public class Memo
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
    }
}

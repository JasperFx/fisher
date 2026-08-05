using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     <c>session.Query&lt;T&gt;()</c> end to end, against real stored documents.
/// </summary>
public class querying_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("query");
    private DocumentStore _store = null!;
    private readonly Guid _frodoId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Explorer>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Explorer
        {
            Id = _frodoId, Name = "Frodo", Age = 33, Active = true,
            Grade = Grade.HighDistinction, Home = new Address { City = "Bag End" }
        });
        session.Store(new Explorer
        {
            Id = Guid.NewGuid(), Name = "Samwise", Age = 38, Active = true,
            Grade = Grade.Pass, Home = new Address { City = "Bag End" }
        });
        session.Store(new Explorer
        {
            Id = Guid.NewGuid(), Name = "Merry", Age = 36, Active = false,
            Grade = Grade.Pass, Home = new Address { City = "Buckland" }
        });
        session.Store(new Explorer
        {
            Id = Guid.NewGuid(), Name = "Pippin", Age = 28, Active = false,
            Grade = Grade.Pass, Home = new Address { City = "Tuckborough" }
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQuerySession Session() => _store.LightweightSession();

    [Fact]
    public async Task an_unfiltered_query_returns_everything()
    {
        await using var session = Session();

        var all = await session.Query<Explorer>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(4);
    }

    [Fact]
    public async Task a_where_clause_filters()
    {
        await using var session = Session();

        var results = await session.Query<Explorer>()
            .Where(x => x.Age > 34)
            .ToListAsync(TestContext.Current.CancellationToken);

        results.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Merry", "Samwise"]);
    }

    /// <summary>
    ///     Documents come back fully materialised through the same query-only selector
    ///     <c>LoadAsync</c> uses, so nested members survive the round trip.
    /// </summary>
    [Fact]
    public async Task results_are_fully_deserialized()
    {
        await using var session = Session();

        var frodo = await session.Query<Explorer>()
            .Where(x => x.Name == "Frodo")
            .SingleAsync(TestContext.Current.CancellationToken);

        frodo.ShouldNotBeNull();
        frodo.Id.ShouldBe(_frodoId);
        frodo.Age.ShouldBe(33);
        frodo.Active.ShouldBeTrue();
        frodo.Grade.ShouldBe(Grade.HighDistinction);
        frodo.Home.City.ShouldBe("Bag End");
    }

    [Fact]
    public async Task stacked_where_clauses_are_anded()
    {
        await using var session = Session();

        var results = await session.Query<Explorer>()
            .Where(x => x.Age > 30)
            .Where(x => x.Active)
            .ToListAsync(TestContext.Current.CancellationToken);

        results.Select(x => x.Name).OrderBy(x => x).ShouldBe(["Frodo", "Samwise"]);
    }

    [Fact]
    public async Task ordering_ascending_and_descending()
    {
        await using var session = Session();

        (await session.Query<Explorer>().OrderBy(x => x.Age)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Pippin", "Frodo", "Merry", "Samwise"]);

        (await session.Query<Explorer>().OrderByDescending(x => x.Age)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Samwise", "Merry", "Frodo", "Pippin"]);
    }

    [Fact]
    public async Task then_by_refines_the_primary_ordering()
    {
        await using var session = Session();

        var results = await session.Query<Explorer>()
            .OrderBy(x => x.Home.City)
            .ThenByDescending(x => x.Age)
            .ToListAsync(TestContext.Current.CancellationToken);

        results.Select(x => x.Name).ShouldBe(["Samwise", "Frodo", "Merry", "Pippin"]);
    }

    [Fact]
    public async Task take_and_skip_page_the_result()
    {
        await using var session = Session();

        var page = await session.Query<Explorer>()
            .OrderBy(x => x.Age)
            .Skip(1)
            .Take(2)
            .ToListAsync(TestContext.Current.CancellationToken);

        page.Select(x => x.Name).ShouldBe(["Frodo", "Merry"]);
    }

    /// <summary>
    ///     Skip with no Take is the case SQLite needs <c>limit -1</c> for.
    /// </summary>
    [Fact]
    public async Task skip_without_take_returns_the_rest()
    {
        await using var session = Session();

        var rest = await session.Query<Explorer>()
            .OrderBy(x => x.Age)
            .Skip(2)
            .ToListAsync(TestContext.Current.CancellationToken);

        rest.Select(x => x.Name).ShouldBe(["Merry", "Samwise"]);
    }

    [Fact]
    public async Task counting_with_and_without_a_filter()
    {
        await using var session = Session();

        (await session.Query<Explorer>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(4);
        (await session.Query<Explorer>().Where(x => x.Active)
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        (await session.Query<Explorer>().LongCountAsync(TestContext.Current.CancellationToken)).ShouldBe(4L);
    }

    /// <summary>
    ///     A count over a paged query counts the page. Without the subquery wrapper the paging is
    ///     discarded and this reports the whole table.
    /// </summary>
    [Fact]
    public async Task counting_a_paged_query_counts_the_page()
    {
        await using var session = Session();

        (await session.Query<Explorer>().Take(2).CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        (await session.Query<Explorer>().Skip(3).CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await session.Query<Explorer>().Skip(1).Take(2)
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task any_reports_existence()
    {
        await using var session = Session();

        (await session.Query<Explorer>().Where(x => x.Age > 100)
            .AnyAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await session.Query<Explorer>().Where(x => x.Age > 30)
            .AnyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task first_or_default_returns_null_when_nothing_matches()
    {
        await using var session = Session();

        (await session.Query<Explorer>().Where(x => x.Name == "Gollum")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task first_honours_the_ordering()
    {
        await using var session = Session();

        var youngest = await session.Query<Explorer>()
            .OrderBy(x => x.Age)
            .FirstAsync(TestContext.Current.CancellationToken);

        youngest!.Name.ShouldBe("Pippin");
    }

    [Fact]
    public async Task first_on_an_empty_result_throws()
    {
        await using var session = Session();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.Query<Explorer>().Where(x => x.Name == "Gollum")
                .FirstAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The difference between Single and First: a second match is an error. The statement asks for
    ///     two rows so the second one is detectable without another round trip.
    /// </summary>
    [Fact]
    public async Task single_throws_when_more_than_one_matches()
    {
        await using var session = Session();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.Query<Explorer>().Where(x => x.Active)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task a_terminal_predicate_filters_like_a_where()
    {
        await using var session = Session();

        var merry = await session.Query<Explorer>()
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        merry.ShouldNotBeNull();

        (await session.Query<Explorer>().Where(x => x.Name == "Merry")
            .SingleAsync(TestContext.Current.CancellationToken))!.Age.ShouldBe(36);
    }

    [Fact]
    public async Task string_methods_translate()
    {
        await using var session = Session();

        (await session.Query<Explorer>().Where(x => x.Name.StartsWith("Sam"))
            .ToListAsync(TestContext.Current.CancellationToken))
            .Single().Name.ShouldBe("Samwise");

        (await session.Query<Explorer>().Where(x => x.Home.City.Contains("Bag"))
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task an_in_clause_over_ids_matches()
    {
        await using var session = Session();
        var ids = new[] { _frodoId };

        var results = await session.Query<Explorer>()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(TestContext.Current.CancellationToken);

        results.Single().Name.ShouldBe("Frodo");
    }

    /// <summary>
    ///     A query inside a unit of work runs on the session's own connection, so it sees writes that
    ///     session has already committed.
    /// </summary>
    [Fact]
    public async Task a_query_sees_documents_the_same_session_saved()
    {
        await using var session = _store.LightweightSession();

        session.Store(new Explorer { Id = Guid.NewGuid(), Name = "Bilbo", Age = 111 });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bilbo = await session.Query<Explorer>()
            .Where(x => x.Name == "Bilbo")
            .SingleAsync(TestContext.Current.CancellationToken);

        bilbo!.Age.ShouldBe(111);
    }

    /// <summary>
    ///     Refused rather than silently evaluated in memory — pulling the table down to satisfy a
    ///     foreach is not help.
    /// </summary>
    [Fact]
    public async Task synchronous_enumeration_is_refused()
    {
        await using var session = Session();

        var ex = Should.Throw<NotSupportedException>(() => session.Query<Explorer>().ToList());

        ex.Message.ShouldContain("ToListAsync");
    }

    [Fact]
    public async Task an_untranslatable_operator_names_itself()
    {
        await using var session = Session();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Explorer>().Distinct().ToListAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("Distinct");
    }

    /// <summary>
    ///     Ordering by a date is refused for the same reason range comparison is: the stored text does
    ///     not order by instant.
    /// </summary>
    [Fact]
    public async Task ordering_by_a_date_is_refused()
    {
        await using var session = Session();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(async () =>
            await session.Query<Explorer>().OrderBy(x => x.JoinedAt)
                .ToListAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("order-preserving");
    }
}

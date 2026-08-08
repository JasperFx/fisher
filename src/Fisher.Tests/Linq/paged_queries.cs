using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     Offset paging — fisher#27.
/// </summary>
public class paged_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("paging");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Catch>();
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        for (var i = 1; i <= 7; i++)
        {
            session.Store(new Catch { Id = Guid.NewGuid(), Weight = i, Species = i % 2 == 0 ? "Pike" : "Trout" });
        }
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IQuerySession Session() => _store.LightweightSession();

    [Fact]
    public async Task a_page_reports_its_place_in_the_whole()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().OrderBy(x => x.Weight)
            .ToPagedListAsync(2, 3, Token);

        page.Select(x => x.Weight).ShouldBe([4, 5, 6]);
        page.TotalItemCount.ShouldBe(7);
        page.PageCount.ShouldBe(3);
        page.PageNumber.ShouldBe(2);
        page.PageSize.ShouldBe(3);
        page.HasPreviousPage.ShouldBeTrue();
        page.HasNextPage.ShouldBeTrue();
        page.IsFirstPage.ShouldBeFalse();
        page.IsLastPage.ShouldBeFalse();
        page.FirstItemOnPage.ShouldBe(4);
        page.LastItemOnPage.ShouldBe(6);
    }

    [Fact]
    public async Task the_first_and_last_pages()
    {
        await using var session = Session();

        var first = await session.Query<Catch>().OrderBy(x => x.Weight).ToPagedListAsync(1, 3, Token);
        first.IsFirstPage.ShouldBeTrue();
        first.HasPreviousPage.ShouldBeFalse();
        first.FirstItemOnPage.ShouldBe(1);

        var last = await session.Query<Catch>().OrderBy(x => x.Weight).ToPagedListAsync(3, 3, Token);
        last.Count.ShouldBe(1);
        last.IsLastPage.ShouldBeTrue();
        last.HasNextPage.ShouldBeFalse();
        last.LastItemOnPage.ShouldBe(7);
    }

    /// <summary>
    ///     The case a pager needs the total for most. A window function would return no row at all
    ///     here, which is why the count is a second statement.
    /// </summary>
    [Fact]
    public async Task a_page_past_the_end_still_reports_the_total()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().OrderBy(x => x.Weight).ToPagedListAsync(9, 3, Token);

        page.ShouldBeEmpty();
        page.TotalItemCount.ShouldBe(7);
        page.PageCount.ShouldBe(3);
        page.FirstItemOnPage.ShouldBe(0);
        page.LastItemOnPage.ShouldBe(0);
    }

    [Fact]
    public async Task paging_respects_a_where()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().Where(x => x.Species == "Trout")
            .OrderBy(x => x.Weight).ToPagedListAsync(1, 2, Token);

        page.Select(x => x.Weight).ShouldBe([1, 3]);
        page.TotalItemCount.ShouldBe(4);
    }

    /// <summary>
    ///     The total ignores paging already on the query — a total that counted the page would say
    ///     nothing. So an existing <c>Take</c>/<c>Skip</c> is replaced rather than composed with.
    /// </summary>
    [Fact]
    public async Task the_total_ignores_paging_already_on_the_query()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().OrderBy(x => x.Weight).Take(2)
            .ToPagedListAsync(1, 3, Token);

        page.Count.ShouldBe(3);
        page.TotalItemCount.ShouldBe(7);
    }

    [Fact]
    public async Task paging_a_projection()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().OrderBy(x => x.Weight).Select(x => x.Weight)
            .ToPagedListAsync(2, 3, Token);

        page.ShouldBe([4, 5, 6]);
        page.TotalItemCount.ShouldBe(7);
    }

    [Fact]
    public async Task an_empty_result_is_a_page_of_nothing()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().Where(x => x.Species == "Barracuda")
            .OrderBy(x => x.Weight).ToPagedListAsync(1, 3, Token);

        page.ShouldBeEmpty();
        page.TotalItemCount.ShouldBe(0);
        page.PageCount.ShouldBe(0);
        page.IsLastPage.ShouldBeTrue();
        page.HasNextPage.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    public async Task a_nonsensical_page_is_refused(int pageNumber, int pageSize)
    {
        await using var session = Session();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            session.Query<Catch>().OrderBy(x => x.Weight).ToPagedListAsync(pageNumber, pageSize, Token));
    }

    // ---- keyset paging ----

    /// <summary>
    ///     A full walk with a page size that does not divide the set, so the last page is short and
    ///     the cursor has to stop.
    /// </summary>
    [Fact]
    public async Task a_cursor_walk_covers_every_row_exactly_once()
    {
        await using var session = Session();

        var seen = new List<int>();
        string? cursor = null;

        do
        {
            var page = await session.Query<Catch>().OrderBy(x => x.Weight).ThenBy(x => x.Id)
                .ToCursorPageAsync(3, cursor, Token);

            seen.AddRange(page.Items.Select(x => x.Weight));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.ShouldBe([1, 2, 3, 4, 5, 6, 7]);
    }

    /// <summary>
    ///     Ties on the sort key are the case keyset paging gets wrong without a total order — which is
    ///     why the terminal key must be the identity. Every row here shares a species.
    /// </summary>
    [Fact]
    public async Task a_cursor_walk_over_a_tied_sort_key()
    {
        await using var session = Session();

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var page = await session.Query<Catch>().OrderBy(x => x.Species).ThenBy(x => x.Id)
                .ToCursorPageAsync(2, cursor, Token);

            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.Count.ShouldBe(7);
        seen.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public async Task a_descending_cursor_walk()
    {
        await using var session = Session();

        var seen = new List<int>();
        string? cursor = null;

        do
        {
            var page = await session.Query<Catch>().OrderByDescending(x => x.Weight).ThenBy(x => x.Id)
                .ToCursorPageAsync(3, cursor, Token);

            seen.AddRange(page.Items.Select(x => x.Weight));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.ShouldBe([7, 6, 5, 4, 3, 2, 1]);
    }

    [Fact]
    public async Task a_cursor_page_respects_a_where()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().Where(x => x.Species == "Trout")
            .OrderBy(x => x.Weight).ThenBy(x => x.Id).ToCursorPageAsync(10, null, Token);

        page.Items.Select(x => x.Weight).ShouldBe([1, 3, 5, 7]);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task an_ordering_without_a_terminal_identity_is_refused_by_name()
    {
        await using var session = Session();

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().OrderBy(x => x.Weight).ToCursorPageAsync(3, null, Token)))
            .Message.ShouldContain("total order");

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().ToCursorPageAsync(3, null, Token));
    }

    [Fact]
    public async Task a_cursor_from_a_different_ordering_is_refused()
    {
        await using var session = Session();

        var page = await session.Query<Catch>().OrderBy(x => x.Weight).ThenBy(x => x.Id)
            .ToCursorPageAsync(2, null, Token);

        await Should.ThrowAsync<ArgumentException>(() =>
            session.Query<Catch>().OrderBy(x => x.Id).ToCursorPageAsync(2, page.NextCursor, Token));
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("v1:!!!!")]
    public async Task a_malformed_cursor_is_refused(string cursor)
    {
        await using var session = Session();

        await Should.ThrowAsync<ArgumentException>(() =>
            session.Query<Catch>().OrderBy(x => x.Weight).ThenBy(x => x.Id)
                .ToCursorPageAsync(2, cursor, Token));
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public int Weight { get; set; }
        public string Species { get; set; } = "";
    }
}

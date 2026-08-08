using System.Text;
using System.Text.Json;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     Reads that hand back stored JSON — fisher#28.
/// </summary>
/// <remarks>
///     The property worth pinning is byte-exactness. <c>data</c> is TEXT holding what
///     System.Text.Json wrote, so nothing normalises whitespace or key order on the way out — a
///     stronger guarantee than either sibling can make, and the whole reason these operators are worth
///     more on an embedded store than on a client-server one.
/// </remarks>
public class json_reads : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("json-reads");
    private DocumentStore _store = null!;
    private readonly Guid _frodo = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Angler>().SoftDeleted();
            o.Schema.For<Versioned>().UseOptimisticConcurrency();
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Angler { Id = _frodo, Name = "Frodo", Catches = 3 });
        session.Store(new Angler { Id = Guid.NewGuid(), Name = "Sam", Catches = 9 });
        session.Store(new Versioned { Id = Guid.NewGuid(), Label = "one" });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IDocumentSession Session() => _store.LightweightSession();

    [Fact]
    public async Task load_json_is_byte_exact_against_what_the_serializer_wrote()
    {
        await using var session = Session();

        var json = await session.LoadJsonAsync<Angler>(_frodo, Token);

        json.ShouldBe(_store.Options.Serializer.ToJson(
            new Angler { Id = _frodo, Name = "Frodo", Catches = 3 }));
    }

    [Fact]
    public async Task load_json_for_an_absent_document()
    {
        await using var session = Session();

        (await session.LoadJsonAsync<Angler>(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    /// <summary>
    ///     It goes through the LINQ path, so the soft-delete filter applies without being restated —
    ///     the property that keeps a JSON read from resurrecting a deleted document.
    /// </summary>
    [Fact]
    public async Task load_json_does_not_see_a_soft_deleted_document()
    {
        await using (var session = Session())
        {
            session.Delete<Angler>(_frodo);
            await session.SaveChangesAsync(Token);
        }

        await using var check = Session();
        (await check.LoadJsonAsync<Angler>(_frodo, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task to_json_array()
    {
        await using var session = Session();

        var json = await session.Query<Angler>().OrderBy(x => x.Name).ToJsonArrayAsync(Token);

        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement.EnumerateArray().Select(x => x.GetProperty("name").GetString())
            .ShouldBe(["Frodo", "Sam"]);
    }

    [Fact]
    public async Task to_json_array_over_no_rows_is_an_empty_array()
    {
        await using var session = Session();

        (await session.Query<Angler>().Where(x => x.Name == "Gandalf").ToJsonArrayAsync(Token))
            .ShouldBe("[]");
    }

    [Fact]
    public async Task to_json_first_with_version()
    {
        await using var session = Session();

        var result = await session.Query<Versioned>().ToJsonFirstWithVersionAsync(Token);

        result.ShouldNotBeNull();
        result.Version.ShouldNotBe(Guid.Empty);
        JsonDocument.Parse(result.Json).RootElement.GetProperty("label").GetString().ShouldBe("one");

        (await session.Query<Versioned>().Where(x => x.Label == "nope")
            .ToJsonFirstWithVersionAsync(Token)).ShouldBeNull();
    }

    /// <summary>
    ///     <c>guid_version</c> only exists for a type with optimistic concurrency, so there is no
    ///     version to report for any other — said plainly rather than surfacing as "no such column".
    /// </summary>
    [Fact]
    public async Task the_version_variant_is_refused_without_the_column()
    {
        await using var session = Session();

        (await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<Angler>().ToJsonFirstWithVersionAsync(Token)))
            .Message.ShouldContain("UseOptimisticConcurrency");
    }

    [Fact]
    public async Task streaming_writes_the_same_array()
    {
        await using var session = Session();

        using var buffer = new MemoryStream();
        await session.Query<Angler>().OrderBy(x => x.Name).StreamJsonArrayAsync(buffer, Token);

        Encoding.UTF8.GetString(buffer.ToArray())
            .ShouldBe(await session.Query<Angler>().OrderBy(x => x.Name).ToJsonArrayAsync(Token));
    }

    [Fact]
    public async Task a_json_read_after_a_select_is_refused_by_name()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Angler>().Select(x => x.Name).ToJsonArrayAsync(Token));
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Catches { get; set; }
    }

    public class Versioned
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }
}

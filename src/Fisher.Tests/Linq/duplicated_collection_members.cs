using Fisher.Linq;
using Fisher.Attributes;
using JasperFx;

namespace Fisher.Tests.Linq;

public class TallySheet
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string[] Names { get; set; } = [];

    [DuplicateField]
    public int[] Numbers { get; set; } = [];

    public List<string> Labels { get; set; } = [];
}

/// <summary>
///     fisher#637, related to marten#5934. A duplicated collection member resolved to the duplicated
///     column instead of a collection, so <c>Contains()</c> and <c>Any()</c> over it refused to translate,
///     while the same operators over an array that isn't duplicated worked.
/// </summary>
public class duplicated_collection_members : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("duplicated-collections");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<TallySheet>().Duplicate(x => x.Labels);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new TallySheet { Id = Guid.NewGuid(), Name = "one", Names = ["xy"], Numbers = [1, 2, 3], Labels = ["red", "blue"] });
        session.Store(new TallySheet { Id = Guid.NewGuid(), Name = "two", Numbers = [4, 5], Labels = ["green"] });
        session.Store(new TallySheet { Id = Guid.NewGuid(), Name = "three", Numbers = [], Labels = [] });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<string[]> NamesWhere(Func<IQueryable<TallySheet>, IQueryable<TallySheet>> filter)
    {
        await using var query = _store.QuerySession();
        var sheets = await filter(query.Query<TallySheet>()).ToListAsync(Token);
        return sheets.Select(x => x.Name).Order().ToArray();
    }

    [Fact]
    public async Task contains_over_a_duplicated_array()
    {
        (await NamesWhere(q => q.Where(x => x.Numbers.Contains(3)))).ShouldBe(["one"]);
        (await NamesWhere(q => q.Where(x => x.Labels.Contains("green")))).ShouldBe(["two"]);
    }

    [Fact]
    public async Task any_and_count_over_a_duplicated_array()
    {
        (await NamesWhere(q => q.Where(x => x.Numbers.Any()))).ShouldBe(["one", "two"]);
        (await NamesWhere(q => q.Where(x => x.Labels.Any()))).ShouldBe(["one", "two"]);
        (await NamesWhere(q => q.Where(x => !x.Numbers.Any()))).ShouldBe(["three"]);
        (await NamesWhere(q => q.Where(x => x.Numbers.Count() == 2))).ShouldBe(["two"]);
        (await NamesWhere(q => q.Where(x => x.Labels.Count == 0))).ShouldBe(["three"]);
    }

    [Fact]
    public async Task selecting_a_duplicated_array_still_works()
    {
        await using var query = _store.QuerySession();
        var numbers = await query.Query<TallySheet>().Where(x => x.Name == "one").Select(x => x.Numbers)
            .SingleAsync(Token);

        numbers.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task the_duplicated_column_is_still_written()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select numbers from fi_doc_tallysheet where json_extract(data, '$.name') = 'one'";

        (await command.ExecuteScalarAsync(Token)).ShouldBe("[1,2,3]");
    }
}

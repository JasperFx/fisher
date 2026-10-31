using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class Parcel
{
    public Guid Id { get; set; }
    public string? Label { get; set; }
}

/// <summary>
///     fisher#484, from marten#5759. <c>string.IsNullOrWhiteSpace</c> rendered SQLite's one-argument
///     <c>trim()</c>, which strips spaces and nothing else.
/// </summary>
public class white_space_predicates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("white-space");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Parcel>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task StoreAsync(params string?[] labels)
    {
        await using var session = _store.LightweightSession();

        foreach (var label in labels)
        {
            session.Store(new Parcel { Id = Guid.NewGuid(), Label = label });
        }

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task tabs_and_line_breaks_count_as_white_space()
    {
        await StoreAsync(null, "", "   ", "\t", " \r\n ", "a", " a ");

        await using var session = _store.QuerySession();

        var blank = await session.Query<Parcel>()
            .Where(x => string.IsNullOrWhiteSpace(x.Label))
            .CountAsync(Token);

        blank.ShouldBe(5);
    }

    [Fact]
    public async Task the_negated_form_is_the_other_half()
    {
        await StoreAsync(null, "", "\t\n", "a", "\ta\t");

        await using var session = _store.QuerySession();

        var labels = await session.Query<Parcel>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => x.Label)
            .ToListAsync(Token);

        labels.ShouldBe(["a", "\ta\t"], ignoreOrder: true);
    }

    /// <summary>
    ///     Agreement with .NET over the whole of <see cref="char" />, in both directions, so the SQL's
    ///     character list cannot quietly differ from what the method means.
    /// </summary>
    [Fact]
    public async Task a_value_of_any_white_space_character_matches()
    {
        var whiteSpace = Enumerable.Range(0, char.MaxValue + 1)
            .Select(x => (char)x)
            .Where(char.IsWhiteSpace)
            .Select(x => x.ToString())
            .ToArray();

        // Near misses: format and control characters that look blank and are not white space to .NET.
        await StoreAsync([.. whiteSpace, "​", "﻿", "\u0001"]);

        await using var session = _store.QuerySession();

        var matched = await session.Query<Parcel>()
            .Where(x => string.IsNullOrWhiteSpace(x.Label))
            .Select(x => x.Label)
            .ToListAsync(Token);

        matched.ShouldBe(whiteSpace, ignoreOrder: true);
    }
}

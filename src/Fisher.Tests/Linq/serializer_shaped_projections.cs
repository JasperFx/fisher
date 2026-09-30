using JasperFx;
using Fisher.Linq;
using Weasel.Core;

namespace Fisher.Tests.Linq;

public enum BeaconState
{
    Dark,
    Lit,
    Flashing
}

public class Buoy
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Uri? Endpoint { get; set; }
    public TimeSpan Interval { get; set; }
    public DateOnly Laid { get; set; }
    public TimeOnly Lit { get; set; }
    public BeaconState State { get; set; }
    public char Letter { get; set; }
    public decimal Depth { get; set; }
}

public class BuoyRow<T>
{
    public string Id { get; set; } = string.Empty;
    public T? Value { get; set; }
}

/// <summary>
///     fisher#361 — a <c>Select</c> of a member whose JSON is a string the column hands back as TEXT,
///     but whose CLR type <c>Convert.ChangeType</c> cannot build from a string.
/// </summary>
/// <remarks>
///     <para>
///         <c>Uri</c>, <c>TimeSpan</c>, <c>DateOnly</c> and <c>TimeOnly</c> all threw
///         <c>InvalidCastException</c> at materialisation while the whole document loaded fine — the
///         shape of fisher#351, for the types that fix did not cover. The fix hands those values to the
///         store's own serializer, which is what built them when the whole document was loaded, so the
///         two paths cannot disagree about what the JSON means.
///     </para>
///     <para>
///         Each fact asserts equality with the value the WHOLE-DOCUMENT load produces, not with a literal,
///         because that agreement is the property. The string, char and decimal facts are controls:
///         those types already went through <c>Convert.ChangeType</c> and must keep doing so.
///     </para>
/// </remarks>
public class serializer_shaped_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("serializer_select");
    private readonly TemporaryDatabase _stringEnums = TemporaryDatabase.Create("serializer_select_enums");
    private DocumentStore _store = null!;
    private DocumentStore _enumsAsStrings = null!;

    private static readonly Buoy Sample = new()
    {
        Id = "b1",
        Name = "Fastnet",
        Endpoint = new Uri("rabbitmq://queue/orders"),
        Interval = TimeSpan.FromSeconds(90),
        Laid = new DateOnly(2026, 9, 30),
        Lit = new TimeOnly(9, 15, 30),
        State = BeaconState.Flashing,
        Letter = 'F',
        Depth = 12.5m
    };

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Buoy>();
        });

        _enumsAsStrings = DocumentStore.For(options =>
        {
            options.ConnectionString = _stringEnums.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(EnumStorage.AsString);
            options.Schema.For<Buoy>();
        });

        foreach (var store in new[] { _store, _enumsAsStrings })
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = store.LightweightSession();
            session.Store(Sample);
            session.Store(new Buoy { Id = "b2", Name = "No endpoint" });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _enumsAsStrings.DisposeAsync();
        _database.Dispose();
        _stringEnums.Dispose();
    }

    private async Task<Buoy> LoadedAsync(DocumentStore store)
    {
        await using var session = store.QuerySession();
        return (await session.LoadAsync<Buoy>("b1", Token))!;
    }

    private async Task<T> SelectAsync<T>(DocumentStore store, System.Linq.Expressions.Expression<Func<Buoy, T>> selector)
    {
        await using var session = store.QuerySession();
        return (await session.Query<Buoy>().Where(x => x.Id == "b1").Select(selector).ToListAsync(Token)).Single();
    }

    [Fact]
    public async Task a_uri_member_projects()
    {
        (await SelectAsync(_store, x => x.Endpoint)).ShouldBe((await LoadedAsync(_store)).Endpoint);
    }

    [Fact]
    public async Task a_uri_member_projects_into_a_shaped_row()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<Buoy>()
            .OrderBy(x => x.Id)
            .Select(x => new BuoyRow<Uri> { Id = x.Id, Value = x.Endpoint })
            .ToListAsync(Token);

        rows.Select(x => x.Value).ShouldBe([Sample.Endpoint, null]);
    }

    [Fact]
    public async Task a_time_span_member_projects()
    {
        (await SelectAsync(_store, x => x.Interval)).ShouldBe((await LoadedAsync(_store)).Interval);
    }

    [Fact]
    public async Task a_date_only_member_projects()
    {
        (await SelectAsync(_store, x => x.Laid)).ShouldBe((await LoadedAsync(_store)).Laid);
    }

    [Fact]
    public async Task a_time_only_member_projects()
    {
        (await SelectAsync(_store, x => x.Lit)).ShouldBe((await LoadedAsync(_store)).Lit);
    }

    [Fact]
    public async Task the_serializer_shaped_members_project_together_in_an_anonymous_type()
    {
        var shaped = await SelectAsync(_store, x => new { x.Endpoint, x.Interval, x.Laid, x.Lit });
        var loaded = await LoadedAsync(_store);

        shaped.ShouldBe(new { loaded.Endpoint, loaded.Interval, loaded.Laid, loaded.Lit });
    }

    /// <summary>
    ///     A string-stored enum arrives as its NAME, which the enum branch used to hand to
    ///     <c>Convert.ToInt64</c>. Same class of failure as the four types above, one branch over.
    /// </summary>
    [Fact]
    public async Task a_string_stored_enum_projects()
    {
        (await SelectAsync(_enumsAsStrings, x => x.State)).ShouldBe(BeaconState.Flashing);
    }

    [Fact]
    public async Task an_integer_stored_enum_still_projects()
    {
        (await SelectAsync(_store, x => x.State)).ShouldBe(BeaconState.Flashing);
    }

    [Fact]
    public async Task the_controls_are_untouched()
    {
        (await SelectAsync(_store, x => x.Name)).ShouldBe("Fastnet");
        (await SelectAsync(_store, x => x.Letter)).ShouldBe('F');
        (await SelectAsync(_store, x => x.Depth)).ShouldBe(12.5m);
    }

    [Fact]
    public async Task an_absent_value_type_member_becomes_its_default()
    {
        await using var session = _store.QuerySession();

        var interval = (await session.Query<Buoy>().Where(x => x.Id == "b2").Select(x => x.Interval)
            .ToListAsync(Token)).Single();

        interval.ShouldBe(TimeSpan.Zero);
    }
}

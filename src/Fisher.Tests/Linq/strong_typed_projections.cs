using JasperFx;
using Fisher.Linq;
using Fisher.Tests.Documents;

namespace Fisher.Tests.Linq;

public readonly record struct AlarmId(string Value);

public class Alarm
{
    // Nullable, as fisher#351's report had it: `AlertId?` on the document.
    public AlarmId? Id { get; set; }
    public string Service { get; set; } = string.Empty;
}

/// <summary>
///     fisher#351 — a <c>Select</c> whose value is a strong-typed identifier.
/// </summary>
/// <remarks>
///     The column holds the wrapper's INNER value — a string, a Guid as text, an integer — and the
///     projection's materializer used to hand that straight to <c>Convert.ChangeType</c>, which cannot
///     build a wrapper and threw <c>InvalidCastException</c>. Loading the whole document never met it,
///     because the serializer builds the wrapper. Every backing is covered, because each arrives from
///     SQLite as a different CLR type and the inner conversion is what differs.
/// </remarks>
public class strong_typed_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("strong_typed_select");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.RegisterValueType<AlarmId>();
            options.Schema.For<Alarm>();
            options.Schema.For<TaggedRod>();
            options.Schema.For<Swivel>();
            options.Schema.For<Spoon>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task selecting_a_nullable_string_backed_id()
    {
        var alarm = new Alarm { Id = new AlarmId("disk-full"), Service = "billing" };
        await StoreAsync(alarm);

        await using var session = _store.QuerySession();
        var ids = await session.Query<Alarm>()
            .Where(x => x.Service == "billing")
            .Select(x => x.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        ids.ShouldBe([new AlarmId("disk-full")]);
    }

    [Fact]
    public async Task selecting_a_guid_backed_id()
    {
        var rod = new TaggedRod { Id = new RodId(Guid.NewGuid()), Name = "fly" };
        await StoreAsync(rod);

        await using var session = _store.QuerySession();
        var ids = await session.Query<TaggedRod>()
            .Where(x => x.Name == "fly")
            .Select(x => x.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        ids.ShouldBe([rod.Id]);
    }

    [Fact]
    public async Task selecting_an_int_backed_id()
    {
        var swivel = new Swivel { Size = "barrel" };
        await StoreAsync(swivel);

        await using var session = _store.QuerySession();
        var ids = await session.Query<Swivel>()
            .Where(x => x.Size == "barrel")
            .Select(x => x.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        ids.ShouldBe([swivel.Id]);
    }

    [Fact]
    public async Task selecting_an_id_built_by_a_static_builder()
    {
        var spoon = new Spoon { Finish = "brass" };
        await StoreAsync(spoon);

        await using var session = _store.QuerySession();
        var ids = await session.Query<Spoon>()
            .Where(x => x.Finish == "brass")
            .Select(x => x.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        ids.ShouldBe([spoon.Id]);
    }

    [Fact]
    public async Task selecting_the_id_inside_a_shaped_projection()
    {
        var alarm = new Alarm { Id = new AlarmId("cpu"), Service = "ledger" };
        await StoreAsync(alarm);

        await using var session = _store.QuerySession();
        var shaped = await session.Query<Alarm>()
            .Where(x => x.Service == "ledger")
            .Select(x => new { x.Id, x.Service })
            .SingleAsync(TestContext.Current.CancellationToken);

        shaped.Id.ShouldBe(alarm.Id);
        shaped.Service.ShouldBe("ledger");
    }

    private async Task StoreAsync<T>(T document) where T : notnull
    {
        await using var session = _store.LightweightSession();
        session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

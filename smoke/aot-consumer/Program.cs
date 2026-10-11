using System.Text.Json;
using System.Text.Json.Serialization;
using Fisher;
using Fisher.Linq;
using Fisher.Linq.Includes;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

// fisher#384 — see AotConsumer.csproj. Exits non-zero on any failure, so CI fails with it.

var path = Path.Combine(Path.GetTempPath(), $"fisher_aot_smoke_{Guid.NewGuid():n}.db");

var services = new ServiceCollection();
services.AddFisher(options =>
{
    options.Connection($"Data Source={path}");
    options.AutoCreateSchemaObjects = AutoCreate.All;

    // Native AOT disables reflection-based System.Text.Json, so an application supplies its own
    // source-generated context. That context also keeps each document's properties from being
    // trimmed, which is what Fisher's identity discovery needs.
    options.ConfigureSerialization(configure: json => json.TypeInfoResolver = SmokeJson.Default);

    // fisher#386. A strong-typed id and a sub-class are runtime types to Fisher's storage registry,
    // so a native image needs them named where they are still generic arguments: Identity(...) for
    // the wrapper, AddSubClass<TSub>() for the hierarchy, RegisterValueType<T>() for the converter.
    options.Schema.For<Alert>().Identity(x => x.Id);
    options.Schema.For<Ticket>().Identity(x => x.Id);
    options.RegisterValueType<TicketId>();
    options.Schema.For<Animal>().AddSubClass<Dog>();

    // fisher#398. An inline snapshot is a projection closed over (aggregate, id) — reflective unless
    // the registration names both while they are still generic arguments.
    options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Inline);

    // fisher#412. An aggregate keyed on a readonly record struct. A value-typed wrapper has no shared
    // instantiation for Native AOT to fall back on, so the projection is closed while both types are
    // still generic arguments: Snapshot<T, TId>() for a snapshotted aggregate, and
    // LiveStreamAggregation<T, TId>() for one that is only ever aggregated live.
    options.Schema.For<Pod>().Identity(x => x.Id);
    options.Projections.Snapshot<Pod, PodId>(SnapshotLifecycle.Inline);
    options.Projections.LiveStreamAggregation<Shoal, ShoalId>();

    // fisher#412. The async daemon: an async snapshot and a multi-stream projection, both projected by
    // a daemon running in the native image.
    options.Projections.Snapshot<Ledger>(SnapshotLifecycle.Async);
    options.Projections.Add(new PortVisitsProjection(), ProjectionLifecycle.Async);

    // fisher#412. The LINQ paths that used to serialize or close generics by reflection.
    options.Schema.For<Note>().FullTextIndex(x => x.Body);
});

await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<IDocumentStore>();

try
{
    var id = Guid.NewGuid();

    await using (var session = store.LightweightSession())
    {
        session.Insert(new GuidDoc { Id = id, Name = "one" });
        await session.SaveChangesAsync();
    }

    await using (var session = store.LightweightSession())
    {
        session.Store(new GuidDoc { Id = id, Name = "two" });
        session.Store(new StringDoc { Id = "s1", Name = "string" });

        var numbered = new IntDoc { Name = "int" };
        var big = new LongDoc { Name = "long" };
        session.Store(numbered);
        session.Store(big);
        await session.SaveChangesAsync();

        Expect(numbered.Id > 0, "a Hi-Lo int id was assigned");
        Expect(big.Id > 0, "a Hi-Lo long id was assigned");
    }

    await using (var query = store.QuerySession())
    {
        Expect((await query.LoadAsync<GuidDoc>(id))?.Name == "two", "a Guid-keyed document round-trips");
        Expect((await query.LoadAsync<StringDoc>("s1"))?.Name == "string", "a string-keyed document round-trips");

        var hits = await query.Query<GuidDoc>().Where(x => x.Name == "two").ToListAsync();
        Expect(hits.Count == 1, "a LINQ query finds the document");
    }

    var alertId = new AlertId(Guid.NewGuid());
    var ticket = new Ticket { Subject = "printer" };

    await using (var session = store.LightweightSession())
    {
        session.Store(new Alert { Id = alertId, Text = "hot" });
        session.Store(ticket);
        session.Store(new Dog { Id = id, Name = "Rex", Breed = "lab" });
        await session.SaveChangesAsync();

        Expect(ticket.Id.Value > 0, "a Hi-Lo id was assigned through an int-backed wrapper");
    }

    await using (var query = store.QuerySession())
    {
        Expect((await query.LoadAsync<Alert, AlertId>(alertId))?.Text == "hot", "a Guid-backed wrapper round-trips");
        Expect((await query.LoadAsync<Ticket, TicketId>(ticket.Id))?.Subject == "printer", "an int-backed wrapper round-trips");

        var tickets = await query.Query<Ticket>().Where(x => x.Id == ticket.Id).ToListAsync();
        Expect(tickets.Count == 1, "a LINQ query on a registered wrapper finds the document");

        Expect(await query.LoadAsync<Animal>(id) is Dog { Breed: "lab" }, "a sub-class loads as itself through its base");
        Expect((await query.LoadAsync<Dog>(id))?.Breed == "lab", "a sub-class loads directly");
        Expect((await query.Query<Dog>().ToListAsync()).Count == 1, "a sub-class query narrows to its type");
    }

    // ---- the event store (fisher#398) ----

    var voyage = Guid.NewGuid();

    await using (var session = store.LightweightSession())
    {
        session.Events.StartStream<Voyage>(voyage, new Departed("Hull"));
        await session.SaveChangesAsync();
    }

    await using (var session = store.LightweightSession())
    {
        session.Events.Append(voyage, new Arrived("Bergen"));
        await session.SaveChangesAsync();
    }

    await using (var session = store.LightweightSession())
    {
        var live = await session.Events.AggregateStreamAsync<Voyage>(voyage);
        Expect(live is { Port: "Bergen", Legs: 2 }, "live aggregation folds the stream");

        var stream = await session.Events.FetchForWriting<Voyage>(voyage);
        Expect(stream.Aggregate is { Legs: 2 } && stream.CurrentVersion == 2, "FetchForWriting folds the stream");

        stream.AppendOne(new Arrived("Tromso"));
        await session.SaveChangesAsync();
    }

    await using (var query = store.QuerySession())
    {
        var snapshot = await query.LoadAsync<Voyage>(voyage);
        Expect(snapshot is { Port: "Tromso", Legs: 3 }, "the inline snapshot was written and reloads");
    }

    // ---- strong-typed aggregate identities (fisher#412) ----

    var pod = Guid.NewGuid();

    await using (var session = store.LightweightSession())
    {
        session.Events.StartStream<Pod>(pod, new PodPlanted("garden"), new PeaAdded());
        await session.SaveChangesAsync();
    }

    await using (var session = store.LightweightSession())
    {
        var live = await session.Events.AggregateStreamAsync<Pod>(pod);
        Expect(live is { Bed: "garden", Peas: 1 } && live.Id == new PodId(pod), "a strong-typed aggregate folds live and carries its id");

        var stream = await session.Events.FetchForWriting<Pod, PodId>(new PodId(pod));
        Expect(stream.Aggregate is { Peas: 1 } && stream.CurrentVersion == 2, "FetchForWriting by a strong-typed id");

        stream.AppendOne(new PeaAdded());
        await session.SaveChangesAsync();
    }

    await using (var query = store.QuerySession())
    {
        Expect((await query.LoadAsync<Pod, PodId>(new PodId(pod)))?.Peas == 2,
            "the inline snapshot of a strong-typed aggregate was written and reloads");
    }

    var shoal = Guid.NewGuid();

    await using (var session = store.LightweightSession())
    {
        session.Events.StartStream<Shoal>(shoal, new FishJoined(), new FishJoined());
        await session.SaveChangesAsync();
    }

    await using (var session = store.LightweightSession())
    {
        var stream = await session.Events.FetchForWriting<Shoal>(shoal);
        Expect(stream.Aggregate is { Fish: 2 } && stream.Aggregate.Id == new ShoalId(shoal),
            "a live-only strong-typed aggregate folds through its declared identity");
    }

    // ---- the async daemon (fisher#412) ----

    var ledger = Guid.NewGuid();

    await using (var session = store.LightweightSession())
    {
        session.Events.StartStream<Ledger>(ledger, new Deposited(100), new Withdrawn(30));
        await session.SaveChangesAsync();
    }

    var daemon = await store.BuildProjectionDaemonAsync();
    try
    {
        await daemon.StartAllAsync();
        await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(60));
    }
    finally
    {
        await daemon.StopAllAsync();
        daemon.Dispose();
    }

    await using (var query = store.QuerySession())
    {
        Expect((await query.LoadAsync<Ledger>(ledger))?.Balance == 70, "the async snapshot was projected by the daemon");

        var visits = await query.LoadAsync<PortVisits>("Bergen");
        Expect(visits?.Count == 1, "the multi-stream projection was projected by the daemon");
        Expect((await query.LoadAsync<PortVisits>("Tromso"))?.Count == 1, "the multi-stream projection grouped by port");
    }

    // ---- LINQ and the step-through (fisher#412) ----

    var boat = Guid.NewGuid();

    // No ApplyAllConfiguredChangesToDatabaseAsync here: since fisher#422 the on-demand path creates the
    // full-text index with the table, which is what this section's Search() now relies on.

    await using (var session = store.LightweightSession())
    {
        session.Store(new Boat { Id = boat, Name = "Belle" });

        for (var i = 1; i <= 5; i++)
        {
            session.Store(new Catch
            {
                Id = Guid.NewGuid(), Weight = i, BoatId = boat, Landed = new DateOnly(2026, 8, i)
            });
        }

        session.Store(new Escalation { Id = Guid.NewGuid(), TicketId = ticket.Id });
        session.Store(new Note { Id = Guid.NewGuid(), Body = "corrosion on the hull" });
        session.Store(new Note { Id = Guid.NewGuid(), Body = "corrosion corrosion corrosion everywhere" });
        await session.SaveChangesAsync();
    }

    await using (var query = store.QuerySession())
    {
        // Keyset paging: the cursor payload used to be JsonSerializer over an object?[].
        var weights = new List<int>();
        string? cursor = null;
        do
        {
            var page = await query.Query<Catch>().OrderBy(x => x.Weight).ThenBy(x => x.Id)
                .ToCursorPageAsync(2, cursor);
            weights.AddRange(page.Items.Select(x => x.Weight));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Expect(weights.SequenceEqual([1, 2, 3, 4, 5]), "a keyset cursor walk covers every row once");

        // A DateOnly comparison value is rendered through the store's serializer.
        var late = await query.Query<Catch>().Where(x => x.Landed >= new DateOnly(2026, 8, 4)).ToListAsync();
        Expect(late.Count == 2, "a DateOnly comparison finds the documents");

        // Include() closed Enumerable.Contains over the member's runtime type.
        var boats = new List<Boat>();
        var catches = await query.Query<Catch>().Include(x => x.BoatId, boats).ToListAsync();
        Expect(catches.Count == 5 && boats.Count == 1 && boats[0].Name == "Belle", "Include fetches the related document");

        // ...over a member whose type is a value type nothing else closes Contains over.
        var related = new List<Ticket>();
        await query.Query<Escalation>().Include(x => x.TicketId, related).ToListAsync();
        Expect(related.Count == 1 && related[0].Subject == "printer", "Include fetches by a strong-typed identity");

        // OrderByRelevance closed its own marker method over T by name.
        var ranked = await query.Query<Note>().Where(x => x.Search("corrosion")).OrderByRelevance().ToListAsync();
        Expect(ranked.Count == 2 && ranked[0].Body.StartsWith("corrosion corrosion"), "a full-text query ranks by relevance");
    }

    // Projection step-through rendered state with JsonSerializer's default options.
    var records = new[] { (object)new Departed("Hull"), new Arrived("Bergen") }
        .Select((body, index) => new EventRecord(Guid.NewGuid(), index + 1, index + 1, voyage.ToString(),
            store.Options.EventGraph.EventMappingFor(body.GetType()).EventTypeName,
            JsonDocument.Parse(store.Options.Serializer.ToJson(body)).RootElement, null,
            DateTimeOffset.UtcNow, null, null))
        .ToList();

    var timeline = await ((IEventStore)store).RunProjectionByNameAsync(nameof(Voyage), voyage, records, null,
        CancellationToken.None);
    Expect(timeline.Steps.Count == 2 && timeline.FinalState?.GetProperty("legs").GetInt32() == 2,
        "projection step-through renders each state through the store's serializer");

    var typedTimeline = await ((IEventStore)store).RunProjectionAsync<Voyage>(nameof(Voyage), voyage, records,
        null, CancellationToken.None);
    Expect(typedTimeline.Steps.Select(x => x.After?.Legs).SequenceEqual([1, 2]),
        "typed projection step-through copies the state at every step");

    // jasperfx#869. Diagnostics criteria are Dynamic LINQ — runtime code generation — so a native image
    // has to refuse them by name rather than crash in the reflective hop, and a page without them still
    // has to read.
    var diagnostics = (IDocumentStoreDiagnostics)store;
    var animals = await diagnostics.QueryDocumentsAsync(typeof(Animal).FullName!, new DocumentQueryOptions(1, 10),
        CancellationToken.None);
    Expect(animals.TotalCount > 0, "a diagnostics page reads without criteria");

    try
    {
        await diagnostics.QueryDocumentsAsync(typeof(Animal).FullName!,
            new DocumentQueryOptions(1, 10) { Where = "Name = @0", Arguments = ["x"] }, CancellationToken.None);
        Expect(false, "diagnostics criteria are refused in a native image");
    }
    catch (DocumentCriteriaNotSupportedException e)
    {
        Expect(e.Message.Contains("Native AOT"), "diagnostics criteria are refused in a native image, by name");
    }

    // fisher#430: the paths that had never been measured natively.
    await UnmeasuredPaths.RunAsync();

    Console.WriteLine("OK: documents and events written and read in a native image.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL:");
    Console.Error.WriteLine(e);
    return 1;
}
finally
{
    try
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            File.Delete(file);
    }
    catch
    {
        // A leftover temp file is not the smoke's failure to report.
    }
}

static void Expect(bool condition, string what)
{
    if (!condition) throw new Exception($"Expected: {what}");
}

public class GuidDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class StringDoc
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class IntDoc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class LongDoc
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public readonly record struct AlertId(Guid Value);

public class Alert
{
    public AlertId Id { get; set; }
    public string Text { get; set; } = "";
}

public readonly record struct TicketId(int Value);

public class Ticket
{
    public TicketId Id { get; set; }
    public string Subject { get; set; } = "";
}

public class Animal
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Dog : Animal
{
    public string Breed { get; set; } = "";
}

public record Departed(string Port);

public record Arrived(string Port);

public class Voyage
{
    public Guid Id { get; set; }
    public string Port { get; set; } = "";
    public int Legs { get; set; }

    public static Voyage Create(Departed departed) => new() { Port = departed.Port, Legs = 1 };

    public void Apply(Arrived arrived)
    {
        Port = arrived.Port;
        Legs++;
    }
}

public readonly record struct PodId(Guid Value);

public record PodPlanted(string Bed);

public record PeaAdded;

public class Pod
{
    public PodId Id { get; set; }
    public string Bed { get; set; } = "";
    public int Peas { get; set; }

    public static Pod Create(PodPlanted planted) => new() { Bed = planted.Bed };

    public void Apply(PeaAdded _) => Peas++;
}

public readonly record struct ShoalId(Guid Value);

public record FishJoined;

public class Shoal
{
    public ShoalId Id { get; set; }
    public int Fish { get; set; }

    public void Apply(FishJoined _) => Fish++;
}

public record Deposited(decimal Amount);

public record Withdrawn(decimal Amount);

public class Ledger
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static Ledger Create(Deposited deposited) => new() { Balance = deposited.Amount };

    public void Apply(Deposited deposited) => Balance += deposited.Amount;

    public void Apply(Withdrawn withdrawn) => Balance -= withdrawn.Amount;
}

public class PortVisits
{
    public string Id { get; set; } = "";
    public int Count { get; set; }
}

public partial class PortVisitsProjection : Fisher.Projections.MultiStreamProjection<PortVisits, string>
{
    public PortVisitsProjection()
    {
        Identity<Arrived>(x => x.Port);
    }

    public void Apply(Arrived _, PortVisits visits) => visits.Count++;
}

public class Boat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Catch
{
    public Guid Id { get; set; }
    public int Weight { get; set; }
    public Guid BoatId { get; set; }
    public DateOnly Landed { get; set; }
}

public class Escalation
{
    public Guid Id { get; set; }
    public TicketId TicketId { get; set; }
}

public class Note
{
    public Guid Id { get; set; }
    public string Body { get; set; } = "";
}

[JsonSerializable(typeof(Pod))]
[JsonSerializable(typeof(PodPlanted))]
[JsonSerializable(typeof(PeaAdded))]
[JsonSerializable(typeof(Shoal))]
[JsonSerializable(typeof(FishJoined))]
[JsonSerializable(typeof(Deposited))]
[JsonSerializable(typeof(Withdrawn))]
[JsonSerializable(typeof(Ledger))]
[JsonSerializable(typeof(PortVisits))]
[JsonSerializable(typeof(Boat))]
[JsonSerializable(typeof(Catch))]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Escalation))]
[JsonSerializable(typeof(Departed))]
[JsonSerializable(typeof(Arrived))]
[JsonSerializable(typeof(Voyage))]
[JsonSerializable(typeof(GuidDoc))]
[JsonSerializable(typeof(Alert))]
[JsonSerializable(typeof(Ticket))]
[JsonSerializable(typeof(Animal))]
[JsonSerializable(typeof(Dog))]
[JsonSerializable(typeof(StringDoc))]
[JsonSerializable(typeof(IntDoc))]
[JsonSerializable(typeof(LongDoc))]
internal partial class SmokeJson : JsonSerializerContext;

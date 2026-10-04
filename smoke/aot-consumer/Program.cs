using System.Text.Json.Serialization;
using Fisher;
using Fisher.Linq;
using JasperFx;
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

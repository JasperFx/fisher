using System.Text.Json.Serialization;
using Fisher;
using Fisher.Linq;
using JasperFx;
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

    Console.WriteLine("OK: documents written and read in a native image.");
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

[JsonSerializable(typeof(GuidDoc))]
[JsonSerializable(typeof(StringDoc))]
[JsonSerializable(typeof(IntDoc))]
[JsonSerializable(typeof(LongDoc))]
internal partial class SmokeJson : JsonSerializerContext;

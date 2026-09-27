using Fisher;
using JasperFx.Events.Projections;

// Conventional Create/Apply methods are dispatched by the source generator the Fisher package bundles
// as an analyzer. There is no runtime fallback, so if the analyzer did not reach this project the
// build fails with JFXEVT900 — and if it somehow attached without generating, the fold below throws.

var file = Path.Combine(Path.GetTempPath(), $"fisher-smoke-{Guid.NewGuid():N}.db");

try
{
    await using (var store = DocumentStore.For(options =>
                 {
                     options.ConnectionString = $"Data Source={file}";
                     options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Inline);
                 }))
    {
        // What an application does at startup. Not what this project tests — the dispatcher is.
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(id, new VoyageBegun("Pequod"), new PortCalled("Nantucket"));
            await session.SaveChangesAsync();
        }

        await using var query = store.QuerySession();

        var live = await query.Events.AggregateStreamAsync<Voyage>(id)
                   ?? throw new InvalidOperationException("Live aggregation returned nothing.");
        var snapshot = await query.LoadAsync<Voyage>(id)
                       ?? throw new InvalidOperationException("The inline snapshot was not written.");

        if (live.Ship != "Pequod" || live.Ports != 1 || snapshot.Ship != "Pequod" || snapshot.Ports != 1)
        {
            throw new InvalidOperationException(
                $"Wrong fold: live {live.Ship}/{live.Ports}, snapshot {snapshot.Ship}/{snapshot.Ports}.");
        }
    }

    Console.WriteLine("The packaged Fisher dispatches conventional projection methods.");
    return 0;
}
finally
{
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        File.Delete(file + suffix);
    }
}

public record VoyageBegun(string Ship);

public record PortCalled(string Port);

public class Voyage
{
    public Guid Id { get; set; }
    public string Ship { get; set; } = "";
    public int Ports { get; set; }

    public static Voyage Create(VoyageBegun begun) => new() { Ship = begun.Ship };

    public void Apply(PortCalled _) => Ports++;
}

using BenchmarkDotNet.Attributes;
using Fisher.Linq;
using Fisher.TestUtils;

namespace Fisher.Benchmarks;

/// <summary>
///     Splits a LINQ query into its two halves — <b>construct</b> (walk the expression tree, resolve
///     members, build a <c>Statement</c>, render the SQL) and <b>execute</b> (bind, run, materialize)
///     — so the compiled-query question can be answered with a number rather than an argument.
/// </summary>
/// <remarks>
///     <para>
///         A compiled query removes the construct half and nothing else. On Marten that half sits in
///         front of a network round trip to a PostgreSQL server, so it is a small fraction of the call;
///         Fisher's database is a file inside the same process, so there is no round trip for it to be
///         a fraction of, and the ratio has to be measured rather than inherited. <b>Note the
///         database-side plan cache is a different thing entirely</b> and answers neither half: it
///         saves the *server* from re-planning SQL it has already seen, which is not the cost a
///         compiled query removes.
///     </para>
///     <para>
///         <b>Both halves run on one warm session</b>, created in <c>GlobalSetup</c> and used for
///         every invocation. That is what makes the pair comparable: a session per invocation would
///         charge the execute half for opening a pooled connection, which a compiled query would not
///         have saved either. <c>Construct</c> is <c>session.ToSql(...)</c>, which is
///         <c>BuildStatement</c> + <c>Statement.Apply</c> + <c>CommandBuilder.Compile</c> — the same
///         three calls <c>FisherQueryProvider.CommandFor</c> makes, minus the ensured-table cache hit
///         and setting the command's transaction.
///     </para>
///     <para>
///         <b>The table is deliberately tiny (200 rows, at most 10 returned)</b>, for the reason
///         <see cref="QueryBenchmarks" /> gives: a large result set puts deserialization in front of
///         everything and hides the very thing being measured. <c>Count</c> materializes nothing at
///         all, so it is the shape where construct cost is largest as a share — the ceiling on what a
///         compiled query could ever be worth here.
///     </para>
/// </remarks>
[Config(typeof(MicroBenchmarkConfig))]
public class QueryConstructionBenchmarks
{
    private TemporaryDatabase _database = null!;
    private DocumentStore _store = null!;
    private IDocumentSession _session = null!;
    private Guid _knownId;

    [GlobalSetup]
    public async Task Setup()
    {
        _database = TemporaryDatabase.Create("bdn-query-construction");
        _store = Scenarios.Harness.BuildStore(_database);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync();

        await using (var seed = _store.LightweightSession())
        {
            for (var i = 0; i < 200; i++)
            {
                var doc = new BenchDoc
                {
                    Id = Guid.NewGuid(),
                    Name = $"doc-{i}",
                    Number = i,
                    Timestamp = DateTimeOffset.UtcNow
                };

                if (i == 42)
                {
                    _knownId = doc.Id;
                }

                seed.Store(doc);
            }

            await seed.SaveChangesAsync();
        }

        _session = _store.LightweightSession();

        // Pay the first-use table ensure, the lazily-built mappings, the storage provider and the
        // connection open here rather than inside the first measured invocation.
        _ = _session.ToSql(_session.Query<BenchDoc>().Where(x => x.Number > 0));
        await _session.Query<BenchDoc>().Where(x => x.Number > 0).Take(10).ToListAsync();
        await _session.Query<BenchDoc>().Where(x => x.Number > 0).CountAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _session.DisposeAsync();
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- A filtered, ordered page: the ordinary read shape --------------------------------------

    /// <summary>Parse and render only, no database contact.</summary>
    [Benchmark]
    public string PageConstruct()
        => _session.ToSql(_session.Query<BenchDoc>()
            .Where(x => x.Number > 100 && x.Name != "missing")
            .OrderBy(x => x.Number)
            .Take(10));

    /// <summary>The same query end to end: construct, execute, materialize ten documents.</summary>
    [Benchmark]
    public async Task<IReadOnlyList<BenchDoc>> PageFull()
        => await _session.Query<BenchDoc>()
            .Where(x => x.Number > 100 && x.Name != "missing")
            .OrderBy(x => x.Number)
            .Take(10)
            .ToListAsync();

    // ---- The same predicate counted: nothing materialized ---------------------------------------

    [Benchmark]
    public string CountConstruct()
        => _session.ToSql(_session.Query<BenchDoc>()
            .Where(x => x.Number > 100 && x.Name != "missing"));

    /// <remarks>
    ///     The count wraps the statement rather than replacing it, so the construct half is the
    ///     <c>Where</c> chain above plus the wrap — close enough to <see cref="CountConstruct" /> to
    ///     read the pair as one comparison.
    /// </remarks>
    [Benchmark]
    public async Task<long> CountFull()
        => await _session.Query<BenchDoc>()
            .Where(x => x.Number > 100 && x.Name != "missing")
            .CountAsync();

    // ---- The shortest chain there is: one document by one member --------------------------------

    [Benchmark]
    public string FirstConstruct()
        => _session.ToSql(_session.Query<BenchDoc>().Where(x => x.Name == "doc-42"));

    [Benchmark]
    public async Task<BenchDoc?> FirstFull()
        => await _session.Query<BenchDoc>().Where(x => x.Name == "doc-42").FirstOrDefaultAsync();

    // ---- The cheapest execution there is: one row off the primary key ---------------------------

    /// <remarks>
    ///     <b>This is the shape most favourable to a compiled query</b>, and it is here for that
    ///     reason. Every other shape scans 200 rows through <c>json_extract</c>, so execution
    ///     dominates by construction; a predicate on <c>id</c> is an index seek returning one row, so
    ///     whatever share construction holds here is the ceiling on what caching it could ever be
    ///     worth on this store.
    /// </remarks>
    [Benchmark]
    public string ByIdConstruct()
        => _session.ToSql(_session.Query<BenchDoc>().Where(x => x.Id == _knownId));

    [Benchmark]
    public async Task<BenchDoc?> ByIdFull()
        => await _session.Query<BenchDoc>().Where(x => x.Id == _knownId).FirstOrDefaultAsync();
}

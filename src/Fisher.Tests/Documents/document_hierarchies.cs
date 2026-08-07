using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#17 — <c>Schema.For&lt;TBase&gt;().AddSubClass&lt;TDerived&gt;()</c>, so a type and its
///     sub-classes share one table and one identity space.
/// </summary>
/// <remarks>
///     <para>
///         The discriminator is a short alias in its own <c>doc_type</c> column, <b>not</b> the
///         assembly-qualified <c>dotnet_type</c> that was already on every row. The tests assert the
///         alias directly, because it is what is stored and therefore what a rename breaks.
///     </para>
///     <para>
///         The two narrowing paths are deliberately different and are tested separately: a query is
///         narrowed in SQL, a load by id is narrowed in memory. Conflating them would make "that id is
///         a different sub-class" indistinguishable from "no such id".
///     </para>
/// </remarks>
public class document_hierarchies : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hierarchies");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(options => options.Schema.For<FlyPattern>()
            .AddSubClass<DryFly>()
            .AddSubClass<WetFly>()
            .AddSubClass<Nymph>("bug"));

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore StoreFor(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    // ---- the table ----

    [Fact]
    public async Task the_hierarchy_shares_one_table()
    {
        await StoreFliesAsync();

        var tables = await ReadStringsAsync(
            "select name from sqlite_master where type = 'table' and name like 'fi_doc%'");

        tables.ShouldContain("fi_doc_flypattern");
        tables.ShouldNotContain("fi_doc_dryfly");
        tables.ShouldNotContain("fi_doc_wetfly");
    }

    [Fact]
    public async Task the_table_carries_a_doc_type_column()
    {
        var columns = await ReadStringsAsync("select name from pragma_table_xinfo('fi_doc_flypattern')");

        columns.ShouldContain("doc_type");
        // The assembly-qualified column is still there and still not the discriminator.
        columns.ShouldContain("dotnet_type");
    }

    [Fact]
    public async Task a_type_with_no_subclasses_gets_no_discriminator()
    {
        await using var store = StoreFor(_ => { });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Store(new Leader { Id = Guid.NewGuid(), Breaking = 4 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ReadStringsAsync("select name from pragma_table_xinfo('fi_doc_leader')"))
            .ShouldNotContain("doc_type");
    }

    /// <summary>
    ///     An abstract base can never be the concrete type of a row, so its table needs the column from
    ///     the first migration — adding it later would leave the rows already written with no
    ///     discriminator to read.
    /// </summary>
    [Fact]
    public async Task an_abstract_base_is_a_hierarchy_with_nothing_registered()
    {
        _store.Options.Schema.For<Plug>().Mapping.IsHierarchy.ShouldBeTrue();

        await using var store = StoreFor(options => options.Schema.For<Plug>());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        (await ReadStringsAsync("select name from pragma_table_xinfo('fi_doc_plug')"))
            .ShouldContain("doc_type");
    }

    // ---- writes ----

    [Fact]
    public async Task each_row_stores_its_own_alias()
    {
        await StoreFliesAsync();

        var aliases = await ReadStringsAsync("select distinct doc_type from fi_doc_flypattern order by doc_type");

        aliases.ShouldBe(["bug", "dryfly", "flypattern", "wetfly"]);
    }

    [Fact]
    public void an_explicit_alias_wins_over_the_default()
        => _store.Options.Schema.For<FlyPattern>().Mapping.AliasFor(typeof(Nymph)).ShouldBe("bug");

    /// <summary>
    ///     The same convention the base's own alias follows — the one the table is named from — rather
    ///     than the snake case Fisher-owned column names use. A hierarchy writes both into one column,
    ///     so mixing conventions would mean a reader had to know which type produced a row to know
    ///     which spelling to expect.
    /// </summary>
    [Fact]
    public void the_default_alias_follows_the_base_alias_convention()
    {
        var mapping = _store.Options.Schema.For<FlyPattern>().Mapping;

        mapping.AliasFor(typeof(DryFly)).ShouldBe("dryfly");
        mapping.AliasFor(typeof(FlyPattern)).ShouldBe(mapping.Alias);
    }

    // ---- reads through the base ----

    [Fact]
    public async Task loading_through_the_base_returns_the_subclass()
    {
        var ids = await StoreFliesAsync();

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<FlyPattern>(ids.Dry, TestContext.Current.CancellationToken);

        loaded.ShouldBeOfType<DryFly>().Hackle.ShouldBe("Badger");
    }

    [Fact]
    public async Task querying_the_base_returns_every_subclass_as_its_own_type()
    {
        await StoreFliesAsync();

        await using var session = _store.LightweightSession();
        var all = await session.Query<FlyPattern>().ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBe(4);
        all.OfType<DryFly>().Count().ShouldBe(1);
        all.OfType<WetFly>().Count().ShouldBe(1);
        all.OfType<Nymph>().Count().ShouldBe(1);
    }

    // ---- reads narrowed to a subclass ----

    [Fact]
    public async Task querying_a_subclass_narrows_to_it()
    {
        await StoreFliesAsync();

        await using var session = _store.LightweightSession();
        var dries = await session.Query<DryFly>().ToListAsync(TestContext.Current.CancellationToken);

        dries.ShouldHaveSingleItem().Hackle.ShouldBe("Badger");
    }

    /// <summary>
    ///     The narrowing is in SQL, not a client-side filter — which is the point, since the
    ///     alternative fetches and discards the rest of the table.
    /// </summary>
    [Fact]
    public async Task the_subclass_narrowing_reaches_the_database()
    {
        await StoreFliesAsync();

        await using var session = _store.LightweightSession();
        var count = await session.Query<WetFly>().CountAsync(TestContext.Current.CancellationToken);

        count.ShouldBe(1);
    }

    [Fact]
    public async Task a_predicate_composes_with_the_subclass_narrowing()
    {
        await StoreFliesAsync();

        await using var session = _store.LightweightSession();

        (await session.Query<DryFly>().Where(x => x.Name == "Adams")
            .ToListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);

        (await session.Query<DryFly>().Where(x => x.Name == "Hare's Ear")
            .ToListAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    /// <summary>
    ///     Narrowed in memory rather than in SQL. A load names one row, and the id is unique across the
    ///     hierarchy, so adding a discriminator predicate would only turn "that id is a different
    ///     sub-class" into the same answer as "no such id" — which is the answer either way, but for a
    ///     reason worth keeping distinct.
    /// </summary>
    [Fact]
    public async Task loading_a_subclass_by_another_subclass_id_returns_null()
    {
        var ids = await StoreFliesAsync();

        await using var session = _store.LightweightSession();

        (await session.LoadAsync<DryFly>(ids.Wet, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await session.LoadAsync<DryFly>(ids.Dry, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task storing_and_loading_through_the_subclass_round_trips()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Nymph { Id = id, Name = "Pheasant Tail", Weight = 2 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var nymph = await query.LoadAsync<Nymph>(id, TestContext.Current.CancellationToken);

        nymph.ShouldNotBeNull();
        nymph.Weight.ShouldBe(2);
    }

    // ---- registration ----

    [Fact]
    public void a_type_that_does_not_inherit_is_refused()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        Should.Throw<ArgumentException>(
            () => options.Schema.For<FlyPattern>().AddSubClass(typeof(Leader)));
    }

    [Fact]
    public void two_subclasses_cannot_share_an_alias()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        var ex = Should.Throw<InvalidOperationException>(() => options.Schema.For<FlyPattern>()
            .AddSubClass<DryFly>("same")
            .AddSubClass<WetFly>("same"));

        ex.Message.ShouldContain("same");
        ex.Message.ShouldContain("indistinguishable");
    }

    [Fact]
    public void a_subclass_cannot_take_the_base_alias()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        Should.Throw<InvalidOperationException>(
            () => options.Schema.For<FlyPattern>().AddSubClass<DryFly>("flypattern"));
    }

    [Fact]
    public void registering_the_same_subclass_twice_is_idempotent()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        options.Schema.For<FlyPattern>().AddSubClass<DryFly>().AddSubClass<DryFly>();

        options.Schema.For<FlyPattern>().Mapping.SubClasses.Count.ShouldBe(1);
    }

    /// <summary>
    ///     Throws rather than falling back to the base. A row written by a deployment that knew a
    ///     sub-class this one does not is a real configuration gap, and deserializing it as the base
    ///     would hand back an object quietly missing whatever the sub-class added.
    /// </summary>
    [Fact]
    public void an_unknown_alias_throws_rather_than_falling_back()
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(
            () => _store.Options.Schema.For<FlyPattern>().Mapping.TypeFor("streamer"));

        ex.Message.ShouldContain("streamer");
    }

    [Fact]
    public void an_unregistered_type_has_no_alias()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => _store.Options.Schema.For<FlyPattern>().Mapping.AliasFor(typeof(Leader)));

    // ---- helpers ----

    private async Task<(Guid Dry, Guid Wet)> StoreFliesAsync()
    {
        var dry = Guid.NewGuid();
        var wet = Guid.NewGuid();

        await using var session = _store.LightweightSession();

        session.Store<FlyPattern>(new FlyPattern { Id = Guid.NewGuid(), Name = "Generic" });
        session.Store(new DryFly { Id = dry, Name = "Adams", Hackle = "Badger" });
        session.Store(new WetFly { Id = wet, Name = "Silver Doctor", SinkRate = 3 });
        session.Store(new Nymph { Id = Guid.NewGuid(), Name = "Hare's Ear", Weight = 1 });

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (dry, wet);
    }

    private async Task<List<string>> ReadStringsAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, TestContext.Current.CancellationToken))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }
}

public class DryFly : FlyPattern
{
    public string Hackle { get; set; } = string.Empty;
}

public class WetFly : FlyPattern
{
    public int SinkRate { get; set; }
}

public class Nymph : FlyPattern
{
    public int Weight { get; set; }
}

public abstract class Plug
{
    public Guid Id { get; set; }
}

public class FlyPattern
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Leader
{
    public Guid Id { get; set; }
    public int Breaking { get; set; }
}

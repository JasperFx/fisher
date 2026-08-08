using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;

namespace Fisher.Tests.Documents;

/// <summary>
///     Patching — fisher#35.
/// </summary>
/// <remarks>
///     The cases that carry weight are the absent-member ones. <c>json_extract</c> of a missing key is
///     SQL NULL, so an increment without <c>coalesce</c> silently nulls the member and an append to a
///     missing array has to create it. Everything else is the happy path.
/// </remarks>
public class patching_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("patching");
    private DocumentStore _store = null!;
    private readonly Guid _frodo = Guid.NewGuid();
    private readonly Guid _sam = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Angler>().Duplicate(x => x.Catches);
            o.Schema.For<Guarded>().UseOptimisticConcurrency();
            o.Schema.For<Gone>().SoftDeleted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await Reseed();
    }

    private async Task Reseed()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Angler
        {
            Id = _frodo, Name = "Frodo", Catches = 3, Fee = 12.5m,
            Flies = ["dun", "olive"], Home = new Water { Name = "Brandywine" }
        });
        session.Store(new Angler { Id = _sam, Name = "Sam", Catches = 9, Flies = [] });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Angler> Reload(Guid id)
    {
        await using var session = _store.LightweightSession();
        return (await session.LoadAsync<Angler>(id, Token))!;
    }

    // ---- the operations ----

    [Fact]
    public async Task set_a_member_and_a_nested_one()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Set(x => x.Name, "Mr Underhill")
                .Set(x => x.Home.Name, "Withywindle");
            await session.SaveChangesAsync(Token);
        }

        var angler = await Reload(_frodo);
        angler.Name.ShouldBe("Mr Underhill");
        angler.Home.Name.ShouldBe("Withywindle");
    }

    /// <summary>
    ///     A value goes in through the store's serializer, not through fisher#34's column conversions —
    ///     it lands inside <c>data</c>, so it must match what a full write would have produced.
    /// </summary>
    [Fact]
    public async Task set_values_of_every_shape()
    {
        var landed = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo)
                .Set(x => x.Fee, 99.25m)
                .Set(x => x.LandedAt, landed)
                .Set(x => x.Home, new Water { Name = "Sea" })
                .Set(x => x.Flies, ["nymph"]);
            await session.SaveChangesAsync(Token);
        }

        var angler = await Reload(_frodo);
        angler.Fee.ShouldBe(99.25m);
        angler.LandedAt.ShouldBe(landed);
        angler.Home.Name.ShouldBe("Sea");
        angler.Flies.ShouldBe(["nymph"]);
    }

    [Fact]
    public async Task increment()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Increment(x => x.Catches, 4);
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Catches.ShouldBe(7);
    }

    /// <summary>
    ///     Without <c>coalesce</c> this is <c>NULL + 5</c>, so the member silently becomes null rather
    ///     than 5 — and only for documents whose key is absent or null.
    /// </summary>
    /// <remarks>
    ///     The member is <c>int?</c> deliberately. A non-nullable <c>int</c> serializes as 0 rather
    ///     than being absent, so the test would pass with or without the coalesce — which is what the
    ///     first version of it did.
    /// </remarks>
    [Fact]
    public async Task increment_an_absent_member()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Increment(x => x.Untouched, 5);
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Untouched.ShouldBe(5);
    }

    [Fact]
    public async Task append_and_append_if_not_exists()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo)
                .Append(x => x.Flies, "sedge")
                .AppendIfNotExists(x => x.Flies, "dun")
                .AppendIfNotExists(x => x.Flies, "hopper");
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Flies.ShouldBe(["dun", "olive", "sedge", "hopper"]);
    }

    [Fact]
    public async Task append_to_an_empty_array()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_sam).Append(x => x.Flies, "first");
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_sam)).Flies.ShouldBe(["first"]);
    }

    [Fact]
    public async Task remove_takes_every_match()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Append(x => x.Flies, "dun");
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Remove(x => x.Flies, "dun");
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Flies.ShouldBe(["olive"]);
    }

    [Fact]
    public async Task delete_and_rename()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Delete(x => x.Fee);
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Fee.ShouldBe(0m);

        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Rename("name", x => x.Nickname);
            await session.SaveChangesAsync(Token);
        }

        var angler = await Reload(_frodo);
        angler.Nickname.ShouldBe("Frodo");
        angler.Name.ShouldBe("");
    }

    [Fact]
    public async Task duplicate_writes_every_destination()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Duplicate(x => x.Name, "Ring-bearer", x => x.Nickname);
            await session.SaveChangesAsync(Token);
        }

        var angler = await Reload(_frodo);
        angler.Name.ShouldBe("Ring-bearer");
        angler.Nickname.ShouldBe("Ring-bearer");
    }

    // ---- composition ----

    /// <summary>
    ///     Steps that read what they change read the accumulated expression, not the bare column, so a
    ///     chain sees its own earlier work.
    /// </summary>
    [Fact]
    public async Task chained_steps_compose_in_one_statement()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo)
                .Increment(x => x.Catches, 1)
                .Increment(x => x.Catches, 1)
                .Append(x => x.Flies, "a")
                .AppendIfNotExists(x => x.Flies, "a");
            await session.SaveChangesAsync(Token);
        }

        var angler = await Reload(_frodo);
        angler.Catches.ShouldBe(5);
        angler.Flies.ShouldBe(["dun", "olive", "a"]);
    }

    [Fact]
    public async Task patch_by_predicate_touches_every_match()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(x => x.Catches > 0).Increment(x => x.Catches, 10);
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Catches.ShouldBe(13);
        (await Reload(_sam)).Catches.ShouldBe(19);
    }

    [Fact]
    public async Task a_patch_commits_with_the_rest_of_the_unit_of_work()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Increment(x => x.Catches, 1);
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Merry" });
            await session.SaveChangesAsync(Token);
        }

        (await Reload(_frodo)).Catches.ShouldBe(4);

        await using var check = _store.LightweightSession();
        (await check.Query<Angler>().CountAsync(Token)).ShouldBe(3);
    }

    // ---- the metadata a patch has to maintain ----

    /// <summary>
    ///     fisher#2's dividend: a duplicated field is a <c>VIRTUAL</c> generated column over
    ///     <c>data</c>, so it follows a patch with nothing to refresh. Marten and Polecat must update
    ///     theirs inside the patch SQL.
    /// </summary>
    [Fact]
    public async Task a_duplicated_column_follows_a_patch_with_no_refresh()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Patch<Angler>(_frodo).Set(x => x.Catches, 42);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        // Queried through the duplicated column, not through json_extract.
        (await check.Query<Angler>().Where(x => x.Catches == 42).CountAsync(Token)).ShouldBe(1);
    }

    /// <summary>
    ///     The version is not in the JSON, so nothing about the json1 expression moves it. Without the
    ///     explicit assignment an optimistic-concurrency type would silently stop seeing patched writes.
    /// </summary>
    [Fact]
    public async Task a_patch_moves_the_version_and_the_timestamp()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Guarded { Id = id, Label = "one" });
            await session.SaveChangesAsync(Token);
        }

        var before = await VersionAndModified(id);

        await Task.Delay(20, Token);

        await using (var session = _store.LightweightSession())
        {
            session.Patch<Guarded>(id).Set(x => x.Label, "two");
            await session.SaveChangesAsync(Token);
        }

        var after = await VersionAndModified(id);

        after.Version.ShouldNotBe(before.Version);
        after.Modified.ShouldBeGreaterThan(before.Modified);
    }

    private async Task<(string Version, string Modified)> VersionAndModified(Guid id)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select guid_version, last_modified from fi_doc_guarded where id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);

        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    ///     A soft-deleted row is not there as far as every other read is concerned, so a patch must not
    ///     reach one either.
    /// </summary>
    [Fact]
    public async Task a_patch_does_not_reach_a_soft_deleted_document()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Gone { Id = id, Label = "here" });
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Delete<Gone>(id);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Patch<Gone>(id).Set(x => x.Label, "changed");
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();
        var row = await check.Query<Gone>().MaybeDeleted().Where(x => x.Id == id).SingleAsync(Token);
        row!.Label.ShouldBe("here");
    }

    [Fact]
    public async Task an_empty_patch_does_nothing()
    {
        await using var session = _store.LightweightSession();
        session.Patch<Angler>(_frodo);

        await Should.NotThrowAsync(() => session.SaveChangesAsync(Token));

        (await Reload(_frodo)).Catches.ShouldBe(3);
    }

    public class Water
    {
        public string Name { get; set; } = "";
    }

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Nickname { get; set; } = "";
        public int Catches { get; set; }
        /// <summary>
        ///     Nullable so it serializes as JSON <c>null</c>, which <c>json_extract</c> returns as SQL
        ///     NULL — the shape an increment has to survive. A non-nullable int would serialize as 0
        ///     and the test would pass with or without the coalesce.
        /// </summary>
        public int? Untouched { get; set; }
        public decimal Fee { get; set; }
        public DateTimeOffset LandedAt { get; set; }
        public string[] Flies { get; set; } = [];
        public Water Home { get; set; } = new();
    }

    public class Guarded
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }

    public class Gone
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = "";
    }
}

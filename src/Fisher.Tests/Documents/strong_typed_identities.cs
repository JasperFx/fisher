using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

public readonly record struct RodId(Guid Value);

public readonly record struct SwivelId(int Value);

public readonly record struct HookId(string Value);

/// <summary>A builder rather than a constructor — the other shape <c>ValueTypeInfo</c> accepts.</summary>
public readonly struct SpoonId
{
    private SpoonId(long value) => Value = value;

    public long Value { get; }

    public static SpoonId From(long value) => new(value);
}

public class TaggedRod
{
    public RodId Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Swivel
{
    public SwivelId Id { get; set; }
    public string Size { get; set; } = string.Empty;
}

public class Hook
{
    public HookId Id { get; set; }
    public string Pattern { get; set; } = string.Empty;
}

public class Spoon
{
    public SpoonId Id { get; set; }
    public string Finish { get; set; } = string.Empty;
}

/// <summary>
///     fisher#14 — documents identified by a strong-typed id wrapper.
/// </summary>
/// <remarks>
///     <c>StrongTypedIdentityCompliance</c> covers the Guid- and string-backed cases through the event
///     store. What it cannot reach is the numeric backings, where the wrapper decides the *column
///     type* — and the Guid casing, which the suite cannot see because it never looks at the row.
/// </remarks>
public class strong_typed_identities : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("strong_typed");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<TaggedRod>();
            options.Schema.For<Swivel>();
            options.Schema.For<Hook>();
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
    public async Task a_guid_backed_id_round_trips()
    {
        var id = new RodId(Guid.NewGuid());

        await StoreAsync(new TaggedRod { Id = id, Name = "Sage" });

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<TaggedRod, RodId>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(id);
        loaded.Name.ShouldBe("Sage");
    }

    /// <summary>
    ///     The wrapper must not reintroduce the casing trap <c>SqliteGuidIdentification</c> exists for.
    ///     The compliance suite cannot see this — it never reads the row — and a document written in
    ///     uppercase would still round-trip within one process while being invisible to every
    ///     <c>json_each</c> id match.
    /// </summary>
    [Fact]
    public async Task a_guid_backed_id_is_stored_as_lowercase_canonical_text()
    {
        var id = new RodId(Guid.NewGuid());

        await StoreAsync(new TaggedRod { Id = id, Name = "Orvis" });

        var stored = (string)(await ScalarAsync("select id from fi_doc_taggedrod"))!;

        stored.ShouldBe(id.Value.ToString("D").ToLowerInvariant());
    }

    /// <summary>
    ///     The column type comes from the type the wrapper wraps, not from the wrapper. Deriving it
    ///     from the wrapper gives an int-backed id a TEXT column, which sorts and compares as text.
    /// </summary>
    [Fact]
    public async Task an_int_backed_id_gets_an_integer_column()
        => (await ColumnTypeAsync("fi_doc_swivel", "id")).ShouldBe("INTEGER");

    [Fact]
    public async Task a_guid_backed_id_gets_a_text_column()
        => (await ColumnTypeAsync("fi_doc_taggedrod", "id")).ShouldBe("TEXT");

    /// <summary>
    ///     A numeric wrapper gets its ids from the same Hi-Lo sequence its unwrapped counterpart would,
    ///     so an unassigned id is filled in on store rather than left at zero.
    /// </summary>
    [Fact]
    public async Task an_int_backed_id_is_assigned_from_the_hilo_sequence()
    {
        var swivel = new Swivel { Size = "size 8" };

        await StoreAsync(swivel);

        swivel.Id.Value.ShouldBeGreaterThan(0);

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Swivel, SwivelId>(swivel.Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Size.ShouldBe("size 8");
    }

    [Fact]
    public async Task a_string_backed_id_round_trips()
    {
        var id = new HookId("14-scud");

        await StoreAsync(new Hook { Id = id, Pattern = "Scud" });

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Hook, HookId>(id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(id);
    }

    /// <summary>
    ///     <c>ValueTypeInfo</c> accepts a static builder as well as a constructor, and the second shape
    ///     has no test anywhere else in Fisher.
    /// </summary>
    [Fact]
    public async Task a_wrapper_built_by_a_static_builder_round_trips()
    {
        var spoon = new Spoon { Finish = "copper" };

        await StoreAsync(spoon);

        spoon.Id.Value.ShouldBeGreaterThan(0);

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Spoon, SpoonId>(spoon.Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Finish.ShouldBe("copper");
    }

    [Fact]
    public async Task an_unknown_strong_typed_id_loads_as_null()
    {
        await using var session = _store.LightweightSession();

        (await session.LoadAsync<TaggedRod, RodId>(new RodId(Guid.NewGuid()),
            TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    ///     A type whose <c>Id</c> is neither canonical nor a valid wrapper is still refused, and the
    ///     message says what a wrapper has to look like.
    /// </summary>
    [Fact]
    public void a_member_that_is_not_a_usable_identity_is_still_refused()
    {
        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Tippet>();
        }));

        // The permissive second pass names the member it found, rather than claiming there is none —
        // and now says what a wrapper would have to look like, since that is the other way to qualify.
        ex.Message.ShouldContain("'Uri', which Fisher cannot store");
        ex.Message.ShouldContain("strong-typed wrapper");
    }

    // ---- helpers ----

    private async Task StoreAsync<T>(T document) where T : notnull
    {
        await using var session = _store.LightweightSession();
        session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> ColumnTypeAsync(string table, string column)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = $"select type from pragma_table_xinfo('{table}') where name = '{column}'";

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>An identity member of a type that is neither canonical nor a wrapper shape.</summary>
public class Tippet
{
    public Uri Id { get; set; } = new("urn:tippet");
}

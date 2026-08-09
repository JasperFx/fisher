using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#38 — <c>Schema.For&lt;T&gt;().ForeignKey&lt;TOther&gt;(x =&gt; x.OtherId)</c>, a real
///     foreign key between two document tables.
/// </summary>
/// <remarks>
///     <para>
///         <b>The blocker the issue told us to check first does not exist.</b> It asked whether SQLite
///         accepts a <c>VIRTUAL</c> generated column as a foreign key <em>child</em>, because if not,
///         a foreign key would need a written or <c>STORED</c> column and would reopen the write-path
///         question fisher#2 closed. Probed against SQLite 3.50.4 before anything was built: the table
///         is created, the constraint is enforced, a NULL child is exempt, and <c>ON DELETE CASCADE</c>
///         works. So the write path is untouched and a foreign key costs index space only.
///     </para>
///     <para>
///         Enforcement is per-connection in SQLite and off by default <em>in the library</em> — but on
///         for every connection Fisher opens, because Weasel's default pragma profile sets it. That is
///         the same fact fisher#6 discovered the hard way.
///     </para>
/// </remarks>
public class document_foreign_keys : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("foreign-keys");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    private DocumentStore StoreFor(Action<StoreOptions>? extra = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<River>();
            options.Schema.For<Beat>().ForeignKey<River>(x => x.RiverId);
            options.Schema.For<FishingPermit>().ForeignKey<River>(x => x.RiverId, CascadeAction.Cascade);
            extra?.Invoke(options);
        });

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Guid> SeedRiverAsync(string name = "Tay")
    {
        var id = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Store(new River { Id = id, Name = name });
        await session.SaveChangesAsync(Token);

        return id;
    }

    [Fact]
    public async Task a_reference_that_exists_is_accepted()
    {
        var riverId = await SeedRiverAsync();
        var beatId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Beat { Id = beatId, Name = "Upper", RiverId = riverId });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Beat>(beatId, Token))!.RiverId.ShouldBe(riverId);
    }

    /// <remarks>
    ///     The constraint is real, not decorative — and it is enforced against a generated column,
    ///     which is the whole question fisher#38 opened with.
    /// </remarks>
    [Fact]
    public async Task an_orphan_is_refused()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Beat { Id = Guid.NewGuid(), Name = "Nowhere", RiverId = Guid.NewGuid() });

        var ex = await Should.ThrowAsync<SqliteException>(async () => await session.SaveChangesAsync(Token));
        ex.Message.ShouldContain("FOREIGN KEY constraint failed");
    }

    /// <remarks>
    ///     <c>json_extract</c> yields SQL NULL for an absent key and SQLite exempts a NULL child value,
    ///     so a document that does not name a parent is unconstrained. Same asymmetry as a
    ///     <c>UNIQUE</c> index over an absent member, and the same on both siblings — worth pinning
    ///     because it is the kind of thing a reader assumes the opposite of.
    /// </remarks>
    [Fact]
    public async Task a_document_with_no_reference_is_unconstrained()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Beat { Id = id, Name = "Unassigned", RiverId = null });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Beat>(id, Token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task deleting_a_referenced_document_is_refused_by_default()
    {
        var riverId = await SeedRiverAsync();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Beat { Id = Guid.NewGuid(), Name = "Upper", RiverId = riverId });
            await session.SaveChangesAsync(Token);
        }

        await using var deleting = _store.LightweightSession();
        deleting.Delete<River>(riverId);

        var ex = await Should.ThrowAsync<SqliteException>(async () => await deleting.SaveChangesAsync(Token));
        ex.Message.ShouldContain("FOREIGN KEY constraint failed");
    }

    [Fact]
    public async Task on_delete_cascade_removes_the_referencing_documents()
    {
        var riverId = await SeedRiverAsync();
        var permitId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new FishingPermit { Id = permitId, Holder = "Frodo", RiverId = riverId });
            await session.SaveChangesAsync(Token);
        }

        await using (var deleting = _store.LightweightSession())
        {
            deleting.Delete<River>(riverId);
            await deleting.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<FishingPermit>(permitId, Token)).ShouldBeNull();
    }

    // ---- schema ----

    /// <remarks>
    ///     The delta-detection check, and the place a second gap would show. Generated columns already
    ///     needed <c>pragma_table_xinfo</c> (weasel#426); a foreign key on one is exactly where the
    ///     next detection hole would be, and it presents as the second migration in a process failing
    ///     rather than as anything a schema test would catch.
    /// </remarks>
    [Fact]
    public async Task applying_the_configuration_again_is_a_no_op()
    {
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        (await ForeignKeysOfAsync("fi_doc_beat")).ShouldBe(["river_id->fi_doc_river.id"]);
    }

    [Fact]
    public async Task the_key_names_the_generated_column_and_the_other_tables_id()
    {
        (await ForeignKeysOfAsync("fi_doc_fishingpermit")).ShouldBe(["river_id->fi_doc_river.id"]);

        // Declaring the key duplicated the member, so the column and its index are there too.
        (await ColumnsOfAsync("fi_doc_beat")).ShouldContain("river_id");
    }

    /// <remarks>
    ///     The implicit duplication is what makes a foreign key possible at all — a member lives in
    ///     <c>data</c> and a constraint needs a column — so a query against it is served by the
    ///     column's index for free.
    /// </remarks>
    [Fact]
    public async Task the_duplicated_column_is_indexed_and_the_planner_uses_it()
    {
        var riverId = await SeedRiverAsync();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Beat { Id = Guid.NewGuid(), Name = "Upper", RiverId = riverId });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var sql = query.ToSql(query.Query<Beat>().Where(x => x.RiverId == riverId));

        sql.ShouldContain("river_id");
        sql.ShouldNotContain("json_extract(data, '$.RiverId')");

        (await query.Query<Beat>().Where(x => x.RiverId == riverId).ToListAsync(Token)).Count.ShouldBe(1);
    }

    // ---- cleaning ----

    /// <remarks>
    ///     fisher#6's lesson one layer over: an unordered sweep fails with
    ///     <c>FOREIGN KEY constraint failed</c> for whichever order <c>sqlite_master</c> happens to
    ///     return, which makes it an intermittent rather than a failure.
    /// </remarks>
    [Fact]
    public async Task the_cleaner_clears_referencing_tables_first()
    {
        var riverId = await SeedRiverAsync();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Beat { Id = Guid.NewGuid(), Name = "Upper", RiverId = riverId });
            session.Store(new FishingPermit { Id = Guid.NewGuid(), Holder = "Frodo", RiverId = riverId });
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.Clean.DeleteAllDocumentsAsync(Token);

        await using var query = _store.LightweightSession();
        (await query.Query<River>().ToListAsync(Token)).ShouldBeEmpty();
        (await query.Query<Beat>().ToListAsync(Token)).ShouldBeEmpty();
        (await query.Query<FishingPermit>().ToListAsync(Token)).ShouldBeEmpty();
    }

    // ---- configuration ----

    [Fact]
    public void a_self_reference_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Beat>().ForeignKey<Beat>(x => x.RiverId);
        }));

        ex.Message.ShouldContain("cannot declare a foreign key to itself");
    }

    [Fact]
    public void declaring_the_same_key_twice_is_idempotent_and_changing_it_is_not()
    {
        using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Beat>().ForeignKey<River>(x => x.RiverId).ForeignKey<River>(x => x.RiverId);
        });

        store.Options.Schema.MappingFor(typeof(Beat)).DuplicatedFields.Count.ShouldBe(1);

        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Beat>()
                .ForeignKey<River>(x => x.RiverId)
                .ForeignKey<River>(x => x.RiverId, CascadeAction.Cascade);
        }));

        ex.Message.ShouldContain("already has a foreign key");
    }

    private async Task<List<string>> ForeignKeysOfAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select \"from\" || '->' || \"table\" || '.' || \"to\" from pragma_foreign_key_list('{table}')";

        var keys = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private async Task<List<string>> ColumnsOfAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select name from pragma_table_xinfo('{table}')";

        var columns = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

public class River
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Beat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? RiverId { get; set; }
}

public class FishingPermit
{
    public Guid Id { get; set; }
    public string Holder { get; set; } = string.Empty;
    public Guid? RiverId { get; set; }
}

using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using JasperFx.Metadata;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

public class soft_deleted_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("soft_deletes");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // The three ways of saying the same thing, one per document type, so each is exercised.
            options.Schema.For<Lure>();
            options.Schema.For<Fly>();
            options.Schema.For<Net>().SoftDeleted();
            options.Schema.For<Sinker>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task the_two_columns_are_added_only_for_a_soft_deleted_type()
    {
        (await ColumnsOf("fi_doc_lure")).ShouldContain("is_deleted");
        (await ColumnsOf("fi_doc_lure")).ShouldContain("deleted_at");

        var plain = await ColumnsOf("fi_doc_sinker");
        plain.ShouldNotContain("is_deleted");
        plain.ShouldNotContain("deleted_at");
    }

    [Fact]
    public async Task deleting_flags_the_row_rather_than_removing_it()
    {
        var lure = await StoreLure("Mepps", 3);

        await using (var session = _store.LightweightSession())
        {
            session.Delete(lure);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (isDeleted, deletedAt) = await DeletionStateOf("fi_doc_lure", lure.Id);

        isDeleted.ShouldBe(1);
        deletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_deleted_document_is_invisible_to_load_and_load_many()
    {
        var deleted = await StoreLure("Gone", 1);
        var live = await StoreLure("Here", 2);

        await DeleteLure(deleted.Id);

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<Lure>(deleted.Id, TestContext.Current.CancellationToken)).ShouldBeNull();

        var many = await query.LoadManyAsync<Lure>(deleted.Id, live.Id);
        many.Select(x => x.Name).ShouldBe(["Here"]);
    }

    [Fact]
    public async Task a_deleted_document_is_invisible_to_a_query()
    {
        var deleted = await StoreLure("Gone", 1);
        await StoreLure("Here", 2);

        await DeleteLure(deleted.Id);

        await using var query = _store.LightweightSession();

        (await query.Query<Lure>().ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Here"]);

        // The filter has to survive the caller's own predicate and the count path alike, which are
        // separate places the statement is assembled.
        (await query.Query<Lure>().Where(x => x.Depth > 0).CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task maybe_deleted_drops_the_filter_and_is_deleted_inverts_it()
    {
        var deleted = await StoreLure("Gone", 1);
        await StoreLure("Here", 2);

        await DeleteLure(deleted.Id);

        await using var query = _store.LightweightSession();

        (await query.Query<Lure>().MaybeDeleted().ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).OrderBy(x => x).ShouldBe(["Gone", "Here"]);

        (await query.Query<Lure>().IsDeleted().ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Gone"]);
    }

    [Fact]
    public async Task deleted_since_and_deleted_before_bound_the_deletion_time()
    {
        var lure = await StoreLure("Timed", 1);
        await DeleteLure(lure.Id);

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var after = DateTimeOffset.UtcNow.AddMinutes(1);

        await using var query = _store.LightweightSession();

        (await query.Query<Lure>().DeletedSince(before).ToListAsync(TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);

        (await query.Query<Lure>().DeletedSince(after).ToListAsync(TestContext.Current.CancellationToken))
            .ShouldBeEmpty();

        (await query.Query<Lure>().DeletedBefore(after).ToListAsync(TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);

        (await query.Query<Lure>().DeletedBefore(before).ToListAsync(TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     Storing a soft-deleted document brings it back, which is the upsert's update branch
    ///     assigning both columns from <c>excluded.*</c> rather than leaving them alone.
    /// </summary>
    [Fact]
    public async Task storing_a_deleted_document_undeletes_it()
    {
        var lure = await StoreLure("Returning", 4);
        await DeleteLure(lure.Id);

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Lure { Id = lure.Id, Name = "Returned", Depth = 5 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (isDeleted, deletedAt) = await DeletionStateOf("fi_doc_lure", lure.Id);
        isDeleted.ShouldBe(0);
        deletedAt.ShouldBeNull();

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Lure>(lure.Id, TestContext.Current.CancellationToken))!.Name.ShouldBe("Returned");
    }

    [Fact]
    public async Task hard_delete_removes_the_row_outright()
    {
        var lure = await StoreLure("Doomed", 1);

        await using (var session = _store.LightweightSession())
        {
            session.HardDelete<Lure>(lure.Id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowCountOf("fi_doc_lure")).ShouldBe(0);
    }

    [Fact]
    public async Task hard_delete_by_document_removes_the_row_outright()
    {
        var lure = await StoreLure("Doomed", 1);

        await using (var session = _store.LightweightSession())
        {
            session.HardDelete(lure);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowCountOf("fi_doc_lure")).ShouldBe(0);
    }

    /// <summary>
    ///     A second delete must not move <c>deleted_at</c> forward, or "deleted since" would be
    ///     answering about the most recent call rather than the deletion. Pinned against a planted
    ///     timestamp because two deletes in the same millisecond would agree either way.
    /// </summary>
    [Fact]
    public async Task deleting_an_already_deleted_document_leaves_its_deletion_time_alone()
    {
        var lure = await StoreLure("Twice", 1);
        await DeleteLure(lure.Id);

        const string planted = "2000-01-01T00:00:00.000Z";
        await ExecuteAsync($"update fi_doc_lure set deleted_at = '{planted}' where id = '{lure.Id}'");

        await DeleteLure(lure.Id);

        (await DeletionStateOf("fi_doc_lure", lure.Id)).DeletedAt.ShouldBe(planted);
    }

    [Fact]
    public async Task delete_where_flags_the_matching_rows_only()
    {
        await StoreLure("Deep", 10);
        await StoreLure("Shallow", 1);

        await using (var session = _store.LightweightSession())
        {
            session.DeleteWhere<Lure>(x => x.Depth > 5);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Query<Lure>().ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Shallow"]);

        // Flagged, not gone.
        (await RowCountOf("fi_doc_lure")).ShouldBe(2);
    }

    [Fact]
    public async Task hard_delete_where_removes_the_matching_rows()
    {
        await StoreLure("Deep", 10);
        await StoreLure("Shallow", 1);

        await using (var session = _store.LightweightSession())
        {
            session.HardDeleteWhere<Lure>(x => x.Depth > 5);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowCountOf("fi_doc_lure")).ShouldBe(1);
    }

    [Fact]
    public async Task delete_where_on_a_type_that_is_not_soft_deleted_removes_the_rows()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new Sinker { Id = Guid.NewGuid(), Grams = 20 });
            session.Store(new Sinker { Id = Guid.NewGuid(), Grams = 2 });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.DeleteWhere<Sinker>(x => x.Grams > 10);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await RowCountOf("fi_doc_sinker")).ShouldBe(1);
    }

    [Fact]
    public async Task undo_delete_where_brings_the_matching_rows_back()
    {
        var deep = await StoreLure("Deep", 10);
        await StoreLure("Shallow", 1);

        await using (var session = _store.LightweightSession())
        {
            session.DeleteWhere<Lure>(x => x.Depth > 0);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.UndoDeleteWhere<Lure>(x => x.Depth > 5);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Query<Lure>().ToListAsync(TestContext.Current.CancellationToken))
            .Select(x => x.Name).ShouldBe(["Deep"]);

        (await DeletionStateOf("fi_doc_lure", deep.Id)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task undo_delete_where_refuses_a_type_that_is_not_soft_deleted()
    {
        await using var session = _store.LightweightSession();

        var ex = Should.Throw<InvalidOperationException>(
            () => session.UndoDeleteWhere<Sinker>(x => x.Grams > 1));

        ex.Message.ShouldContain("Sinker");
        ex.Message.ShouldContain("SoftDeleted");
    }

    [Fact]
    public async Task a_soft_delete_operator_refuses_a_type_that_is_not_soft_deleted()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Sinker>().IsDeleted().ToListAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("Sinker");
    }

    [Fact]
    public async Task the_interface_opts_a_type_in_just_as_the_attribute_does()
    {
        var fly = new Fly { Id = Guid.NewGuid(), Pattern = "Adams" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(fly);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Delete(fly);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await DeletionStateOf("fi_doc_fly", fly.Id)).IsDeleted.ShouldBe(1);
        (await RowCountOf("fi_doc_fly")).ShouldBe(1);
    }

    [Fact]
    public async Task the_fluent_registration_opts_a_type_in_just_as_the_attribute_does()
    {
        var net = new Net { Id = Guid.NewGuid(), Mesh = "Fine" };

        await using (var session = _store.LightweightSession())
        {
            session.Store(net);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Delete<Net>(net.Id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await DeletionStateOf("fi_doc_net", net.Id)).IsDeleted.ShouldBe(1);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Net>(net.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // ---- helpers ----

    private async Task<Lure> StoreLure(string name, int depth)
    {
        var lure = new Lure { Id = Guid.NewGuid(), Name = name, Depth = depth };

        await using var session = _store.LightweightSession();
        session.Store(lure);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return lure;
    }

    private async Task DeleteLure(Guid id)
    {
        await using var session = _store.LightweightSession();
        session.Delete<Lure>(id);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return connection;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<string>> ColumnsOf(string table)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select name from pragma_table_info('{table}')";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<long> RowCountOf(string table)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The raw deletion columns, read outside Fisher — every read through Fisher filters the row
    ///     out, so this is the only way to see what a soft delete actually wrote.
    /// </summary>
    private async Task<(long IsDeleted, string? DeletedAt)> DeletionStateOf(string table, Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"select is_deleted, deleted_at from {table} where id = '{id}'";

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        return (reader.GetInt64(0), await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken)
            ? null
            : reader.GetString(1));
    }
}

[SoftDeleted]
public class Lure
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Depth { get; set; }
}

/// <summary>
///     Opted in by the shared marker interface. Its two members are <em>not</em> populated on read —
///     Fisher has no document metadata member mapping at all (fisher#11) — so the interface is an
///     opt-in and nothing more here.
/// </summary>
public class Fly : ISoftDeleted
{
    public Guid Id { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class Net
{
    public Guid Id { get; set; }
    public string Mesh { get; set; } = string.Empty;
}

public class Sinker
{
    public Guid Id { get; set; }
    public int Grams { get; set; }
}

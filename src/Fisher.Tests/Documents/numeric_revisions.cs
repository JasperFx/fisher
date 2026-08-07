using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#18 — a numeric <c>revision</c> column as the alternative to <c>guid_version</c>.
/// </summary>
/// <remarks>
///     <para>
///         What a revision buys over a Guid version is that it is <em>readable</em>: it crosses an API
///         boundary, and a caller can say "store this only if it is still revision 4". So the tests
///         are about the guard and about the value coming back, not about the column existing.
///     </para>
///     <para>
///         The risk in this feature is positional, not logical. Four statements each bind a different
///         number of revision slots — insert two, guarded upsert two plus four, overwrite two plus
///         two, update two plus two — and the shared numeric operations in Weasel.Storage bind by
///         position. A miscount does not throw; it writes the wrong number into the wrong column. That
///         is what the round-trip assertions are really for.
///     </para>
/// </remarks>
public class numeric_revisions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("numeric-revisions");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(_ => { });
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

    // ---- configuration ----

    [Fact]
    public void implementing_IRevisioned_turns_numeric_revisions_on()
    {
        var mapping = _store.Options.Schema.For<Permit>().Mapping;

        mapping.UseNumericRevisions.ShouldBeTrue();
        mapping.UseOptimisticConcurrency.ShouldBeFalse();
        mapping.ConcurrencyMode.ShouldBe(ConcurrencyMode.Numeric);
    }

    [Fact]
    public async Task the_table_carries_a_revision_column_and_not_a_guid_version()
    {
        // The table is created on the first write of a type the schema has mapped, so store one first.
        await StorePermitAsync("Trout, one rod");

        var columns = await ColumnNamesAsync("fi_doc_permit");

        columns.ShouldContain("revision");
        columns.ShouldNotContain("guid_version");
    }

    /// <summary>
    ///     The affinity matters: a TEXT column would sort revision 10 below revision 9 and turn the
    ///     "must be greater" guard into nonsense.
    /// </summary>
    [Fact]
    public async Task the_revision_column_is_an_integer()
    {
        await StorePermitAsync("Trout, one rod");

        var type = await ScalarAsync(
            "select type from pragma_table_xinfo('fi_doc_permit') where name = 'revision'");

        type.ShouldBe("INTEGER");
    }

    [Fact]
    public void asking_for_both_concurrency_styles_is_refused()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };
        options.Schema.For<Permit>().UseOptimisticConcurrency();

        var ex = Should.Throw<InvalidOperationException>(() =>
            options.Schema.For<Permit>().Mapping.AssertConcurrencyIsCoherent());

        ex.Message.ShouldContain("alternatives");
    }

    // ---- the happy path ----

    [Fact]
    public async Task a_new_document_starts_at_revision_one()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        permit.Version.ShouldBe(1);
        (await StoredRevisionAsync(permit.Id)).ShouldBe(1);
    }

    /// <summary>
    ///     <b>The sharp edge, and it is Marten's rather than Fisher's.</b> <c>Store</c> passes the
    ///     document's own <c>Version</c> as the expected revision — the docs put it as "<c>Store()</c> is
    ///     essentially <c>UpdateRevision(entity, entity.Version)</c>" — and the guard requires the
    ///     supplied revision to be strictly greater than the stored one. So re-storing an instance that
    ///     still carries the revision it was written at is a concurrency failure, not an increment.
    /// </summary>
    /// <remarks>
    ///     Pinned rather than smoothed over. Making plain <c>Store</c> auto-increment would be friendlier
    ///     and would silently disagree with Marten about what an explicit revision means; JasperFx's own
    ///     exception message names the fix, which is why it is asserted here.
    /// </remarks>
    [Fact]
    public async Task storing_an_instance_that_carries_its_current_revision_is_rejected()
    {
        var permit = await StorePermitAsync("Trout, one rod");
        permit.Version.ShouldBe(1);

        await using var session = _store.LightweightSession();
        permit.Description = "Trout, two rods";
        session.Store(permit);

        var ex = await Should.ThrowAsync<ConcurrencyException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("UpdateRevision");
    }

    /// <summary>
    ///     The supported way to move a loaded document forward: name the revision it is going to.
    /// </summary>
    [Fact]
    public async Task updating_at_the_next_revision_writes_it_back()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        permit.Description = "Trout, two rods";
        await using (var session = _store.LightweightSession())
        {
            session.UpdateRevision(permit, permit.Version + 1);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        permit.Version.ShouldBe(2);
        (await StoredRevisionAsync(permit.Id)).ShouldBe(2);
    }

    /// <summary>
    ///     A revision of zero means auto — increment whatever is stored — which is what a document with
    ///     no revision on it gets, and the escape hatch from the sharp edge above.
    /// </summary>
    [Fact]
    public async Task a_zero_revision_auto_increments()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        permit.Description = "Trout, two rods";
        permit.Version = 0;

        await using (var session = _store.LightweightSession())
        {
            session.Store(permit);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        permit.Version.ShouldBe(2);
        (await StoredRevisionAsync(permit.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task the_revision_comes_back_on_a_load()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using var session = _store.LightweightSession();
        var loaded = await session.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Version.ShouldBe(1);
    }

    /// <summary>
    ///     A query-only load returns the revision too, unlike a Guid version, which is dropped there.
    ///     The reason is asymmetric: the revision the caller will guard the next write with is the one
    ///     the database computed, so a read that withheld it would leave every explicit store guessing.
    /// </summary>
    [Fact]
    public async Task a_query_returns_the_revision_as_well()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using var session = _store.LightweightSession();
        var all = await session.Query<Permit>().ToListAsync(TestContext.Current.CancellationToken);

        all.Single(x => x.Id == permit.Id).Version.ShouldBe(1);
    }

    // ---- the guard ----

    [Fact]
    public async Task storing_at_a_stale_revision_fails_the_unit_of_work()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        // Someone else moves it to revision 2.
        await using (var other = _store.LightweightSession())
        {
            var theirs = await other.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken);
            theirs!.Description = "Pike";
            other.UpdateRevision(theirs, theirs.Version + 1);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session = _store.LightweightSession();
        session.Store(permit, revision: 1);

        await Should.ThrowAsync<ConcurrencyException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task storing_at_a_newer_revision_succeeds()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using var session = _store.LightweightSession();
        permit.Description = "Pike";
        session.Store(permit, revision: 5);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await StoredRevisionAsync(permit.Id)).ShouldBe(5);
    }

    /// <summary>
    ///     A stale write leaves the row exactly as it was — the guard matching nothing means the
    ///     statement touches no row, which is the same mechanism the Guid guard uses.
    /// </summary>
    [Fact]
    public async Task a_failed_guard_leaves_the_row_untouched()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using (var other = _store.LightweightSession())
        {
            var theirs = await other.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken);
            theirs!.Description = "Pike";
            other.UpdateRevision(theirs, theirs.Version + 1);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            permit.Description = "Salmon";
            session.Store(permit, revision: 1);

            await Should.ThrowAsync<ConcurrencyException>(async () =>
                await session.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        await using var check = _store.LightweightSession();
        var stored = await check.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken);

        stored!.Description.ShouldBe("Pike");
        stored.Version.ShouldBe(2);
    }

    [Fact]
    public async Task try_update_revision_drops_a_stale_write_without_failing()
    {
        var permit = await StorePermitAsync("Trout, one rod");
        var second = await StorePermitAsync("Pike, two rods");

        await using (var other = _store.LightweightSession())
        {
            var theirs = await other.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken);
            theirs!.Description = "Moved on";
            other.UpdateRevision(theirs, theirs.Version + 1);
            await other.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            permit.Description = "Stale";
            session.TryUpdateRevision(permit, 1);

            // ... and an unrelated write in the same unit of work still lands.
            second.Description = "Pike, three rods";
            session.UpdateRevision(second, second.Version + 1);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var check = _store.LightweightSession();
        (await check.LoadAsync<Permit>(permit.Id, TestContext.Current.CancellationToken))!
            .Description.ShouldBe("Moved on");
        (await check.LoadAsync<Permit>(second.Id, TestContext.Current.CancellationToken))!
            .Description.ShouldBe("Pike, three rods");
    }

    // ---- the other statements ----

    /// <summary>
    ///     Insert binds two revision slots and no guard; the assertion that matters is that a fresh row
    ///     lands at revision 1 rather than at whatever the two slots would produce if miscounted.
    /// </summary>
    [Fact]
    public async Task insert_starts_a_new_document_at_revision_one()
    {
        var permit = new Permit { Id = Guid.NewGuid(), Description = "Inserted" };

        await using var session = _store.LightweightSession();
        session.Insert(permit);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        permit.Version.ShouldBe(1);
        (await StoredRevisionAsync(permit.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task update_increments_the_revision()
    {
        var permit = await StorePermitAsync("Trout, one rod");

        await using var session = _store.LightweightSession();
        permit.Description = "Updated";
        permit.Version = 0;
        session.Update(permit);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await StoredRevisionAsync(permit.Id)).ShouldBe(2);
    }

    // ---- a type that opted in through the DSL rather than the interface ----

    [Fact]
    public async Task a_type_configured_through_the_dsl_gets_the_same_column()
    {
        await using var store = StoreFor(options =>
            options.Schema.For<Licence>().UseNumericRevisions());

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        (await ColumnNamesAsync("fi_doc_licence")).ShouldContain("revision");

        var licence = new Licence { Id = Guid.NewGuid(), Holder = "Isaak" };

        await using var session = store.LightweightSession();
        session.Store(licence);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // No IRevisioned member to project onto, so the value lives only in the column.
        (await StoredRevisionAsync(licence.Id, "fi_doc_licence")).ShouldBe(1);
    }

    // ---- helpers ----

    private async Task<Permit> StorePermitAsync(string description)
    {
        var permit = new Permit { Id = Guid.NewGuid(), Description = description };

        await using var session = _store.LightweightSession();
        session.Store(permit);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return permit;
    }

    private async Task<long> StoredRevisionAsync(Guid id, string table = "fi_doc_permit")
    {
        var raw = await ScalarAsync($"select revision from {table} where id = '{id}'");
        return Convert.ToInt64(raw);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> ColumnNamesAsync(string table)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select name from pragma_table_xinfo('{table}')";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

public class Permit : IRevisioned
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; }
}

public class Licence
{
    public Guid Id { get; set; }
    public string Holder { get; set; } = string.Empty;
}

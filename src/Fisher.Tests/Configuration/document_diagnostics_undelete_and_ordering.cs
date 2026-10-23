using JasperFx;
using JasperFx.Documents;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#471 — <c>UndeleteDocumentAsync</c> (jasperfx#1059) and <c>DocumentQueryOptions.OrderByMetadata</c>
///     (jasperfx#1058), on the shapes <c>DocumentStoreDiagnosticsCompliance</c> cannot reach.
/// </summary>
/// <remarks>
///     The shared suite's soft-deleted type carries no version column, is single-tenanted and has no
///     sub-classes, and its ordering facts can only check that a Guid version or an untracked created time is
///     refused. So the two real version columns, conjoined tenancy, a hierarchy, a byte-exact body, and the two
///     orderings Fisher <em>does</em> honour beyond <c>last_modified</c> are pinned here.
/// </remarks>
public class document_diagnostics_undelete_and_ordering : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("diag-undelete");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IDocumentStoreDiagnostics Diagnostics => _store;
    private IDocumentStoreDiagnosticsWriter Writer => _store;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<RestoredBuoy>().SoftDeleted().UseOptimisticConcurrency();
            options.Schema.For<RevisedLogbook>().SoftDeleted().UseNumericRevisions();
            options.Schema.For<RestoredMooring>().SoftDeleted().MultiTenanted()
                .Metadata(m => m.CreatedAt.Enabled = true);
            options.Schema.For<RestoredVessel>().SoftDeleted().AddSubClass<RestoredTrawler>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static string TypeName<T>() => typeof(T).FullName!;

    private async Task StoreAsync<T>(T document, string? tenantId = null) where T : notnull
    {
        await using var session = _store.LightweightSession(tenantId);
        session.Store(document);
        await session.SaveChangesAsync(Token);
    }

    private async Task DeleteAsync<T>(Guid id, string? tenantId = null) where T : notnull
    {
        await using var session = _store.LightweightSession(tenantId);
        session.Delete<T>(id);
        await session.SaveChangesAsync(Token);
    }

    // ---------------------------------------------------------------- undelete

    [Fact]
    public async Task an_undelete_moves_a_guid_version_on()
    {
        var buoy = new RestoredBuoy { Id = Guid.NewGuid(), Colour = "red" };
        await StoreAsync(buoy);
        await DeleteAsync<RestoredBuoy>(buoy.Id);

        var deleted = (await Diagnostics.LoadDocumentAsync(TypeName<RestoredBuoy>(), buoy.Id.ToString(), null, Token))!;

        var result = await Writer.UndeleteDocumentAsync(
            new DocumentUndeleteRequest(TypeName<RestoredBuoy>(), buoy.Id.ToString()) { ExpectedVersion = deleted.Version },
            Token);

        result.Status.ShouldBe(DocumentWriteStatus.Undeleted);
        var restored = result.Document.ShouldNotBeNull();
        restored.IsDeleted.ShouldBeFalse();
        restored.Version.ShouldNotBe(deleted.Version);
        Guid.TryParse(restored.Version, out _).ShouldBeTrue();

        // The new version is the one the session's guard now checks against: an application save that
        // carries it lands, which it could not if the column held something the guard never wrote.
        await using var session = _store.LightweightSession();
        var loaded = (await session.LoadAsync<RestoredBuoy>(buoy.Id, Token)).ShouldNotBeNull();
        loaded.Colour = "green";
        session.Store(loaded);
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task an_undelete_increments_a_numeric_revision()
    {
        var logbook = new RevisedLogbook { Id = Guid.NewGuid(), Entry = "fog" };
        await StoreAsync(logbook);
        await DeleteAsync<RevisedLogbook>(logbook.Id);

        var deleted = (await Diagnostics.LoadDocumentAsync(TypeName<RevisedLogbook>(), logbook.Id.ToString(), null, Token))!;

        var result = await Writer.UndeleteDocumentAsync(
            new DocumentUndeleteRequest(TypeName<RevisedLogbook>(), logbook.Id.ToString()), Token);

        result.Status.ShouldBe(DocumentWriteStatus.Undeleted);
        long.Parse(result.Document!.Version!).ShouldBe(long.Parse(deleted.Version!) + 1);
    }

    /// <remarks>
    ///     The restore clears the flags without rewriting <c>data</c>, so a member the deployment's type does
    ///     not declare — a newer deployment's, say — survives. A deserialize-and-store would drop it.
    /// </remarks>
    [Fact]
    public async Task an_undelete_leaves_the_body_byte_exact()
    {
        var buoy = new RestoredBuoy { Id = Guid.NewGuid(), Colour = "red" };
        await StoreAsync(buoy);

        await ExecuteAsync(
            $"update fi_doc_restoredbuoy set data = json_set(data, '$.addedLater', 'kept'), is_deleted = 1 where id = '{buoy.Id}'");
        var before = await ReadDataAsync(buoy.Id);

        (await Writer.UndeleteDocumentAsync(new DocumentUndeleteRequest(TypeName<RestoredBuoy>(), buoy.Id.ToString()), Token))
            .Status.ShouldBe(DocumentWriteStatus.Undeleted);

        (await ReadDataAsync(buoy.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task an_undelete_restores_only_the_named_tenants_row()
    {
        var id = Guid.NewGuid();
        await StoreAsync(new RestoredMooring { Id = id, Berth = "acme" }, "acme");
        await StoreAsync(new RestoredMooring { Id = id, Berth = "globex" }, "globex");
        await DeleteAsync<RestoredMooring>(id, "acme");
        await DeleteAsync<RestoredMooring>(id, "globex");

        var result = await Writer.UndeleteDocumentAsync(
            new DocumentUndeleteRequest(TypeName<RestoredMooring>(), id.ToString()) { TenantId = "acme" }, Token);

        result.Status.ShouldBe(DocumentWriteStatus.Undeleted);
        result.Document!.TenantId.ShouldBe("acme");

        (await Diagnostics.LoadDocumentAsync(TypeName<RestoredMooring>(), id.ToString(), "globex", Token))
            .ShouldNotBeNull().IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task an_undelete_through_a_sub_class_name_restores_the_sub_class()
    {
        var trawler = new RestoredTrawler { Id = Guid.NewGuid(), Name = "Kittiwake", Nets = 3 };
        await StoreAsync(trawler);
        await DeleteAsync<RestoredVessel>(trawler.Id);

        var result = await Writer.UndeleteDocumentAsync(
            new DocumentUndeleteRequest(TypeName<RestoredTrawler>(), trawler.Id.ToString()), Token);

        result.Status.ShouldBe(DocumentWriteStatus.Undeleted);
        result.Document!.DocumentType.ShouldBe(TypeName<RestoredTrawler>());

        await using var session = _store.QuerySession();
        (await session.LoadAsync<RestoredVessel>(trawler.Id, Token)).ShouldBeOfType<RestoredTrawler>().Nets.ShouldBe(3);
    }

    // ---------------------------------------------------------------- metadata ordering

    [Fact]
    public async Task a_numeric_revision_orders_by_version()
    {
        var logbooks = Enumerable.Range(0, 3).Select(i => new RevisedLogbook { Id = Guid.NewGuid(), Entry = $"#{i}" }).ToArray();
        foreach (var logbook in logbooks) await StoreAsync(logbook);

        // Two more writes to the first, so its revision is the highest.
        await using (var session = _store.LightweightSession())
        {
            session.UpdateRevision(logbooks[0], 2);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.UpdateRevision(logbooks[0], 3);
            await session.SaveChangesAsync(Token);
        }

        var result = await Diagnostics.QueryDocumentsAsync(TypeName<RevisedLogbook>(),
            new DocumentQueryOptions(1, 10) { OrderByMetadata = new DocumentMetadataOrder(DocumentMetadataField.Version, true) },
            Token);

        result.Documents[0].Id.ShouldBe(logbooks[0].Id.ToString());
        result.Documents.Select(x => long.Parse(x.Version!)).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task a_guid_version_is_refused_rather_than_ignored()
    {
        await StoreAsync(new RestoredBuoy { Id = Guid.NewGuid(), Colour = "red" });

        var refusal = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(TypeName<RestoredBuoy>(),
                new DocumentQueryOptions(1, 10) { OrderByMetadata = new DocumentMetadataOrder(DocumentMetadataField.Version) },
                Token));

        refusal.Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderByMetadata));
    }

    /// <remarks>
    ///     A tracked created time orders by first write, which an edit does not move, and under
    ///     <see cref="DocumentQueryOptions.AllTenants" /> the tenant still comes first.
    /// </remarks>
    [Fact]
    public async Task a_tracked_created_time_orders_within_each_tenant_under_all_tenants()
    {
        var first = new RestoredMooring { Id = Guid.NewGuid(), Berth = "1" };
        var second = new RestoredMooring { Id = Guid.NewGuid(), Berth = "2" };
        var other = new RestoredMooring { Id = Guid.NewGuid(), Berth = "3" };

        await StoreAsync(other, "zulu");
        await Task.Delay(5, Token);
        await StoreAsync(first, "alpha");
        await Task.Delay(5, Token);
        await StoreAsync(second, "alpha");
        await Task.Delay(5, Token);

        // Edited last, but created first: the created ordering does not move it.
        first.Berth = "edited";
        await StoreAsync(first, "alpha");

        var result = await Diagnostics.QueryDocumentsAsync(TypeName<RestoredMooring>(),
            new DocumentQueryOptions(1, 10)
            {
                AllTenants = true,
                OrderByMetadata = new DocumentMetadataOrder(DocumentMetadataField.Created, true)
            }, Token);

        result.Documents.Select(x => (x.TenantId, x.Id)).ShouldBe([
            ("alpha", second.Id.ToString()),
            ("alpha", first.Id.ToString()),
            ("zulu", other.Id.ToString())
        ]);
    }

    private async Task<string> ReadDataAsync(Guid id)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select data from fi_doc_restoredbuoy where id = '{id}'";
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}

public class RestoredBuoy
{
    public Guid Id { get; set; }
    public string Colour { get; set; } = string.Empty;
}

public class RevisedLogbook : JasperFx.IRevisioned
{
    public Guid Id { get; set; }
    public string Entry { get; set; } = string.Empty;
    public int Version { get; set; }
}

public class RestoredMooring
{
    public Guid Id { get; set; }
    public string Berth { get; set; } = string.Empty;
}

public class RestoredVessel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RestoredTrawler : RestoredVessel
{
    public int Nets { get; set; }
}

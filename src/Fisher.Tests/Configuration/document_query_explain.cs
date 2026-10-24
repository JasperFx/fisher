using JasperFx;
using JasperFx.Documents;

namespace Fisher.Tests.Configuration;

/// <summary>
///     jasperfx#1078: <c>IDocumentStoreDiagnostics.ExplainDocumentQueryAsync</c> — SQLite's
///     <c>EXPLAIN QUERY PLAN</c> over the page statement <c>QueryDocumentsAsync</c> runs, read into the
///     shared <c>DocumentQueryPlan</c>.
/// </summary>
/// <remarks>
///     The shared suite asserts only what every engine can say. What is Fisher's to pin is the reading of
///     SQLite's prose — which index a step names, and whether a step scans the document table — against a
///     table with a declared index, and that a type with no table yet is a plan of nothing.
/// </remarks>
public class document_query_explain : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("diag-explain");
    private readonly TemporaryDatabase _default = TemporaryDatabase.Create("diag-explain-default");
    private readonly TemporaryDatabase _north = TemporaryDatabase.Create("diag-explain-north");
    private DocumentStore _store = null!;
    private DocumentStore _perTenant = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;
    private IDocumentStoreDiagnostics Diagnostics => _store;

    private static readonly string LighthouseType = typeof(Lighthouse).FullName!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Lighthouse>().Index(x => x.Name);
            options.Schema.For<Beacon>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            for (var i = 0; i < 20; i++)
            {
                session.Store(new Lighthouse { Id = Guid.NewGuid(), Name = $"light-{i}", Code = $"C{i}" });
            }

            await session.SaveChangesAsync(Token);
        }

        _perTenant = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(tenants => tenants
                .AddTenant(StorageConstants.DefaultTenantId, _default.ConnectionString)
                .AddTenant("north", _north.ConnectionString));

            options.Schema.For<Lighthouse>().Index(x => x.Name);
        });

        await _perTenant.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var north = _perTenant.LightweightSession("north"))
        {
            north.Store(new Lighthouse { Id = Guid.NewGuid(), Name = "Fastnet" });
            await north.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _perTenant.DisposeAsync();
        _database.Dispose();
        _default.Dispose();
        _north.Dispose();
    }

    private async Task<string> DeclaredIndexNameAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name from sqlite_master where type = 'index' and sql is not null and tbl_name like '%lighthouse'";
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    [Fact]
    public async Task the_plan_is_sqlites_and_says_nothing_it_cannot()
    {
        var plan = (await Diagnostics.ExplainDocumentQueryAsync(LighthouseType, new DocumentQueryOptions(1, 10), Token))
            .ShouldNotBeNull();

        plan.Engine.ShouldBe("SQLite");
        plan.PlanText.ShouldStartWith("QUERY PLAN");
        plan.PlanText.ShouldContain("fi_doc_lighthouse");

        // SQLite's plan is prose with no row counts, and EXPLAIN QUERY PLAN executes nothing.
        plan.RowsExamined.ShouldBeNull();
        plan.RowsReturned.ShouldBeNull();
        plan.IsActual.ShouldBeFalse();
        plan.IndexesUsed.ShouldNotBeNull();
    }

    /// <summary>An unfiltered page reads every row — in id order, but every row.</summary>
    [Fact]
    public async Task an_unfiltered_page_scans_the_table()
    {
        var plan = await Diagnostics.ExplainDocumentQueryAsync(LighthouseType, new DocumentQueryOptions(1, 10), Token);

        plan!.ScansTable.ShouldBe(true);
    }

    [Fact]
    public async Task a_where_on_an_indexed_member_names_the_index_and_does_not_scan()
    {
        var plan = await Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { Where = "Name == @0", Arguments = ["light-3"] }, Token);

        plan!.IndexesUsed!.ShouldContain(await DeclaredIndexNameAsync());
        plan.ScansTable.ShouldBe(false);
    }

    [Fact]
    public async Task a_where_on_an_unindexed_member_scans_the_table()
    {
        var plan = await Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { Where = "Code == @0", Arguments = ["C3"] }, Token);

        plan!.ScansTable.ShouldBe(true);
        plan.IndexesUsed!.ShouldNotContain(await DeclaredIndexNameAsync());
    }

    /// <summary>The hand-built read, not only the criteria read: an id lookup is a primary-key search.</summary>
    [Fact]
    public async Task an_id_lookup_seeks_rather_than_scans()
    {
        var plan = await Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
            new DocumentQueryOptions(1, 10, IdEquals: Guid.NewGuid().ToString()), Token);

        plan!.ScansTable.ShouldBe(false);
        plan.IndexesUsed.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task an_unknown_type_is_null()
    {
        (await Diagnostics.ExplainDocumentQueryAsync("No.Such.Type", new DocumentQueryOptions(1, 10), Token))
            .ShouldBeNull();
    }

    /// <summary>
    ///     The query answers a type with no table with an empty page and runs nothing, so the plan is the
    ///     contract's plan of nothing — and asking must not create the table.
    /// </summary>
    [Fact]
    public async Task a_type_with_no_table_yet_is_a_plan_of_nothing_and_stays_tableless()
    {
        using var fresh = TemporaryDatabase.Create("diag-explain-fresh");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = fresh.ConnectionString;
            options.Schema.For<Beacon>();
        });

        var plan = (await ((IDocumentStoreDiagnostics)store).ExplainDocumentQueryAsync(typeof(Beacon).FullName!,
                new DocumentQueryOptions(1, 10), Token))
            .ShouldNotBeNull();
        plan.RowsExamined.ShouldBe(0);
        plan.RowsReturned.ShouldBe(0);
        plan.ScansTable.ShouldBe(false);
        plan.PlanText.ShouldStartWith("No query was executed");

        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name like '%beacon'";
        Convert.ToInt64(await command.ExecuteScalarAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task it_refuses_what_the_query_refuses()
    {
        var where = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { Where = "Name >>> nonsense (" }, Token));
        where.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));

        var metadata = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { CorrelationId = "c-1" }, Token));
        metadata.Criterion.ShouldBe(nameof(DocumentQueryOptions.CorrelationId));

        var ordering = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
                new DocumentQueryOptions(1, 10)
                {
                    OrderByMetadata = new DocumentMetadataOrder(DocumentMetadataField.Version, false)
                }, Token));
        ordering.Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderByMetadata));

        await Should.ThrowAsync<ArgumentException>(() =>
            Diagnostics.ExplainDocumentQueryAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { AllTenants = true, TenantId = "north" }, Token));
    }

    [Fact]
    public async Task a_tenant_scoped_explain_reads_the_tenants_own_file_and_all_tenants_explains_a_segment()
    {
        IDocumentStoreDiagnostics diagnostics = _perTenant;

        var north = await diagnostics.ExplainDocumentQueryAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { TenantId = "north" }, Token);
        north.ShouldNotBeNull().ScansTable.ShouldBe(true);

        var all = await diagnostics.ExplainDocumentQueryAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { AllTenants = true }, Token);
        all.ShouldNotBeNull().PlanText.ShouldContain("fi_doc_lighthouse");
    }

    /// <summary>
    ///     A scan of something other than the document table — the <c>json_each</c> an id list is read
    ///     through, say — is not a scan of the documents.
    /// </summary>
    [Fact]
    public void only_a_scan_of_the_document_table_reads_as_a_table_scan()
    {
        var mapping = _store.Options.Schema.MappingFor(typeof(Lighthouse));
        var table = mapping.TableName.Name;

        var seek = DocumentStore.PlanFrom(mapping,
        [
            new DocumentStore.ExplainedStep(3, 0, "SCAN json_each VIRTUAL TABLE INDEX 1:"),
            new DocumentStore.ExplainedStep(5, 0, $"SEARCH {table} USING INDEX sqlite_autoindex_{table}_1 (id=?)")
        ]);

        seek.ScansTable.ShouldBe(false);
        seek.IndexesUsed.ShouldBe([$"sqlite_autoindex_{table}_1"]);
        seek.PlanText.ShouldContain("|--SCAN json_each");

        var scan = DocumentStore.PlanFrom(mapping,
        [
            new DocumentStore.ExplainedStep(2, 0, $"SCAN {table} USING COVERING INDEX idx_x"),
            new DocumentStore.ExplainedStep(4, 2, "USE TEMP B-TREE FOR ORDER BY")
        ]);

        scan.ScansTable.ShouldBe(true);
        scan.IndexesUsed.ShouldBe(["idx_x"]);
        scan.PlanText.ShouldContain("   |--USE TEMP B-TREE");

        // A table whose name merely starts with this one's is somebody else's.
        DocumentStore.PlanFrom(mapping, [new DocumentStore.ExplainedStep(2, 0, $"SCAN {table}_archive")])
            .ScansTable.ShouldBe(false);
    }
}

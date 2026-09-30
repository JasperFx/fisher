using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;

namespace Fisher.Tests.Configuration;

/// <summary>
///     The parts of jasperfx#870's grown <c>IDocumentStoreDiagnostics</c> contract (fisher#364) that
///     <c>DocumentStoreDiagnosticsCompliance</c> structurally cannot reach.
/// </summary>
/// <remarks>
///     <para>
///         The shared suite runs one single-file store whose documents carry no version column, so four
///         things are Fisher's to pin: database-per-tenant targeting the tenant's own file, the two real
///         version columns standing in for the content-hash token, and the structured descriptors.
///     </para>
///     <para>
///         <b>Database-per-tenant is the one that was plainly wrong before.</b> The read used the store's
///         default database unconditionally, so a tenant-scoped query answered from the default file —
///         correct for whoever owned it, empty for everybody else, and silent either way.
///     </para>
/// </remarks>
public class document_diagnostics_contract : IAsyncLifetime
{
    private readonly TemporaryDatabase _default = TemporaryDatabase.Create("diag-default");
    private readonly TemporaryDatabase _north = TemporaryDatabase.Create("diag-north");
    private readonly TemporaryDatabase _single = TemporaryDatabase.Create("diag-single");

    private DocumentStore _perTenant = null!;
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _perTenant = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(tenants => tenants
                .AddTenant(StorageConstants.DefaultTenantId, _default.ConnectionString)
                .AddTenant("north", _north.ConnectionString));

            options.Schema.For<Lighthouse>();
        });

        await _perTenant.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _single.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Beacon>().UseOptimisticConcurrency();
            options.Schema.For<NauticalChart>().UseNumericRevisions();
            options.Schema.For<Lighthouse>()
                .Duplicate(x => x.Keeper)
                .Index(x => x.Name)
                .UniqueIndex(x => x.Code);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _perTenant.DisposeAsync();
        await _store.DisposeAsync();
        _default.Dispose();
        _north.Dispose();
        _single.Dispose();
    }

    private static readonly string LighthouseType = typeof(Lighthouse).FullName!;

    // ---------------------------------------------------------------- database-per-tenant

    private async Task<Lighthouse> StoreInNorthAsync()
    {
        var lighthouse = new Lighthouse { Id = Guid.NewGuid(), Name = "Fastnet", Code = "F1", Keeper = "ann" };

        await using var session = _perTenant.LightweightSession("north");
        session.Store(lighthouse);
        await session.SaveChangesAsync(Token);

        return lighthouse;
    }

    [Fact]
    public async Task a_tenant_scoped_query_reads_the_tenants_own_database()
    {
        var lighthouse = await StoreInNorthAsync();
        IDocumentStoreDiagnostics diagnostics = _perTenant;

        var north = await diagnostics.QueryDocumentsAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { TenantId = "north" }, Token);

        north.Documents.ShouldHaveSingleItem().Id.ShouldBe(lighthouse.Id.ToString());
        north.Documents[0].TenantId.ShouldBe("north");

        // The default file holds nothing, which is what the old read answered for every tenant.
        (await diagnostics.QueryDocumentsAsync(LighthouseType, new DocumentQueryOptions(1, 10), Token))
            .TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_load_reads_the_tenants_own_database()
    {
        var lighthouse = await StoreInNorthAsync();
        IDocumentStoreDiagnostics diagnostics = _perTenant;

        (await diagnostics.LoadDocumentAsync(LighthouseType, lighthouse.Id.ToString(), "north", Token))
            .ShouldNotBeNull().TenantId.ShouldBe("north");

        (await diagnostics.LoadDocumentAsync(LighthouseType, lighthouse.Id.ToString(), null, Token))
            .ShouldBeNull();
    }

    [Fact]
    public async Task a_save_lands_in_the_tenants_own_database()
    {
        var lighthouse = await StoreInNorthAsync();
        IDocumentStoreDiagnostics diagnostics = _perTenant;
        IDocumentStoreDiagnosticsWriter writer = _perTenant;

        var before = await diagnostics.LoadDocumentAsync(LighthouseType, lighthouse.Id.ToString(), "north", Token);
        var edited = before!.Json.Replace("Fastnet", "Fastnet Rock");

        var result = await writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest(LighthouseType, lighthouse.Id.ToString(), edited)
            {
                TenantId = "north",
                ExpectedVersion = before.Version
            }, Token);

        result.Status.ShouldBe(DocumentWriteStatus.Saved);

        await using (var north = _perTenant.QuerySession("north"))
        {
            (await north.LoadAsync<Lighthouse>(lighthouse.Id, Token))!.Name.ShouldBe("Fastnet Rock");
        }

        await using var fallback = _perTenant.QuerySession();
        (await fallback.LoadAsync<Lighthouse>(lighthouse.Id, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task an_unknown_tenant_is_refused_rather_than_answered_from_the_default_file()
    {
        IDocumentStoreDiagnostics diagnostics = _perTenant;

        await Should.ThrowAsync<Exception>(() => diagnostics.QueryDocumentsAsync(LighthouseType,
            new DocumentQueryOptions(1, 10) { TenantId = "atlantis" }, Token));
    }

    // ---------------------------------------------------------------- the version columns

    /// <remarks>
    ///     A Guid-versioned type's version IS <c>guid_version</c>, and an unconditional console save of
    ///     an existing row has to succeed — which it would not if the writer's session were left to
    ///     seed its guard from what it had read (fisher#245): it has read nothing.
    /// </remarks>
    [Fact]
    public async Task an_optimistic_types_version_is_its_guid_version_and_an_unguarded_save_succeeds()
    {
        var beacon = new Beacon { Id = Guid.NewGuid(), Light = "white" };
        await using (var session = _store.LightweightSession())
        {
            session.Store(beacon);
            await session.SaveChangesAsync(Token);
        }

        IDocumentStoreDiagnostics diagnostics = _store;
        IDocumentStoreDiagnosticsWriter writer = _store;

        var stored = await diagnostics.LoadDocumentAsync(typeof(Beacon).FullName!, beacon.Id.ToString(), null, Token);
        await using (var metadata = _store.QuerySession())
        {
            var version = (await metadata.MetadataForAsync<Beacon>(beacon.Id, Token))!.Version;
            stored!.Version.ShouldBe(version.ToString());
        }

        var unguarded = await writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest(typeof(Beacon).FullName!, beacon.Id.ToString(), stored!.Json.Replace("white", "red")),
            Token);

        unguarded.Status.ShouldBe(DocumentWriteStatus.Saved);
        unguarded.Document!.Version.ShouldNotBe(stored.Version);

        // …and the version the first save superseded is now stale.
        var stale = await writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest(typeof(Beacon).FullName!, beacon.Id.ToString(), stored.Json)
            {
                ExpectedVersion = stored.Version
            }, Token);

        stale.Status.ShouldBe(DocumentWriteStatus.ConcurrencyConflict);
        stale.Document!.Json.ShouldContain("red");
    }

    /// <remarks>
    ///     The numeric rule is Marten's: an explicit revision must be strictly greater than the stored
    ///     one. A console hands back the revision it read, so honouring that rule would refuse every
    ///     edit; the writer checks staleness against the column and then writes the revision as auto.
    /// </remarks>
    [Fact]
    public async Task a_numeric_revision_is_the_version_and_a_guarded_edit_is_not_refused_by_the_revision_rule()
    {
        var chart = new NauticalChart { Id = Guid.NewGuid(), Sheet = "one" };
        await using (var session = _store.LightweightSession())
        {
            session.Store(chart);
            await session.SaveChangesAsync(Token);
        }

        IDocumentStoreDiagnostics diagnostics = _store;
        IDocumentStoreDiagnosticsWriter writer = _store;

        var stored = await diagnostics.LoadDocumentAsync(typeof(NauticalChart).FullName!, chart.Id.ToString(), null, Token);
        stored!.Version.ShouldBe("1");

        var result = await writer.SaveDocumentJsonAsync(
            new DocumentWriteRequest(typeof(NauticalChart).FullName!, chart.Id.ToString(), stored.Json.Replace("one", "two"))
            {
                ExpectedVersion = stored.Version
            }, Token);

        result.Status.ShouldBe(DocumentWriteStatus.Saved);
        result.Document!.Version.ShouldBe("2");
    }

    // ---------------------------------------------------------------- criteria

    [Fact]
    public async Task criteria_are_refused_even_for_a_type_the_store_does_not_have()
    {
        // Refused first, so a predicate against an unknown type is not an empty page that reads as an
        // answer to it.
        IDocumentStoreDiagnostics diagnostics = _store;

        var refused = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            diagnostics.QueryDocumentsAsync("No.Such.Type", new DocumentQueryOptions(1, 10) { Where = "x > 1" }, Token));

        refused.Criterion.ShouldBe(nameof(DocumentQueryOptions.Where));
    }

    // ---------------------------------------------------------------- descriptors (§5)

    private async Task<DocumentMappingDescriptor> LighthouseDescriptorAsync()
    {
        var usage = await ((IDocumentStoreUsageSource)_store).TryCreateUsage(Token);
        return usage!.Documents.Single(x => x.DocumentType.Name == nameof(Lighthouse));
    }

    [Fact]
    public async Task usage_reports_the_serializer_casing()
    {
        var usage = await ((IDocumentStoreUsageSource)_store).TryCreateUsage(Token);

        usage!.SerializerCasing.ShouldBe(_store.Options.Serializer.Casing.ToString());
        usage.SerializerCasing.ShouldBe("CamelCase");
    }

    [Fact]
    public async Task usage_describes_duplicated_fields()
    {
        var field = (await LighthouseDescriptorAsync()).DuplicatedFields.ShouldHaveSingleItem();

        field.MemberPath.ShouldBe(nameof(Lighthouse.Keeper));
        field.ColumnName.ShouldBe("keeper");
        field.DbType.ShouldBe("TEXT");
    }

    [Fact]
    public async Task usage_describes_indexes_with_the_names_sqlite_holds()
    {
        var descriptor = await LighthouseDescriptorAsync();

        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'index' and sql is not null and tbl_name like '%lighthouse'";
        var actual = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token)) actual.Add(reader.GetString(0));
        }

        descriptor.Indexes.Select(x => x.Name).ShouldBe(actual, ignoreOrder: true);

        var byMember = descriptor.Indexes.ToDictionary(x => string.Join(",", x.Members));
        byMember[nameof(Lighthouse.Name)].IsUnique.ShouldBeFalse();
        byMember[nameof(Lighthouse.Code)].IsUnique.ShouldBeTrue();

        // A duplicated field's index is over its column, and that column IS the member.
        byMember[nameof(Lighthouse.Keeper)].Columns.ShouldBe(["keeper"]);
        descriptor.Indexes.ShouldAllBe(x => x.Method == null);
    }
}

public class Lighthouse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Keeper { get; set; } = string.Empty;
}

public class Beacon
{
    public Guid Id { get; set; }
    public string Light { get; set; } = string.Empty;
}

public class NauticalChart
{
    public Guid Id { get; set; }
    public string Sheet { get; set; } = string.Empty;
}

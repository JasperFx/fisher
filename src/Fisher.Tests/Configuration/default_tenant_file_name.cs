using JasperFx;

namespace Fisher.Tests.Configuration;

/// <summary>
///     What the default tenant's database file is called under directory tenancy (fisher#325).
/// </summary>
/// <remarks>
///     <para>
///         It used to be <c>*DEFAULT*.db</c> — the sentinel <c>StorageConstants.DefaultTenantId</c> taken
///         literally — and <c>*</c> is reserved in Windows file names. <b>The Windows run of this class
///         established it rather than assuming it</b>: the file could not be opened
///         (<c>SQLite Error 14: unable to open database file</c>), so directory tenancy did not work on
///         Windows at all. <c>.github/workflows/windows.yml</c> keeps running it there.
///     </para>
///     <para>
///         The new name, <c>(default).db</c>, is one no tenant id can produce. An existing
///         <c>*DEFAULT*.db</c> keeps being used, because renaming it would orphan every default-tenant
///         database written before this change.
///     </para>
/// </remarks>
public class default_tenant_file_name : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-default-tenant-" + Guid.NewGuid().ToString("n")[..8]);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held by a pooled connection; the directory is under temp.
        }

        return ValueTask.CompletedTask;
    }

    private DocumentStore Store() => DocumentStore.For(options =>
    {
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.MultiTenantedDatabasesInDirectory(_directory);
    });

    private async Task<Guid> WriteThroughTheDefaultTenantAsync(DocumentStore store)
    {
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Store(new ProbeNote { Id = id, Text = "written by the default tenant" });
        await session.SaveChangesAsync(Token);

        return id;
    }

    private string[] DatabaseFiles()
        => Directory.GetFiles(_directory, "*.db").Select(x => Path.GetFileName(x)!).ToArray();

    [Fact]
    public async Task a_new_store_names_the_default_tenants_file_safely_on_every_platform()
    {
        await using (var store = Store())
        {
            await WriteThroughTheDefaultTenantAsync(store);
        }

        DatabaseFiles().ShouldBe(["(default).db"]);
    }

    /// <remarks>
    ///     The migration half: a default-tenant database written before fisher#325 is still the one used,
    ///     rather than an empty new one appearing beside it. Unreachable on Windows, where the old file
    ///     could never have been created.
    /// </remarks>
    [Fact]
    public async Task an_existing_legacy_default_tenant_file_keeps_being_used()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "'*' is not a legal file name character on Windows.");

        Guid id;

        Directory.CreateDirectory(_directory);
        await using (var legacy = DocumentStore.For(options =>
                     {
                         options.AutoCreateSchemaObjects = AutoCreate.All;
                         options.MultiTenantedDatabases(tenants =>
                             tenants.AddTenant(StorageConstants.DefaultTenantId,
                                 $"Data Source={Path.Combine(_directory, $"{StorageConstants.DefaultTenantId}.db")}"));
                     }))
        {
            id = await WriteThroughTheDefaultTenantAsync(legacy);
        }

        await using (var store = Store())
        {
            await using var session = store.LightweightSession();
            (await session.LoadAsync<ProbeNote>(id, Token)).ShouldNotBeNull();
        }

        DatabaseFiles().ShouldBe([$"{StorageConstants.DefaultTenantId}.db"]);
    }

    [Fact]
    public async Task enumerating_the_directory_reports_the_default_tenant_by_its_id()
    {
        await using var store = Store();
        await WriteThroughTheDefaultTenantAsync(store);

        var tenants = await new Fisher.Storage.DirectoryTenantSource(_directory).AllAsync(Token);

        tenants.Select(x => x.TenantId).ShouldBe([StorageConstants.DefaultTenantId]);
    }

    /// <remarks>
    ///     The property the file name exists for: no caller's tenant id can name the default tenant's
    ///     file. A stem like <c>_default</c> would be a perfectly good tenant id, and two tenants would
    ///     then share one database.
    /// </remarks>
    [Fact]
    public async Task no_tenant_id_can_name_the_default_tenants_file()
    {
        await using var store = Store();

        Should.Throw<ArgumentException>(() => store.LightweightSession("(default)"));
    }
}

public class ProbeNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
}

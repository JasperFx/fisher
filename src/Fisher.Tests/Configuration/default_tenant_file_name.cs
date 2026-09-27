using JasperFx;

namespace Fisher.Tests.Configuration;

/// <summary>
///     What the default tenant's database file is called under directory tenancy (fisher#325).
/// </summary>
/// <remarks>
///     <para>
///         <c>DynamicTenancy.Default</c> resolves <c>StorageConstants.DefaultTenantId</c> — the literal
///         <c>*DEFAULT*</c> — through <c>DirectoryTenantSource.PathFor</c>, so the file is
///         <c>&lt;directory&gt;/*DEFAULT*.db</c>. <c>*</c> is reserved in Windows file names, and Fisher's
///         main CI runs on Linux only, so nothing would notice if that broke there.
///     </para>
///     <para>
///         <b>This is a probe before it is a regression test.</b> It pins the name as it is today and runs
///         on Windows through <c>.github/workflows/windows.yml</c>; whether it passes there is the fact
///         fisher#325 needs before anything is renamed, since renaming would orphan every existing
///         default-tenant database.
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

    [Fact]
    public async Task the_default_tenant_writes_to_a_file_named_for_the_sentinel()
    {
        await using (var store = DocumentStore.For(options =>
                     {
                         options.AutoCreateSchemaObjects = AutoCreate.All;
                         options.MultiTenantedDatabasesInDirectory(_directory);
                     }))
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = store.LightweightSession();
            session.Store(new ProbeNote { Id = Guid.NewGuid(), Text = "written by the default tenant" });
            await session.SaveChangesAsync(Token);
        }

        Directory.GetFiles(_directory, "*.db").Select(Path.GetFileName)
            .ShouldContain($"{StorageConstants.DefaultTenantId}.db");
    }
}

public class ProbeNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
}

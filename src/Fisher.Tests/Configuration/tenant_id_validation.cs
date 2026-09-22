using Fisher.Storage;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Configuration;

/// <summary>
///     A tenant id that would not be a safe file name is refused before it becomes one (fisher#318).
/// </summary>
/// <remarks>
///     <para>
///         <b>The hazard is two reasonable properties combining.</b> <c>DirectoryTenantSource</c>
///         resolves any tenant id at all — that is what makes a tenant appear at runtime with no
///         registration step — and <c>Path.Combine</c> does not constrain its result to the first
///         argument. Together they let <c>LightweightSession(tenantId)</c> <b>create a SQLite database
///         outside the configured directory</b>, at whatever the process can write.
///     </para>
///     <para>
///         <b>Every refusal here is asserted as a refusal AND as the file not existing.</b> Asserting
///         only the exception would pass against an implementation that threw after creating the file,
///         which is the failure that actually matters.
///     </para>
/// </remarks>
public class tenant_id_validation : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fisher-tenants-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DocumentStore StoreFor()
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabasesInDirectory(_root);
        });

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("a/../../escape")]
    [InlineData("nested/child")]
    [InlineData("nested\\child")]
    [InlineData("/rooted")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("null\0byte")]
    [InlineData("colon:name")]
    [InlineData("CON")]
    [InlineData("com1")]
    public void an_unsafe_tenant_id_is_refused_by_name(string tenantId)
    {
        using var store = StoreFor();

        var refusal = Should.Throw<ArgumentException>(() => store.LightweightSession(tenantId));

        refusal.Message.ShouldContain(tenantId.Replace("\0", "\0"), Case.Insensitive);
    }

    /// <summary>
    ///     The one that matters: nothing is written outside the directory.
    /// </summary>
    /// <remarks>
    ///     Resolving a tenant does not open a connection, so the refusal has to be reached before the
    ///     first session touches the file — this drives it all the way to a commit, and then looks for
    ///     the file the traversal was aiming at.
    /// </remarks>
    [Fact]
    public async Task a_traversing_tenant_id_creates_no_file_outside_the_directory()
    {
        var target = Path.Combine(Path.GetDirectoryName(_root)!, $"escaped-{Guid.NewGuid():n}");

        using var store = StoreFor();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await using var session = store.LightweightSession($"../{Path.GetFileName(target)}");
            session.Store(new TenantNote { Id = Guid.NewGuid(), Text = "escaped" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        File.Exists($"{target}.db").ShouldBeFalse();
    }

    /// <summary>
    ///     An absolute path replaces the directory outright rather than nesting under it, which is the
    ///     half a <c>..</c> check alone would miss.
    /// </summary>
    [Fact]
    public void a_rooted_tenant_id_does_not_replace_the_directory()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"fisher-elsewhere-{Guid.NewGuid():n}");

        using var store = StoreFor();

        Should.Throw<ArgumentException>(() => store.LightweightSession(elsewhere));

        File.Exists($"{elsewhere}.db").ShouldBeFalse();
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("ACME")]
    [InlineData("acme-co")]
    [InlineData("acme_co")]
    [InlineData("acme.co")]
    [InlineData("tenant-0123456789")]
    public async Task an_ordinary_tenant_id_still_works(string tenantId)
    {
        using var store = StoreFor();

        await using (var session = store.LightweightSession(tenantId))
        {
            session.Store(new TenantNote { Id = Guid.NewGuid(), Text = tenantId });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        File.Exists(Path.Combine(_root, $"{tenantId}.db")).ShouldBeTrue();
    }

    /// <summary>
    ///     The configuration path refuses the same ids as the runtime one.
    /// </summary>
    /// <remarks>
    ///     <c>TenantDatabases.InDirectory(...).AddTenants(...)</c> builds the same paths from a
    ///     different call site. A store that accepted an id in configuration and refused it at runtime
    ///     would be inconsistent about its own rule, and the failure would land wherever it was reached
    ///     second.
    /// </remarks>
    [Fact]
    public void the_configuration_convention_refuses_the_same_ids()
        => Should.Throw<ArgumentException>(() => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(databases => databases
                .InDirectory(_root)
                .AddTenants("good", "../escape"));
        }));

    [Fact]
    public void a_tenant_id_longer_than_a_file_name_is_refused()
    {
        using var store = StoreFor();

        Should.Throw<ArgumentException>(() => store.LightweightSession(new string('a', 300)));
    }
}

public class TenantNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

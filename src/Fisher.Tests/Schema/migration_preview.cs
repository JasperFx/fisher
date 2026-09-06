using JasperFx;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace Fisher.Tests.Schema;

/// <summary>
///     fisher#210 — the programmatic half of "preview and assert a migration".
/// </summary>
/// <remarks>
///     <para>
///         <b>fisher#172 closed the command-line half and only that half.</b> It registered
///         <c>ISystemPart</c> and <c>IDatabaseSource</c>, so <c>db-apply</c> / <c>db-assert</c> /
///         <c>db-patch</c> / <c>db-dump</c> see a Fisher store, and gave the store
///         <c>AssertDatabaseMatchesConfigurationAsync</c>. What an application still could not do was
///         ask for the delta as an object — <c>ToDatabaseScript()</c> describes the configuration and
///         never looks at the database — so "is there anything outstanding" and "what exactly would
///         change" meant shelling out to the CLI.
///     </para>
///     <para>
///         These tests <em>apply</em> the migrations they compute and read <c>sqlite_master</c>
///         afterwards, rather than asserting that a delta object came back non-null. A preview that
///         reported the right <see cref="SchemaPatchDifference" /> and rendered DDL that does not run
///         would pass every shape assertion and be worthless.
///     </para>
/// </remarks>
public class migration_preview : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("migration-preview");

    public ValueTask InitializeAsync() => default;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return default;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StoreFor(Action<StoreOptions>? configure = null) =>
        DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<PreviewTarget>();

            configure?.Invoke(options);
        });

    // ---- CreateMigrationAsync ----

    [Fact]
    public async Task an_unapplied_store_reports_a_create_difference_naming_its_tables()
    {
        await using var store = StoreFor();

        var migration = await store.Advanced.CreateMigrationAsync(Token);

        migration.Difference.ShouldBe(SchemaPatchDifference.Create);

        // Every feature the store configures, not just the document type that prompted the question.
        var updates = UpdateSql(migration, store);
        updates.ShouldContain("fi_events");
        updates.ShouldContain("fi_streams");
        updates.ShouldContain("fi_doc_previewtarget");
    }

    /// <remarks>
    ///     The property the whole surface rests on: a store whose schema is current has *nothing*
    ///     outstanding. Without this, "there is a migration" is true of every store forever and
    ///     `Difference` says nothing.
    /// </remarks>
    [Fact]
    public async Task an_applied_store_has_no_outstanding_migration()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var migration = await store.Advanced.CreateMigrationAsync(Token);

        migration.Difference.ShouldBe(SchemaPatchDifference.None);
        migration.Deltas.ShouldAllBe(x => x.Difference == SchemaPatchDifference.None);
    }

    /// <remarks>
    ///     The deployment shape this exists for: apply, then add a type, then ask what is outstanding
    ///     and get back <em>only</em> the new table rather than the whole schema.
    /// </remarks>
    [Fact]
    public async Task a_newly_registered_type_is_the_only_thing_outstanding()
    {
        await using (var first = StoreFor())
        {
            await first.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        await using var second = StoreFor(options => options.Schema.For<PreviewLater>());

        var migration = await second.Advanced.CreateMigrationAsync(Token);

        migration.Difference.ShouldBe(SchemaPatchDifference.Create);

        var outstanding = migration.Deltas
            .Where(x => x.Difference != SchemaPatchDifference.None)
            .Select(x => x.SchemaObject.Identifier.Name)
            .ToArray();

        outstanding.ShouldContain(x => x.Contains("previewlater", StringComparison.OrdinalIgnoreCase));
        outstanding.ShouldNotContain(x => x.Contains("previewtarget", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    ///     Computing a delta reads the schema and writes nothing, so the strictest configuration must
    ///     still be able to ask the question — that is the deployment `AutoCreate.None` describes.
    /// </remarks>
    [Fact]
    public async Task a_preview_is_available_under_auto_create_none()
    {
        await using var store = StoreFor(options => options.AutoCreateSchemaObjects = AutoCreate.None);

        var migration = await store.Advanced.CreateMigrationAsync(Token);

        migration.Difference.ShouldBe(SchemaPatchDifference.Create);
        (await TableExistsAsync("fi_events")).ShouldBeFalse();
    }

    // ---- WriteMigrationFileAsync ----

    /// <remarks>
    ///     The programmatic <c>db-patch</c>. Written, then <em>executed</em>, then the tables read
    ///     back — a patch file whose DDL does not run is the failure worth catching, and asserting
    ///     that the text contains a table name would not catch it.
    /// </remarks>
    [Fact]
    public async Task the_migration_file_is_ddl_that_actually_creates_the_schema()
    {
        var file = Path.Combine(Path.GetTempPath(), "fisher-preview-" + Guid.NewGuid().ToString("N") + ".sql");

        try
        {
            await using var store = StoreFor();
            await store.Advanced.WriteMigrationFileAsync(file, Token);

            File.Exists(file).ShouldBeTrue();

            await ExecuteAsync(await File.ReadAllTextAsync(file, Token));

            (await TableExistsAsync("fi_events")).ShouldBeTrue();
            (await TableExistsAsync("fi_doc_previewtarget")).ShouldBeTrue();

            // And having run it by hand, the store now agrees there is nothing left to do — which is
            // the assertion that says the patch was the *whole* delta rather than part of it.
            await store.AssertDatabaseMatchesConfigurationAsync(Token);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(SchemaMigration.ToDropFileName(file))) File.Delete(SchemaMigration.ToDropFileName(file));
        }
    }

    // ---- WriteScriptsByTypeAsync ----

    [Fact]
    public async Task scripts_by_type_writes_one_file_per_feature_plus_an_all_script()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-preview-scripts-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var store = StoreFor();
            await store.Advanced.WriteScriptsByTypeAsync(directory, Token);

            var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();

            files.ShouldContain("all.sql");
            files.ShouldContain(x => x!.Contains("previewtarget", StringComparison.OrdinalIgnoreCase));

            // The event store is a feature of its own, so it is its own script rather than being
            // folded into whichever document happened to be registered first.
            var eventScript = files.Single(x => x!.Contains("eventstore", StringComparison.OrdinalIgnoreCase));
            (await File.ReadAllTextAsync(Path.Combine(directory, eventScript!), Token)).ShouldContain("fi_events");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ---- AllObjects / AllSchemaNames ----

    [Fact]
    public async Task all_objects_describes_the_configured_schema_without_touching_the_database()
    {
        await using var store = StoreFor();

        var names = store.Advanced.AllObjects().Select(x => x.Identifier.Name).ToArray();

        names.ShouldContain("fi_events");
        names.ShouldContain("fi_streams");
        names.ShouldContain("fi_doc_previewtarget");

        // Read-only: nothing was created by asking.
        (await TableExistsAsync("fi_events")).ShouldBeFalse();
    }

    /// <remarks>
    ///     Pinned as a constant on purpose. SQLite has one schema and Fisher folds
    ///     <c>DatabaseSchemaName</c> into the table prefix instead, so the parity member answers
    ///     <c>main</c> whatever the logical schema is — and reinterpreting it as the prefix would make
    ///     it mean something different here than on Marten.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("reporting")]
    public async Task the_schema_names_are_always_main(string? schemaName)
    {
        await using var store = StoreFor(options =>
        {
            if (schemaName is not null) options.DatabaseSchemaName = schemaName;
        });

        store.Advanced.AllSchemaNames().ShouldBe(["main"]);

        // The isolation the logical schema actually buys is in the table names, which is the thing a
        // caller reaching for AllSchemaNames is usually after.
        var prefix = schemaName is null ? "fi_" : $"{schemaName}_fi_";
        store.Advanced.AllObjects().Select(x => x.Identifier.Name)
            .ShouldContain($"{prefix}events");
    }

    // ---- database-per-tenant ----

    /// <remarks>
    ///     <para>
    ///         Marten's <c>CreateMigrationAsync</c> is documented as single-tenant only. Under
    ///         database-per-tenant a Fisher store is N files, and collapsing them would answer about
    ///         whichever happened to be first — the same reason
    ///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c> reports per database.
    ///     </para>
    ///     <para>
    ///         One tenant is migrated and the other is not, so a preview that ignored its argument
    ///         would report identical answers for the two.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task previews_are_per_database_under_database_per_tenant()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-preview-tenants-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var store = DocumentStore.For(options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.MultiTenantedDatabases(x =>
                    x.InDirectory(directory).AddTenants("one", "two", "*DEFAULT*"));
                options.Schema.For<PreviewTarget>();
            });

            var all = await store.Advanced.CreateAllMigrationsAsync(Token);

            all.Keys.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["*DEFAULT*", "one", "two"]);
            all.Values.ShouldAllBe(x => x.Difference == SchemaPatchDifference.Create);

            // Migrate exactly one tenant's file, by hand, through the tenant-scoped API.
            await store.Tenancy.DatabaseFor("one").ApplyAllConfiguredChangesToDatabaseAsync(ct: Token);

            (await store.Advanced.CreateMigrationAsync("one", Token)).Difference
                .ShouldBe(SchemaPatchDifference.None);
            (await store.Advanced.CreateMigrationAsync("two", Token)).Difference
                .ShouldBe(SchemaPatchDifference.Create);

            var after = await store.Advanced.CreateAllMigrationsAsync(Token);
            after["one"].Difference.ShouldBe(SchemaPatchDifference.None);
            after["two"].Difference.ShouldBe(SchemaPatchDifference.Create);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    ///     An unknown tenant throws rather than quietly previewing the default database — the rule
    ///     every other tenant-scoped member on <c>AdvancedOperations</c> follows, and the one that
    ///     stops a per-tenant deployment check from silently checking the wrong file.
    /// </remarks>
    [Fact]
    public async Task an_unknown_tenant_is_refused_rather_than_answered_about_the_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-preview-unknown-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var store = DocumentStore.For(options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.MultiTenantedDatabases(x => x.InDirectory(directory).AddTenants("one", "*DEFAULT*"));
                options.Schema.For<PreviewTarget>();
            });

            await Should.ThrowAsync<Exception>(async () =>
                await store.Advanced.CreateMigrationAsync("nobody", Token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string UpdateSql(SchemaMigration migration, DocumentStore store)
    {
        var writer = new StringWriter();
        migration.WriteAllUpdates(writer, store.Database.Migrator, AutoCreate.All);

        return writer.ToString();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<bool> TableExistsAsync(string name)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", name);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token)) > 0;
    }
}

public class PreviewTarget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class PreviewLater
{
    public Guid Id { get; set; }
}

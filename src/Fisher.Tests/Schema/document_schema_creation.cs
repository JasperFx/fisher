using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Schema;

public class document_schema_creation : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("doc-schema");

    public ValueTask InitializeAsync() => default;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return default;
    }

    private FisherDatabase DatabaseFor(Action<StoreOptions> configure)
    {
        var options = new StoreOptions
        {
            ConnectionString = _database.ConnectionString,
            AutoCreateSchemaObjects = AutoCreate.All
        };

        configure(options);

        return new FisherDatabase(options);
    }

    private async Task<HashSet<string>> TableNamesAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<(string Name, string Type, bool NotNull, bool PrimaryKey)>> ColumnsAsync(string table)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = $"pragma table_info('{table}')";

        var columns = new List<(string, string, bool, bool)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1,
                reader.GetInt32(5) > 0));
        }

        return columns;
    }

    [Fact]
    public async Task creates_a_table_per_registered_document_type()
    {
        await using var database = DatabaseFor(x =>
        {
            x.Schema.For<DocUser>();
            x.Schema.For<DocInvoice>();
        });

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();

        tables.ShouldContain("fi_doc_docuser");
        tables.ShouldContain("fi_doc_docinvoice");
    }

    [Fact]
    public async Task an_unregistered_document_type_gets_no_table()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocUser>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        (await TableNamesAsync()).ShouldNotContain("fi_doc_docinvoice");
    }

    [Fact]
    public async Task the_default_column_shape_is_id_data_dotnet_type_and_last_modified()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocUser>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync("fi_doc_docuser");

        columns.Select(x => x.Name).ShouldBe(["id", "data", "dotnet_type", "last_modified"]);

        // A Guid id is TEXT, not BLOB — binding one without conversion would write 16 bytes that never
        // match the text the column holds.
        columns.Single(x => x.Name == "id").Type.ShouldBe("TEXT");
        columns.Single(x => x.Name == "id").PrimaryKey.ShouldBeTrue();
        columns.Single(x => x.Name == "data").NotNull.ShouldBeTrue();
    }

    [Fact]
    public async Task a_numeric_identity_gets_an_integer_id_column()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocInvoice>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync("fi_doc_docinvoice");
        columns.Single(x => x.Name == "id").Type.ShouldBe("INTEGER");
    }

    [Fact]
    public async Task a_numeric_identity_brings_the_hilo_table_with_it()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocInvoice>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        (await TableNamesAsync()).ShouldContain("fi_hilo");

        var columns = await ColumnsAsync("fi_hilo");
        columns.Select(x => x.Name).ShouldBe(["entity_name", "hi_value"]);

        // The primary key is what ON CONFLICT targets, which is what makes advancing the hi atomic.
        columns.Single(x => x.Name == "entity_name").PrimaryKey.ShouldBeTrue();
        columns.Single(x => x.Name == "hi_value").Type.ShouldBe("INTEGER");
    }

    [Fact]
    public async Task a_store_with_no_numeric_identity_gets_no_hilo_table()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocUser>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        (await TableNamesAsync()).ShouldNotContain("fi_hilo");
    }

    [Fact]
    public async Task the_hilo_table_takes_the_schema_name_prefix_too()
    {
        await using var database = DatabaseFor(x =>
        {
            x.DatabaseSchemaName = "billing";
            x.Schema.For<DocInvoice>();
        });

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();
        tables.ShouldContain("billing_fi_hilo");
        tables.ShouldNotContain("fi_hilo");
    }

    [Fact]
    public async Task optimistic_concurrency_adds_a_version_column()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocUser>().UseOptimisticConcurrency = true);
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync("fi_doc_docuser");
        columns.Select(x => x.Name).ShouldContain("guid_version");
    }

    [Fact]
    public async Task conjoined_tenancy_adds_a_tenant_id_column_to_the_primary_key()
    {
        await using var database = DatabaseFor(x =>
            x.Schema.For<DocUser>().TenancyStyle = TenancyStyle.Conjoined);

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync("fi_doc_docuser");

        // The same document id may exist once per tenant, so the key is the pair.
        columns.Where(x => x.PrimaryKey).Select(x => x.Name).ShouldBe(["tenant_id", "id"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_single_tenant_table_has_no_tenant_column_at_all()
    {
        await using var database = DatabaseFor(x => x.Schema.For<DocUser>());
        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        (await ColumnsAsync("fi_doc_docuser")).Select(x => x.Name).ShouldNotContain("tenant_id");
    }

    [Fact]
    public async Task document_tables_take_the_schema_name_prefix_like_every_other_table()
    {
        await using var database = DatabaseFor(x =>
        {
            x.DatabaseSchemaName = "reporting";
            x.Schema.For<DocUser>();
        });

        await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync();
        tables.ShouldContain("reporting_fi_doc_docuser");
        tables.ShouldNotContain("fi_doc_docuser");
    }

    [Fact]
    public void a_document_type_with_no_identity_member_is_rejected()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        var ex = Should.Throw<InvalidOperationException>(() => options.Schema.For<DocWithoutId>());
        ex.Message.ShouldContain("no identity member");
    }

    [Fact]
    public void an_unsupported_identity_type_is_rejected()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };

        var ex = Should.Throw<InvalidOperationException>(() => options.Schema.For<DocWithDateId>());
        ex.Message.ShouldContain("cannot store");
    }
}

public class DocUser
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DocInvoice
{
    public long Id { get; set; }
    public decimal Amount { get; set; }
}

public class DocWithoutId
{
    public string Name { get; set; } = string.Empty;
}

public class DocWithDateId
{
    public DateTime Id { get; set; }
}

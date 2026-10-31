using JasperFx;
using JasperFx.Documents;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

public class Bollard
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
///     fisher#507. A type with no version column gets a content hash at millisecond precision, so an
///     undelete and re-delete inside one millisecond reproduced the old token and a stale version was
///     accepted. These plant the timestamp rather than racing for that millisecond, so the old code fails
///     every run. The shared suite's <c>undelete_with_a_stale_version_is_refused_and_stays_deleted</c> is
///     what found it, about one run in three.
/// </summary>
public class undelete_moves_the_version : IAsyncLifetime
{
    private const string Later = "2999-01-01T00:00:00.000Z";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("undelete-version");
    private readonly Guid _id = Guid.NewGuid();
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string TypeName => typeof(Bollard).FullName!;

    private IDocumentStoreDiagnosticsWriter Writer => _store;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Bollard>().SoftDeleted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(new Bollard { Id = _id, Name = "North" });
        await session.SaveChangesAsync(Token);

        session.Delete<Bollard>(_id);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<string> LastModifiedAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select last_modified from fi_doc_bollard";

        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task PlantLastModifiedAsync(string value)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "update fi_doc_bollard set last_modified = @value";
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task UndeleteAsync()
    {
        var result = await Writer.UndeleteDocumentAsync(new DocumentUndeleteRequest(TypeName, _id.ToString()), Token);

        result.Status.ShouldBe(DocumentWriteStatus.Undeleted);
    }

    /// <summary>"Now" is behind the stored value, which is as far as a clock can fail to move.</summary>
    [Fact]
    public async Task a_restore_moves_last_modified_past_a_value_now_has_not_reached()
    {
        await PlantLastModifiedAsync(Later);

        await UndeleteAsync();

        (await LastModifiedAsync()).ShouldBe("2999-01-01T00:00:00.001Z");
    }

    [Fact]
    public async Task an_ordinary_restore_is_stamped_now()
    {
        var before = await LastModifiedAsync();
        var sampled = DateTimeOffset.UtcNow.AddMinutes(1);

        await UndeleteAsync();

        var after = await LastModifiedAsync();

        string.CompareOrdinal(after, before).ShouldBeGreaterThan(0);
        DateTimeOffset.Parse(after).ShouldBeLessThan(sampled);
    }

    /// <summary>The value stays <c>SqliteTimestamp</c>'s fixed-width form, which everything compares as text.</summary>
    [Fact]
    public async Task the_restored_value_keeps_the_stored_format()
    {
        await UndeleteAsync();

        (await LastModifiedAsync()).ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
    }

    /// <summary>The whole cycle, as the shared fact runs it, from a stored time now has not reached.</summary>
    [Fact]
    public async Task a_token_from_before_the_cycle_is_refused_afterwards()
    {
        await PlantLastModifiedAsync(Later);

        var stale = (await ((IDocumentStoreDiagnostics)_store).LoadDocumentAsync(TypeName, _id.ToString(), null, Token))!;

        await UndeleteAsync();

        (await Writer.DeleteDocumentAsync(new DocumentDeleteRequest(TypeName, _id.ToString()), Token))
            .Status.ShouldBe(DocumentWriteStatus.Deleted);

        var result = await Writer.UndeleteDocumentAsync(
            new DocumentUndeleteRequest(TypeName, _id.ToString()) { ExpectedVersion = stale.Version }, Token);

        result.Status.ShouldBe(DocumentWriteStatus.ConcurrencyConflict);
    }
}

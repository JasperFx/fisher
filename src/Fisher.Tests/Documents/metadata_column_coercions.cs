using System.Globalization;
using Fisher.Storage;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     What Microsoft.Data.Sqlite's <c>GetFieldValue&lt;T&gt;</c> does with the exact shapes Fisher
///     writes into its four mappable metadata columns.
/// </summary>
/// <remarks>
///     <para>
///         Fisher's own row readers convert explicitly in both directions and never call a provider
///         convenience method — see the "Row readers" note in CLAUDE.md. Weasel's document metadata
///         binders do the opposite: <c>DocumentVersionBinder</c> reads <c>GetFieldValue&lt;Guid&gt;</c>,
///         <c>DocumentSoftDeletedBinder</c> reads <c>GetFieldValue&lt;bool&gt;</c>, and both
///         <c>DocumentLastModifiedBinder</c> and <c>DocumentSoftDeletedAtBinder</c> read
///         <c>GetFieldValue&lt;DateTimeOffset&gt;</c>.
///     </para>
///     <para>
///         Mapping a metadata column onto a document member (fisher#11) is what first makes those read
///         paths run at all — with a null member every one of them returns before touching the reader.
///         So the whole feature rests on coercions Fisher does not own, over storage formats Fisher
///         does. These tests pin that seam directly, without any of the mapping machinery in the way,
///         so a provider upgrade that changes one of them fails here and names the column rather than
///         presenting as a document member that quietly stopped being populated.
///     </para>
/// </remarks>
public class metadata_column_coercions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("metadata_coercions");
    private SqliteConnection _connection = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection(_database.ConnectionString);
        await _connection.OpenAsync(TestContext.Current.CancellationToken);

        await ExecuteAsync(
            """
            create table probe (
                guid_version TEXT,
                last_modified TEXT,
                is_deleted INTEGER,
                deleted_at TEXT
            )
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     <c>guid_version</c> holds the lowercase canonical form every other Guid column holds — see
    ///     <c>SqliteGuidIdentification</c> and the casing trap in CLAUDE.md.
    /// </summary>
    [Fact]
    public async Task a_guid_version_reads_back_through_the_provider()
    {
        var version = Guid.NewGuid();
        await InsertAsync("guid_version", version.ToString("D").ToLowerInvariant());

        var read = await ReadAsync<Guid>("guid_version");

        read.ShouldBe(version);
    }

    /// <summary>
    ///     The uppercase spelling a raw <c>Guid</c> parameter would have written. It must read back as
    ///     the same value — otherwise the casing trap would extend from matching into reading, and a
    ///     row written by some other path would populate the member with garbage rather than fail.
    /// </summary>
    [Fact]
    public async Task a_guid_version_reads_back_regardless_of_casing()
    {
        var version = Guid.NewGuid();
        await InsertAsync("guid_version", version.ToString("D").ToUpperInvariant());

        var read = await ReadAsync<Guid>("guid_version");

        read.ShouldBe(version);
    }

    /// <summary>
    ///     Booleans are INTEGER 0/1 in Fisher, not a native type.
    /// </summary>
    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    public async Task a_soft_delete_flag_reads_back_through_the_provider(long stored, bool expected)
    {
        await InsertAsync("is_deleted", stored);

        var read = await ReadAsync<bool>("is_deleted");

        read.ShouldBe(expected);
    }

    /// <summary>
    ///     <c>last_modified</c> is written by <see cref="SqliteTimestamp.NowExpression" /> server-side,
    ///     so the stored text is whatever <c>strftime</c> produced — the same fixed-width UTC shape
    ///     <see cref="SqliteTimestamp.Format" /> renders, and the provider has to read it as an instant
    ///     rather than as a local time.
    /// </summary>
    [Fact]
    public async Task a_server_written_last_modified_reads_back_as_utc()
    {
        await ExecuteAsync($"insert into probe (last_modified) values ({SqliteTimestamp.NowExpression})");

        var stored = (string)(await ScalarAsync("select last_modified from probe"))!;
        var read = await ReadAsync<DateTimeOffset>("last_modified");

        read.Offset.ShouldBe(TimeSpan.Zero);
        read.ShouldBe(SqliteTimestamp.FromDatabaseValue(stored));
    }

    /// <summary>
    ///     <c>deleted_at</c> is written client-side through
    ///     <see cref="SqliteTimestamp.ToDatabaseValue" />, and must survive the round trip to the
    ///     millisecond — <c>DeletedSince</c> / <c>DeletedBefore</c> compare the column as text, so a
    ///     member that disagreed with the column would be answering a different question.
    /// </summary>
    [Fact]
    public async Task a_client_written_deleted_at_round_trips_to_the_millisecond()
    {
        var when = new DateTimeOffset(2026, 8, 6, 14, 22, 9, 456, TimeSpan.Zero);
        await InsertAsync("deleted_at", SqliteTimestamp.ToDatabaseValue(when));

        var read = await ReadAsync<DateTimeOffset>("deleted_at");

        read.ToUniversalTime().ShouldBe(when);
    }

    /// <summary>
    ///     A non-UTC instant is normalised on the way in, so what comes back is the same moment rather
    ///     than the same clock reading.
    /// </summary>
    [Fact]
    public async Task a_deleted_at_written_from_a_non_utc_offset_reads_back_as_the_same_instant()
    {
        var when = new DateTimeOffset(2026, 8, 6, 9, 22, 9, 456, TimeSpan.FromHours(-5));
        await InsertAsync("deleted_at", SqliteTimestamp.ToDatabaseValue(when));

        var read = await ReadAsync<DateTimeOffset>("deleted_at");

        read.ToUniversalTime().ShouldBe(when.ToUniversalTime());
    }

    /// <summary>
    ///     A live document's <c>deleted_at</c> is null, and the binder short-circuits on
    ///     <c>IsDBNull</c> before it ever coerces — pinned so the null path is covered by something
    ///     other than inspection.
    /// </summary>
    [Fact]
    public async Task a_null_deleted_at_is_seen_as_null()
    {
        await InsertAsync("deleted_at", null);

        await using var command = _connection.CreateCommand();
        command.CommandText = "select deleted_at from probe";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.IsDBNull(0).ShouldBeTrue();
    }

    private async Task InsertAsync(string column, object? value)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"insert into probe ({column}) values (@value)";
        command.Parameters.AddWithValue("@value", value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<T> ReadAsync<T>(string column)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"select {column} from probe";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        return reader.GetFieldValue<T>(0);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

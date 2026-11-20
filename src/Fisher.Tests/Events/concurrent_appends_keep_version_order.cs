using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record LedgerPosted(int Writer, int Round);

/// <summary>
///     The parity check for marten#6097 / marten#6098: under concurrent writers, Marten's Rich append could give a
///     stream's next version a lower <c>seq_id</c> than the version before it, so the stream read back out of
///     version order.
/// </summary>
/// <remarks>
///     <para>
///         Marten reserved the sequence numbers and read the stream versions in a batch that ran before it locked
///         the streams, so a writer could commit between the two. Fisher cannot get there. Its append opens
///         <c>BEGIN IMMEDIATE</c>, which takes SQLite's one write lock before the append planner reads any stream's
///         version, and <c>seq_id</c> is the table's <c>AUTOINCREMENT</c> key, assigned by the insert inside that
///         same transaction. A writer that commits first commits before the next one reads, so its versions and its
///         sequences are both lower. <c>ReadUncommitted</c>, the one isolation level that would begin a deferred
///         transaction, is refused by <c>SessionOptions</c>.
///     </para>
///     <para>
///         This is Marten's reproduction (Bug_6083's opposite-order writers, no expected versions) run against
///         Fisher, checking each stream in <c>seq_id</c> order rather than through a read that sorts by version.
///     </para>
/// </remarks>
public class concurrent_appends_keep_version_order : IAsyncLifetime
{
    private const int Writers = 8;
    private const int Rounds = 15;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("concurrent-append-order");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType(typeof(LedgerPosted));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task each_streams_seq_id_order_is_its_version_order()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                await using var session = _store.LightweightSession();

                // Half the writers append in the opposite order, as Bug_6083 does
                var (a, b) = writer % 2 == 0 ? (first, second) : (second, first);
                session.Events.Append(a, new LedgerPosted(writer, round));
                session.Events.Append(b, new LedgerPosted(writer, round));

                await session.SaveChangesAsync(Token);
            }
        }, Token)));

        foreach (var stream in new[] { first, second })
        {
            (await versionsInSequenceOrderAsync(stream))
                .ShouldBe(Enumerable.Range(1, Writers * Rounds).Select(x => (long)x));
        }
    }

    private async Task<List<long>> versionsInSequenceOrderAsync(Guid streamId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select version from fi_events where stream_id = @id order by seq_id";
        command.Parameters.AddWithValue("@id", streamId.ToString());

        var versions = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }
}

using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     What a caller sees when an append loses SQLite's single write lock for the database file
///     (fisher#306).
/// </summary>
/// <remarks>
///     <para>
///         <b>Contention is driven for real, not planted.</b> A blocker connection holds
///         <c>BEGIN IMMEDIATE</c> for the whole of the commit under test, which is the only way to
///         reach the wait Fisher actually pays: a contended writer sits inside
///         <c>BeginTransactionAsync</c> under the busy timeout, not in a Polly retry — the
///         measurement <c>otel_counters</c> records, and the reason a retry counter alone reads zero
///         through real contention.
///     </para>
///     <para>
///         <b>The store is built with no retries and a short busy timeout</b>, so the pipeline gives
///         up in one attempt rather than six and the test costs a fraction of a second instead of
///         most of a minute. That is the boundary under test either way — this exception is what a
///         caller gets <em>after</em> the retries, and how many there were is
///         <c>FisherResilienceDefaults</c>' business.
///     </para>
/// </remarks>
public class stream_locked_exception : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("stream-locked");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            // A short timeout on both halves of the wait. PRAGMA busy_timeout bounds SQLite's own
            // retry loop; the connection string's Default Timeout bounds the provider's loop around
            // BEGIN, and it is the one that covers BEGIN IMMEDIATE — both have to be short or the
            // contended commit sits for thirty seconds before failing.
            options.ConnectionString =
                new SqliteConnectionStringBuilder(_database.ConnectionString) { DefaultTimeout = 1 }
                    .ToString();

            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.PragmaSettings.BusyTimeout = 250;
            options.Schema.For<Landing>();

            // No retries: this test is about the translation at the boundary, and the retry policy
            // has its own coverage in `tracing` and `otel_counters`.
            options.ConfigurePolly(_ => { });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     Queue work onto a session, then commit it while another connection holds the file's write
    ///     lock, and hand back what came out.
    /// </summary>
    /// <remarks>
    ///     <b>The session is opened and warmed before the blocker starts, and it has to be.</b>
    ///     Opening a Fisher connection applies the store's PRAGMAs, and <c>journal_mode</c> wants the
    ///     write lock itself — so a session that first touched the file under contention fails while
    ///     <em>opening</em>, a long way from the commit path this is about, and the raw
    ///     <c>SqliteException</c> escapes untranslated because it never reached it. The warm-up read
    ///     also settles the on-demand document-table migration, which runs on its own connection
    ///     before the transaction opens.
    /// </remarks>
    private async Task<Exception> CommitUnderContentionAsync(Func<IDocumentSession, Task> arrange)
    {
        await using var session = _store.LightweightSession();

        await session.LoadAsync<Landing>(Guid.NewGuid(), Token);
        await arrange(session);

        await using var blocker = new SqliteConnection(_database.ConnectionString);
        await blocker.OpenAsync(Token);

        await using var held = (SqliteTransaction)await blocker.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, Token);

        // Make the blocker a real writer. BEGIN IMMEDIATE takes the lock, but writing through it is
        // what makes the state under test unambiguous rather than dependent on when SQLite escalates.
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = held;
            command.CommandText = "update fi_event_progression set last_seq_id = last_seq_id";
            await command.ExecuteNonQueryAsync(Token);
        }

        var exception = await Should.ThrowAsync<Exception>(() => session.SaveChangesAsync(Token));

        await held.RollbackAsync(Token);

        return exception;
    }

    /// <remarks>
    ///     Asserted through the <em>shared</em> type, because that is the whole point: a Wolverine
    ///     <c>OnException&lt;JasperFx.Events.StreamLockedException&gt;().RetryWithCooldown(...)</c>
    ///     policy matches with <c>ex is T</c>, and before this it matched on Marten and Polecat and
    ///     not here.
    /// </remarks>
    [Fact]
    public async Task a_contended_append_surfaces_as_a_stream_locked_exception()
    {
        var streamId = Guid.NewGuid();

        var exception = await CommitUnderContentionAsync(session =>
        {
            session.Events.StartStream(streamId, new Landed("Kelp Bay"));
            return Task.CompletedTask;
        });

        var locked = exception.ShouldBeOfType<Fisher.Exceptions.StreamLockedException>();

        locked.ShouldBeAssignableTo<JasperFx.Events.StreamLockedException>();
        locked.StreamId.ShouldBe(streamId);
        locked.StreamIds.ShouldBe([streamId]);
    }

    /// <remarks>
    ///     The driver's account of what SQLite refused is the thing a support ticket needs, and it is
    ///     carried rather than replaced — the same discipline every other translated exception here
    ///     follows.
    /// </remarks>
    [Fact]
    public async Task the_raw_sqlite_failure_is_kept_as_the_inner_exception()
    {
        var exception = await CommitUnderContentionAsync(session =>
        {
            session.Events.StartStream(Guid.NewGuid(), new Landed("Kelp Bay"));
            return Task.CompletedTask;
        });

        var inner = exception.InnerException.ShouldBeOfType<SqliteException>();

        // 5 is SQLITE_BUSY, 6 is SQLITE_LOCKED — the two FisherResilienceDefaults treats as transient.
        inner.SqliteErrorCode.ShouldBeOneOf(5, 6);
    }

    /// <remarks>
    ///     SQLite locks the <em>file</em>, not a row, so every stream in the unit of work lost
    ///     together — which is the one place this means something different than it does on Marten
    ///     and Polecat, where the lock really is one stream's. The base's single <c>StreamId</c> is
    ///     the first of them, so the aggregate-handler shape reads identically on all three.
    /// </remarks>
    [Fact]
    public async Task every_stream_in_the_unit_of_work_is_named()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var exception = await CommitUnderContentionAsync(session =>
        {
            session.Events.StartStream(first, new Landed("Kelp Bay"));
            session.Events.StartStream(second, new Landed("Cold Shoal"));
            return Task.CompletedTask;
        });

        var locked = exception.ShouldBeOfType<Fisher.Exceptions.StreamLockedException>();

        locked.StreamIds.ShouldBe([first, second], ignoreOrder: true);
        locked.StreamId.ShouldBeOneOf(first, second);
    }

    /// <remarks>
    ///     A <c>StreamLockedException</c> naming no stream would be a worse answer than the driver's,
    ///     so a unit of work with no append keeps the raw one. This is the discriminating fact:
    ///     translating unconditionally passes every test above and fails only this.
    /// </remarks>
    [Fact]
    public async Task a_documents_only_commit_keeps_the_raw_sqlite_exception()
    {
        var exception = await CommitUnderContentionAsync(session =>
        {
            session.Store(new Landing { Id = Guid.NewGuid(), Water = "Kelp Bay" });
            return Task.CompletedTask;
        });

        exception.ShouldBeOfType<SqliteException>();
    }
}

public record Landed(string Water);

public class Landing
{
    public Guid Id { get; set; }
    public string Water { get; set; } = string.Empty;
}

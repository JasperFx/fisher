using JasperFx.Events.Daemon;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     Which SQLite failures <see cref="TransientFailures" /> labels as expected to clear on their own,
///     and so which paused shards a supervisor may restart (wolverine#4986, jasperfx#1016).
/// </summary>
/// <remarks>
///     <para>
///         <b>The faults are produced for real, not constructed</b>, because the thing under test is what
///         Microsoft.Data.Sqlite actually hands back: it does not implement <c>DbException.IsTransient</c>,
///         so a real <c>SQLITE_BUSY</c> carries <c>IsTransient == false</c> and only Fisher's classifier
///         turns it around. Each probe uses a raw connection and a one-second provider timeout — in
///         Microsoft.Data.Sqlite a timeout of 0 means wait forever, not fail fast.
///     </para>
///     <para>
///         <b>The classifier is registered by building a store</b>, not by calling it directly, because
///         "every store goes through the constructor" is the claim. Removing that one line fails the
///         contention probes here and nothing else.
///     </para>
/// </remarks>
public class sqlite_transient_failures : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("transient-failures");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string ShortTimeout =>
        new SqliteConnectionStringBuilder(_database.ConnectionString) { DefaultTimeout = 1, Pooling = false }
            .ToString();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(_database.ConnectionString);

        await using var connection = new SqliteConnection(ShortTimeout);
        await connection.OpenAsync(Token);
        await executeAsync(connection, "create table catch (id integer primary key, water text)");
        await executeAsync(connection, "insert into catch (water) values ('Kelp Bay'), ('Cold Shoal'), ('Deep Ledge')");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_write_against_an_exclusive_lock_is_transient()
    {
        var busy = await busyAsync();

        // The probe that motivates the classifier: the driver itself does not say so.
        busy.SqliteErrorCode.ShouldBe(5);
        busy.IsTransient.ShouldBeFalse();

        TransientFailures.IsTransient(busy).ShouldBeTrue();
    }

    [Fact]
    public async Task the_shard_failure_for_a_busy_database_is_labelled_transient()
    {
        var busy = await busyAsync();

        var failure = ShardFailure.For(busy, DateTimeOffset.UtcNow);

        failure.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task a_missing_column_is_not_transient()
    {
        await using var connection = new SqliteConnection(ShortTimeout);
        await connection.OpenAsync(Token);

        var missing = await Should.ThrowAsync<SqliteException>(
            () => executeAsync(connection, "select no_such_column from catch"));

        missing.SqliteErrorCode.ShouldBe(1);
        TransientFailures.IsTransient(missing).ShouldBeFalse();
        ShardFailure.For(missing, DateTimeOffset.UtcNow).IsTransient.ShouldBeFalse();
    }

    /// <remarks>
    ///     <c>SQLITE_LOCKED</c> is the in-connection twin of <c>SQLITE_BUSY</c>: dropping a table while the
    ///     same connection still has a read open on it. Deterministic, since there is no second connection
    ///     to race.
    /// </remarks>
    [Fact]
    public async Task dropping_a_table_under_an_open_reader_is_transient()
    {
        await using var connection = new SqliteConnection(ShortTimeout);
        await connection.OpenAsync(Token);

        await using var reading = connection.CreateCommand();
        reading.CommandText = "select id from catch";
        await using var reader = await reading.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var locked = await Should.ThrowAsync<SqliteException>(() => executeAsync(connection, "drop table catch"));

        locked.SqliteErrorCode.ShouldBe(6);
        TransientFailures.IsTransient(locked).ShouldBeTrue();
    }

    /// <remarks>
    ///     The fault reaches a supervisor wrapped — in a Fisher exception, a daemon exception, an
    ///     <see cref="AggregateException" /> — so the label has to survive that. It is JasperFx's walk that
    ///     finds it; what this pins is that Fisher's answer for the outer exception does not stop it.
    /// </remarks>
    [Fact]
    public async Task a_busy_database_is_still_transient_when_wrapped()
    {
        var busy = await busyAsync();

        TransientFailures.IsTransient(new InvalidOperationException("projection failed", busy)).ShouldBeTrue();
        TransientFailures.IsTransient(new AggregateException(new ArgumentException("first"), busy)).ShouldBeTrue();

        // A SqliteException Fisher calls permanent around a transient one: the classifier's false is for
        // that exception only, and the walk carries on into its cause.
        var permanentAroundBusy = new SqliteException("outer", 1);
        typeof(Exception)
            .GetField("_innerException", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(permanentAroundBusy, busy);

        TransientFailures.IsTransient(permanentAroundBusy).ShouldBeTrue();
    }

    /// <remarks>
    ///     The whole code table, including the codes deliberately left out — the reason for each is on
    ///     <c>SqliteTransientFailures</c>. Extended codes are <c>primary | (n &lt;&lt; 8)</c>.
    /// </remarks>
    [Theory]
    [InlineData(5, true)] // SQLITE_BUSY
    [InlineData(517, true)] // SQLITE_BUSY_SNAPSHOT
    [InlineData(6, true)] // SQLITE_LOCKED
    [InlineData(262, true)] // SQLITE_LOCKED_SHAREDCACHE
    [InlineData(15, true)] // SQLITE_PROTOCOL
    [InlineData(1, false)] // SQLITE_ERROR: no such table / column, syntax
    [InlineData(10, false)] // SQLITE_IOERR
    [InlineData(266, false)] // SQLITE_IOERR_READ
    [InlineData(11, false)] // SQLITE_CORRUPT
    [InlineData(13, false)] // SQLITE_FULL
    [InlineData(14, false)] // SQLITE_CANTOPEN
    [InlineData(19, false)] // SQLITE_CONSTRAINT
    [InlineData(2067, false)] // SQLITE_CONSTRAINT_UNIQUE
    public void every_sqlite_code_has_a_verdict(int errorCode, bool transient)
    {
        // Registration is the store's job; this fixture built one before any fact ran.
        TransientFailures.IsTransient(new SqliteException("probe", errorCode)).ShouldBe(transient);
    }

    /// <remarks>
    ///     Fisher answers only for <see cref="SqliteException" />; anything else falls through to JasperFx's
    ///     own rules unchanged.
    /// </remarks>
    [Fact]
    public void other_exceptions_are_left_to_the_built_in_rules()
    {
        TransientFailures.IsTransient(new TimeoutException()).ShouldBeTrue();
        TransientFailures.IsTransient(new InvalidOperationException()).ShouldBeFalse();
    }

    private async Task<SqliteException> busyAsync()
    {
        await using var holder = new SqliteConnection(ShortTimeout);
        await holder.OpenAsync(Token);
        await executeAsync(holder, "begin exclusive");

        try
        {
            await using var writer = new SqliteConnection(ShortTimeout);
            await writer.OpenAsync(Token);

            return await Should.ThrowAsync<SqliteException>(
                () => executeAsync(writer, "insert into catch (water) values ('Far Reef')"));
        }
        finally
        {
            await executeAsync(holder, "rollback");
        }
    }

    private static async Task executeAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}

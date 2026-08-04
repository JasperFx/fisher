using JasperFx;
using Microsoft.Data.Sqlite;
using Polly;
using Weasel.Core;
using Weasel.Core.Sequences;
using Weasel.Sqlite;

namespace Fisher.Storage.Sequences;

/// <summary>
///     SQLite Hi-Lo sequence. The dialect-agnostic hi/lo state and the client-side id arithmetic live
///     on <see cref="HiloSequenceBase" />; this supplies only the SQLite I/O against <c>fi_hilo</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Advancing the hi is a single statement, not read-then-compare-and-swap.</b> Marten
///         reaches for a stored function and Polecat for an optimistic UPDATE guarded by the value it
///         just read, retried when the row moved underneath it. SQLite's upsert does the whole thing
///         atomically —
///         <c>insert … on conflict do update set hi_value = fi_hilo.hi_value + 1 returning hi_value</c>
///         — so there is no window between the read and the write for a competing process to win, and
///         the retry loop below exists only to honour the base class's "a negative hi means try again"
///         contract rather than because this dialect can lose a race.
///     </para>
///     <para>
///         The first call inserts the row with <c>hi_value = 0</c> and returns 0, so the first id
///         handed out is 1 — the same starting point as the siblings.
///     </para>
///     <para>
///         The table is created here rather than only through the store's schema application, because
///         an id is assigned when <c>Store</c> is called and that is well before the commit path gets
///         a chance to create anything. <see cref="AutoCreate.None" /> is honoured: a consumer who
///         manages the schema themselves gets SQLite's own "no such table" error rather than surprise
///         DDL, matching how document and event tables behave.
///     </para>
/// </remarks>
internal sealed class HiloSequence : HiloSequenceBase
{
    private readonly AutoCreate _autoCreate;
    private readonly SqliteDataSource _dataSource;
    private readonly string _quotedTable;
    private readonly ResiliencePipeline _resilience;
    private readonly string _schemaName;

    private volatile bool _tableEnsured;

    public HiloSequence(SqliteDataSource dataSource, string schemaName, string entityName,
        IReadOnlyHiloSettings settings, ResiliencePipeline resilience, AutoCreate autoCreate)
        : base(entityName, settings)
    {
        _dataSource = dataSource;
        _schemaName = schemaName;
        _quotedTable = FisherTableNaming.QuotedTableName(schemaName, HiloTable.TableSuffix);
        _resilience = resilience;
        _autoCreate = autoCreate;
    }

    /// <summary>
    ///     Claim the next hi allocation. The <c>DO UPDATE</c> qualifies <c>hi_value</c> with the table
    ///     name, which is how SQLite says "the value already stored" rather than the one being inserted.
    /// </summary>
    private string AdvanceSql =>
        $"insert into {_quotedTable} (entity_name, hi_value) values (@entity, 0) " +
        $"on conflict (entity_name) do update set hi_value = {_quotedTable}.hi_value + 1 " +
        "returning hi_value";

    private string SetFloorSql => $"update {_quotedTable} set hi_value = @floor where entity_name = @entity";

    public override async Task AdvanceToNextHi(CancellationToken ct = default)
    {
        await _resilience.ExecuteAsync(async cancellation =>
        {
            await using var conn = (SqliteConnection)await _dataSource
                .OpenConnectionAsync(cancellation).ConfigureAwait(false);

            await EnsureTableAsync(conn, cancellation).ConfigureAwait(false);

            for (var attempts = 0; attempts < Settings.MaxAdvanceToNextHiAttempts; attempts++)
            {
                await using var command = conn.CreateCommand();
                command.CommandText = AdvanceSql;
                command.Parameters.AddWithValue("@entity", EntityName);

                if (TrySetCurrentHi(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false)))
                {
                    return;
                }
            }

            throw new HiloSequenceAdvanceToNextHiAttemptsExceededException();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The synchronous counterpart, reached from <c>NextLong</c> under the base class's lock.
    /// </summary>
    /// <remarks>
    ///     Synchronous because <c>IIdentification.AssignIfMissing</c> is — an id is assigned inside
    ///     <c>session.Store(document)</c>, which returns void so the caller can read the id straight
    ///     away. It runs through the same resilience pipeline as the async path so that every
    ///     <c>fi_hilo</c> access retries SQLITE_BUSY, not just half of them.
    /// </remarks>
    protected override void AdvanceToNextHiSync()
    {
        _resilience.Execute(() =>
        {
            using var conn = (SqliteConnection)_dataSource.OpenConnection();

            EnsureTableSync(conn);

            for (var attempts = 0; attempts < Settings.MaxAdvanceToNextHiAttempts; attempts++)
            {
                using var command = conn.CreateCommand();
                command.CommandText = AdvanceSql;
                command.Parameters.AddWithValue("@entity", EntityName);

                if (TrySetCurrentHi(command.ExecuteScalar()))
                {
                    return;
                }
            }

            throw new HiloSequenceAdvanceToNextHiAttemptsExceededException();
        });
    }

    /// <summary>
    ///     Reset the sequence so every subsequent id is greater than <paramref name="floor" />.
    /// </summary>
    /// <remarks>
    ///     Advancing first is what guarantees the row exists for the UPDATE to hit; advancing again is
    ///     what makes this instance pick the new floor up rather than keep issuing from the allocation
    ///     it already held.
    /// </remarks>
    public override async Task SetFloor(long floor)
    {
        var numberOfPages = (long)Math.Ceiling((double)floor / MaxLo);

        await AdvanceToNextHi().ConfigureAwait(false);

        await _resilience.ExecuteAsync(async cancellation =>
        {
            await using var conn = (SqliteConnection)await _dataSource
                .OpenConnectionAsync(cancellation).ConfigureAwait(false);

            await using var command = conn.CreateCommand();
            command.CommandText = SetFloorSql;
            command.Parameters.AddWithValue("@floor", numberOfPages);
            command.Parameters.AddWithValue("@entity", EntityName);

            await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AdvanceToNextHi().ConfigureAwait(false);
    }

    private async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (_tableEnsured)
        {
            return;
        }

        if (_autoCreate == AutoCreate.None)
        {
            _tableEnsured = true;
            return;
        }

        var migration = await SchemaMigration.DetermineAsync(conn, ct, new HiloTable(_schemaName))
            .ConfigureAwait(false);

        await new SqliteMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate, ct: ct)
            .ConfigureAwait(false);

        _tableEnsured = true;
    }

    private void EnsureTableSync(SqliteConnection conn)
    {
        if (_tableEnsured)
        {
            return;
        }

        if (_autoCreate == AutoCreate.None)
        {
            _tableEnsured = true;
            return;
        }

#pragma warning disable VSTHRD002
        var migration = SchemaMigration.DetermineAsync(conn, new HiloTable(_schemaName))
            .GetAwaiter().GetResult();

        new SqliteMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate)
            .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        _tableEnsured = true;
    }
}

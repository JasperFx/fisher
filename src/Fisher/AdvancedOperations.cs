using Fisher.Storage;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Protected;
using Weasel.Core;
using Weasel.Core.Sequences;

namespace Fisher;

/// <summary>
///     The store's escape hatch: cleaning, resetting, and the Hi-Lo knobs — the things an application
///     reaches for outside the session API. Mirrors Marten's and Polecat's <c>AdvancedOperations</c>.
/// </summary>
public class AdvancedOperations
{
    private readonly DocumentStore _store;
    private IDocumentCleaner? _cleaner;

    internal AdvancedOperations(DocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    ///     The Hi-Lo settings applied to any document type with a numeric identity and no override of
    ///     its own.
    /// </summary>
    public HiloSettings HiloSequenceDefaults => _store.Options.HiloSequenceDefaults;

    /// <inheritdoc cref="IDocumentCleaner" />
    public IDocumentCleaner Clean => _cleaner ??= new Internal.FisherDocumentCleaner(_store);

    /// <summary>
    ///     Delete every document and every event belonging to this store, keeping the schema.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A running async daemon is paused around the wipe and resumed afterwards</b>
    ///         (fisher#138), and without that the wipe strands it. The delete takes
    ///         <c>fi_event_progression</c> out from under agents that hold their positions in memory,
    ///         so they carry on from where they were, record no progress against an event store that
    ///         now starts at zero, and every later <c>WaitForNonStaleData</c> times out with a message
    ///         about shards that have recorded nothing. Silent until something waits.
    ///     </para>
    ///     <para>
    ///         <b>This is a divergence from Marten, which leaves its daemon alone here.</b> The reason
    ///         to take it is that the alternative was unreachable rather than merely manual: until
    ///         fisher#138 there was no way to get at the running daemon from application code at all,
    ///         so a spec fixture resetting between scenarios — the caller this method overwhelmingly
    ///         has — could not have paused it by hand.
    ///     </para>
    ///     <para>
    ///         Only a daemon <em>this process is hosting</em> is paused, since that is the only one the
    ///         store knows about. <c>DaemonMode.ExternallyManaged</c>, a store built by
    ///         <see cref="DocumentStore.For(System.Action{StoreOptions})" />, or a daemon in another
    ///         process are all unaffected and keep the hazard above — which is the honest outcome, as
    ///         nothing here can reach them.
    ///     </para>
    /// </remarks>
    public async Task ResetAllDataAsync(CancellationToken token = default)
    {
        var daemons = _store.RunningDaemons;

        if (daemons is not null)
        {
            await daemons.PauseAsync().ConfigureAwait(false);
        }

        try
        {
            await Clean.DeleteAllDocumentsAsync(token).ConfigureAwait(false);
            await Clean.DeleteAllEventDataAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // In a finally, because a half-done wipe with the daemon left paused is the worse of the
            // two failures: the caller sees the exception either way, and a paused daemon that never
            // resumes turns one failed reset into every subsequent projection silently not running.
            if (daemons is not null)
            {
                await daemons.ResumeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Delete every row belonging to one tenant, keeping the schema — and, under
    ///     database-per-tenant, the tenant's file (fisher#173).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is not the tenant deletion Fisher refuses, and the difference is the point.</b>
    ///         Deprovisioning a tenant here means deleting a <em>file</em>: the cheapest deprovisioning
    ///         of any Critter Stack store and the most irreversible, and Fisher cannot know whether that
    ///         file is backed up — so <see cref="Storage.ITenantSource" /> suspends or forgets and an
    ///         operator removes the file themselves. Wiping a tenant's <em>rows</em> is a different
    ///         operation: it destroys nothing a file restore would be needed to recover, it is the only
    ///         way to erase a conjoined tenant at all, and nothing covered it.
    ///     </para>
    ///     <para>
    ///         Under conjoined tenancy every table carrying a <c>tenant_id</c> is filtered on it — the
    ///         event store, the tenanted document types, the natural key lookups and the dead letters —
    ///         and the DCB tag rows go through their events, since a tag has no tenant of its own.
    ///         Under database-per-tenant the tenant's whole file is cleared, progression rows excepted:
    ///         they describe how far the daemon read, not what a tenant owns, and resetting them would
    ///         make every shard replay a store that is now empty.
    ///     </para>
    ///     <para>
    ///         <b>One transaction per database, and nothing else is paused.</b> Unlike
    ///         <see cref="ResetAllDataAsync" /> this leaves <c>fi_event_progression</c> alone, so a
    ///         running daemon is not stranded and does not need pausing.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    ///     The store keeps no tenant-scoped data at all, so there is nothing that could be this tenant's
    ///     to delete. Refused rather than reporting a successful erasure of nothing.
    /// </exception>
    /// <exception cref="Storage.UnknownTenantException">
    ///     Database-per-tenant, and this store has no database for that tenant.
    /// </exception>
    public Task DeleteAllTenantDataAsync(string tenantId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return new Internal.TenantDataCleaner(tenantId, _store).ExecuteAsync(token);
    }

    /// <summary>
    ///     Rewrite protected information out of events that are already stored, applying the masking
    ///     rules registered on the store's event options.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole batch commits in one transaction, so an erasure is either done or not done.
    ///         An event is rewritten only when a rule actually matches it.
    ///     </para>
    ///     <para>
    ///         <b>This does not reach anything derived from those events.</b> The daemon's high-water
    ///         mark is a sequence and masking does not move it, so a projection that has already folded
    ///         the unmasked body keeps what it derived — any snapshot, document or flat table holding
    ///         the protected information still holds it until that projection is rebuilt. Marten
    ///         behaves the same way. Masking is a data-at-rest operation.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The batch names no stream and no event filter. Masking every event in the store is not
    ///     something this API can be asked for by accident.
    /// </exception>
    public Task ApplyEventDataMaskingAsync(Action<IEventDataMasking> configure,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var batch = new Events.Protected.EventDataMasking(_store);
        configure(batch);

        return batch.ApplyAsync(token);
    }

    /// <summary>
    ///     Advance a document type's Hi-Lo sequence so that every subsequently assigned id is greater
    ///     than <paramref name="floor" />.
    /// </summary>
    /// <remarks>
    ///     The floor rounds up to a whole allocation, so the next id is the start of the first page
    ///     past it rather than <paramref name="floor" /> + 1. That matches Marten and Polecat, and is
    ///     the price of the client-side batching Hi-Lo exists for.
    /// </remarks>
    public Task ResetHiloSequenceFloorAsync<T>(long floor) where T : notnull
        => _store.Database.SequenceFor(typeof(T)).SetFloor(floor);

    /// <summary>
    ///     Write many documents in as few transactions as possible.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>There is no <c>SqlBulkCopy</c> to reach for, and none is needed.</b> On SQLite the
    ///         cost of an insert is dominated by the transaction rather than by the statement, so a
    ///         prepared statement re-executed with rebound parameters inside one transaction is already
    ///         the fast path — the same order as a bulk-copy protocol, with none of the protocol.
    ///     </para>
    ///     <para>
    ///         <paramref name="batchSize" /> is a ceiling on how long the write lock is held, not a
    ///         throughput knob. SQLite permits one writer per file, so a single transaction over a very
    ///         large set blocks every other writer for its whole duration; committing periodically
    ///         gives them a chance. The trade is that a failure part way leaves earlier batches
    ///         committed — bulk insert is not atomic across batches and does not pretend to be.
    ///     </para>
    ///     <para>
    ///         The statements are the ones <c>SqliteDocumentStorageDescriptorBuilder</c> already
    ///         builds, reached through the session, rather than bulk-specific SQL. That is deliberate:
    ///         a second set of write SQL is exactly where the positional <c>?</c> contract documented
    ///         in CLAUDE.md would drift apart unnoticed.
    ///     </para>
    /// </remarks>
    public async Task BulkInsertAsync<T>(IReadOnlyCollection<T> documents,
        BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000,
        string? tenantId = null, CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (documents.Count == 0)
        {
            return;
        }

        var storage = mode == BulkInsertMode.IgnoreDuplicates
            ? _store.Database.Providers.StorageFor<T>().Lightweight
            : null;

        foreach (var batch in documents.Chunk(batchSize))
        {
            await using var session = _store.LightweightSession(tenantId);

            var alreadyStored = storage is null
                ? null
                : await AlreadyStoredAsync(storage, batch, session.TenantId, token).ConfigureAwait(false);

            foreach (var document in batch)
            {
                if (alreadyStored is not null)
                {
                    if (!alreadyStored.Contains(Compare(storage!.RawIdentityValue(storage.IdentityFor(document)))))
                    {
                        session.Insert(document);
                    }
                }
                else if (mode == BulkInsertMode.InsertsOnly)
                {
                    session.Insert(document);
                }
                else
                {
                    session.Store(document);
                }
            }

            await session.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Which of a batch's ids already occupy a row — the read behind
    ///     <see cref="BulkInsertMode.IgnoreDuplicates" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hand-built rather than routed through <c>LoadManyAsync</c> or <c>Query&lt;T&gt;()</c>,
    ///         which is the opposite of what this codebase usually does and is the point: those two
    ///         answer "which of these can I read", and both apply the soft-delete and hierarchy filters
    ///         to do it. The question here is "which of these would collide", and a soft-deleted row or
    ///         a sub-class row still holds the primary key. It also reads one column instead of
    ///         materializing every document it finds.
    ///     </para>
    ///     <para>
    ///         Ids are compared as their <em>raw</em> SQL values on both sides —
    ///         <c>RawIdentityValue</c> going in and the reader's own value coming back — so a Guid is
    ///         the lowercase canonical text the row holds rather than the uppercase form a bare
    ///         <see cref="Guid" /> binds as. That is the recurring trap, and here it would present as
    ///         every document looking new and the batch failing on the constraint it exists to avoid.
    ///     </para>
    ///     <para>
    ///         A document whose identity is still unassigned needs no special case, which is worth
    ///         saying because it looks like it should. A Guid or numeric identity is assigned before the
    ///         write, so no row can hold <c>Guid.Empty</c> or <c>0</c> for the probe to match; a string
    ///         identity is externally assigned, so an empty one is a real key and finding it is the
    ///         right answer. Only a null identity is dropped, and only because
    ///         <c>RawIdentityValue</c> has nothing to convert.
    ///     </para>
    ///     <para>
    ///         Both sides are compared as invariant strings, which is not decoration:
    ///         Microsoft.Data.Sqlite hands an INTEGER column back as <see cref="long" /> while an
    ///         <see cref="int" /> identity's raw value is an <see cref="int" />, and boxed to
    ///         <see cref="object" /> those two never compare equal — so an int-keyed type would find
    ///         nothing and fail on the constraint this mode exists to avoid. Within one id type the
    ///         rendering is injective, so nothing else can collide.
    ///     </para>
    /// </remarks>
    private async Task<HashSet<string>> AlreadyStoredAsync<T>(Weasel.Storage.IDocumentStorage<T> storage,
        IReadOnlyList<T> batch, string tenantId, CancellationToken token) where T : notnull
    {
        var probe = batch
            .Select(x => storage.IdentityFor(x))
            .Where(x => x is not null)
            .Select(storage.RawIdentityValue)
            .Distinct()
            .ToArray();

        var found = new HashSet<string>();

        if (probe.Length == 0)
        {
            return found;
        }

        var conjoined = storage.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined;

        return await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            var builder = new Weasel.Sqlite.CommandBuilder();
            builder.Append($"select id from {storage.TableName.QualifiedName} ");
            builder.Append("where id in (select value from json_each(");
            builder.AppendParameter(System.Text.Json.JsonSerializer.Serialize(probe));
            builder.Append("))");

            if (conjoined)
            {
                builder.Append(" and tenant_id = ");
                builder.AppendParameter(tenantId);
            }

            await using var command = builder.Compile();
            command.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(Compare(reader.GetValue(0)));
            }

            return found;
        }, token).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AlreadyStoredAsync{T}" />
    private static string Compare(object? rawId)
        => Convert.ToString(rawId, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    ///     How much is in the event store — event count, stream count, and the current sequence.
    /// </summary>
    /// <remarks>
    ///     Three scalars on one connection inside the resilience pipeline, so a <c>SQLITE_BUSY</c>
    ///     retries the set rather than leaving the three readings taken at different moments.
    /// </remarks>
    public async Task<Events.EventStoreStatistics> FetchEventStoreStatisticsAsync(
        CancellationToken token = default)
    {
        var events = _store.Options.EventGraph;

        return await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            return new Events.EventStoreStatistics
            {
                EventCount = await ScalarAsync(connection,
                    $"select count(*) from \"{events.EventsTableName}\"", ct).ConfigureAwait(false),
                StreamCount = await ScalarAsync(connection,
                    $"select count(*) from \"{events.StreamsTableName}\"", ct).ConfigureAwait(false),

                // coalesce, not just a null check: sqlite_sequence has no row for a table nothing has
                // been inserted into, and the table itself does not exist until the first AUTOINCREMENT
                // insert anywhere in the database.
                EventSequenceNumber = await ScalarAsync(connection,
                    "select coalesce((select seq from sqlite_sequence where name = "
                    + $"'{events.EventsTableName}'), 0)", ct).ConfigureAwait(false)
            };
        }, token).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    /// <summary>
    ///     The full creation DDL for this store, without applying any of it.
    /// </summary>
    /// <remarks>
    ///     For reviewing a migration, and for the <c>AutoCreate.None</c> deployment story where
    ///     somebody else runs the DDL. Covers every feature the store has registered — event store,
    ///     document tables, Hi-Lo and flat tables — because it asks the same feature set
    ///     <c>ApplyAllConfiguredChangesToDatabaseAsync</c> applies. A document type that has never been
    ///     registered is absent, exactly as it is from a migration.
    /// </remarks>
    public string ToDatabaseScript() => _store.Database.ToDatabaseScript();

    /// <inheritdoc cref="ToDatabaseScript" />
    public Task WriteCreationScriptToFileAsync(string path, CancellationToken token = default)
        => _store.Database.WriteCreationScriptToFileAsync(path, token);

    // ---- previewing a migration rather than applying one (fisher#210) ----

    /// <summary>
    ///     The difference between this store's configuration and what the database actually holds,
    ///     computed and handed back rather than applied.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The gap fisher#172 left open.</b> That issue made the <em>command line</em> able to
    ///         preview and assert a Fisher store — <c>db-patch</c> writes the outstanding DDL to a file
    ///         and <c>db-assert</c> fails a build against a drifted database — and gave the store
    ///         <see cref="DocumentStore.AssertDatabaseMatchesConfigurationAsync" /> to go with it. What
    ///         it did not give an application was the delta as an <em>object</em>: everything Fisher
    ///         could say about a migration in code was <see cref="ToDatabaseScript" />, which describes
    ///         the schema as configured and knows nothing about the database in front of it.
    ///     </para>
    ///     <para>
    ///         So the two questions a deployment actually asks were unanswerable without shelling out
    ///         to the CLI: <em>is there anything outstanding</em> —
    ///         <see cref="SchemaMigration.Difference" /> is <see cref="SchemaPatchDifference.None" /> —
    ///         and <em>what exactly would change</em>, from <see cref="SchemaMigration.Deltas" />. A
    ///         test asserting "this PR adds no migration" is the same question a third time.
    ///     </para>
    ///     <para>
    ///         <b>This is the default database only</b>, which under database-per-tenant is one file of
    ///         many. Marten's <c>CreateMigrationAsync</c> carries the same restriction and says so;
    ///         here <see cref="CreateMigrationAsync(string,CancellationToken)" /> names a tenant and
    ///         <see cref="CreateAllMigrationsAsync" /> spans every database, because "which tenants are
    ///         behind" is the question this store's migration path already answers per database (see
    ///         <c>TenantMigrationException</c>).
    ///     </para>
    ///     <para>
    ///         Computing a migration reads the schema and writes nothing, so it is honoured under
    ///         <see cref="JasperFx.AutoCreate.None" /> like any other read.
    ///     </para>
    /// </remarks>
    public Task<SchemaMigration> CreateMigrationAsync(CancellationToken token = default)
        => _store.Database.CreateMigrationAsync(token);

    /// <summary>
    ///     The outstanding migration for one tenant's database under database-per-tenant.
    /// </summary>
    /// <remarks>
    ///     Resolved through <see cref="ITenancy.DatabaseFor" />, so an unknown tenant throws rather
    ///     than quietly reporting the default database's delta — the same rule every other
    ///     tenant-scoped member here follows. Under <c>DefaultTenancy</c> every tenant resolves the one
    ///     database, which is the honest answer for a store that is not database-per-tenant.
    /// </remarks>
    public async Task<SchemaMigration> CreateMigrationAsync(string tenantId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        return await _store.Tenancy.DatabaseFor(tenantId).CreateMigrationAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The outstanding migration for every database this store spans, keyed by database identifier.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Per database, because that is what the answer is.</b>
    ///         <see cref="DocumentStore.ApplyAllConfiguredChangesToDatabaseAsync" /> already reports
    ///         success and failure per tenant rather than collapsing them, for the reason
    ///         <c>TenantMigrationException</c> gives: a hundred tenants migrated to the fortieth is a
    ///         mixed store, and which forty is the thing the caller needs. Previewing collapses to one
    ///         migration only for a store with one database, and pretending otherwise would answer
    ///         about whichever file happened to be first.
    ///     </para>
    ///     <para>
    ///         Sequentially, for the same reason the migration itself runs sequentially: each read is
    ///         against its own file, and holding N connections open at once buys nothing on a pool
    ///         ceiling that sizes one file.
    ///     </para>
    ///     <para>
    ///         Under dynamic tenancy the tenants are refreshed first, so a tenant nothing has resolved
    ///         yet is still previewed — the omission <c>db-apply</c> had to close for the same reason.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, SchemaMigration>> CreateAllMigrationsAsync(
        CancellationToken token = default)
    {
        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        var migrations = new Dictionary<string, SchemaMigration>();

        foreach (var database in _store.Tenancy.AllDatabases())
        {
            migrations[database.Identifier] = await database.CreateMigrationAsync(token).ConfigureAwait(false);
        }

        return migrations;
    }

    /// <summary>
    ///     Write the outstanding migration for the default database to a file, the way <c>db-patch</c>
    ///     does, without applying any of it.
    /// </summary>
    /// <remarks>
    ///     The difference from <see cref="WriteCreationScriptToFileAsync" /> is the difference between a
    ///     patch and a dump: this is the delta against the database as it stands, so a store already up
    ///     to date writes a file with nothing in it to run.
    /// </remarks>
    public Task WriteMigrationFileAsync(string path, CancellationToken token = default)
        => _store.Database.WriteMigrationFileAsync(path, token);

    /// <summary>
    ///     Write one creation script per feature — the event store, each document type, Hi-Lo, each flat
    ///     table — into a directory, plus an <c>all.sql</c> that runs them in order.
    /// </summary>
    /// <remarks>
    ///     <b>The directory is cleaned first</b>, by Weasel, so point it at one this store owns.
    ///     For a deployment that reviews schema changes per feature rather than as one script; the
    ///     features are the same set <see cref="ToDatabaseScript" /> writes in one file.
    /// </remarks>
    public Task WriteScriptsByTypeAsync(string directory, CancellationToken token = default)
        => _store.Database.WriteScriptsByTypeAsync(directory, token);

    /// <summary>
    ///     Every schema object this store's configuration describes, in dependency order, without
    ///     touching the database.
    /// </summary>
    /// <remarks>
    ///     Tables and indexes for the event store, every registered document type, Hi-Lo and every flat
    ///     table. The same feature set a migration applies, which is why a type that has never been
    ///     registered is absent from both.
    /// </remarks>
    public IEnumerable<ISchemaObject> AllObjects() => _store.Database.AllObjects();

    /// <summary>
    ///     The schema names the objects above live in. <b>On Fisher this is always exactly
    ///     <c>["main"]</c></b>, and that is a statement about SQLite rather than about this store.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQLite has one schema per connection and no <c>CREATE SCHEMA</c>, so Fisher folds
    ///         <see cref="StoreOptions.DatabaseSchemaName" /> into the <em>table prefix</em> instead
    ///         (see <c>FisherTableNaming</c>) — <c>fi_events</c> against <c>main</c>,
    ///         <c>reporting_fi_events</c> under a logical schema called <c>reporting</c>. The isolation
    ///         between two logical stores in one file is that prefix, and this method cannot see it.
    ///     </para>
    ///     <para>
    ///         <b>Carried anyway, and deliberately not reinterpreted.</b> It is on Marten's
    ///         <c>IMartenStorage</c>, so store-agnostic code calls it; answering with the prefix or with
    ///         the logical schema name would make it mean something different here than it means there,
    ///         which is worse than a constant. <c>the_schema_names_are_always_main</c> pins the constant
    ///         so it reads as a decision rather than as an oversight.
    ///     </para>
    /// </remarks>
    public string[] AllSchemaNames() => _store.Database.AllSchemaNames();

    // ---- the daemon's progression: reading it, and the two ways of unsticking it (fisher#173) ----

    /// <summary>
    ///     Move the high-water mark straight to the highest sequence present, so a daemon starts in
    ///     catch-up mode instead of replaying a store's whole history. <b>Use with caution.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>For retrofitting async projections onto a large event store that has never had
    ///         any.</b> Without this the high-water agent climbs from zero, which on a store with
    ///         millions of events is a long, entirely pointless read.
    ///     </para>
    ///     <para>
    ///         What it skips is the <em>mark's</em> climb, not the shards'. A registered shard still
    ///         starts from its own progression row, and a shard with no row still starts at zero — so
    ///         this is the right call when the projections are new and their history is genuinely not
    ///         wanted, and the wrong one otherwise.
    ///     </para>
    ///     <para>
    ///         Spans every database the store has, because under database-per-tenant a mark advanced on
    ///         one file says nothing about the rest. Name a tenant to advance one.
    ///     </para>
    /// </remarks>
    public async Task AdvanceHighWaterMarkToLatestAsync(CancellationToken token = default)
    {
        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        foreach (var database in _store.Tenancy.AllDatabases())
        {
            await DetectorFor(database).AdvanceHighWaterMarkToLatestAsync(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="AdvanceHighWaterMarkToLatestAsync(CancellationToken)" />
    /// <remarks>
    ///     <b>Under conjoined tenancy every tenant resolves to the one file</b>, which is the honest
    ///     answer rather than a limitation: the high-water mark is a position in a global sequence, and
    ///     a conjoined store has exactly one of those however many tenants write into it. The overload
    ///     is meaningful under database-per-tenant, where a tenant genuinely is a database.
    /// </remarks>
    public Task AdvanceHighWaterMarkToLatestAsync(string tenantId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return DetectorFor(_store.Tenancy.DatabaseFor(tenantId)).AdvanceHighWaterMarkToLatestAsync(token);
    }

    /// <summary>
    ///     Pull any progression row that has advanced past the highest event sequence back down to it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Reachable on Fisher through an ordinary supported operation, where Marten carries the
    ///         same method for a database race it believes it has closed.</b> <c>fi_events.seq_id</c> is
    ///         <c>AUTOINCREMENT</c>, and stream compacting and event masking both delete rows — so
    ///         removing events from the top of the table lowers <c>max(seq_id)</c> below progress that
    ///         was already recorded. A shard stranded above the ceiling never advances again and
    ///         <c>QueryForNonStaleData</c> waits on it forever, with nothing anywhere saying why.
    ///     </para>
    ///     <para>
    ///         <b>Clamped per row, where Marten resets every row wholesale</b> the moment the high-water
    ///         row is ahead. That would drag a shard genuinely behind the head <em>forward</em>, past
    ///         events it had not applied — silently, and exactly on the store somebody is already
    ///         repairing. Only an impossible row is corrected, and only as far as the ceiling.
    ///     </para>
    ///     <para>
    ///         A corrected shard replays from the new ceiling to wherever it thought it was. That is the
    ///         honest outcome: the events it recorded having processed are no longer there to say
    ///         otherwise.
    ///     </para>
    /// </remarks>
    public async Task TryCorrectProgressInDatabaseAsync(CancellationToken token = default)
    {
        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        foreach (var database in _store.Tenancy.AllDatabases())
        {
            await DetectorFor(database).TryCorrectProgressInDatabaseAsync(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="TryCorrectProgressInDatabaseAsync(CancellationToken)" />
    public Task TryCorrectProgressInDatabaseAsync(string tenantId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return DetectorFor(_store.Tenancy.DatabaseFor(tenantId)).TryCorrectProgressInDatabaseAsync(token);
    }

    /// <summary>
    ///     Where every shard has reached, including the high-water mark's own row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The read has existed on <c>FisherDatabase</c> since the daemon landed; what was missing
    ///         was any way to reach it that did not involve casting the store to <c>IEventStore</c> and
    ///         walking <c>AllDatabases()</c>. Marten and Polecat both surface it here.
    ///     </para>
    ///     <para>
    ///         <b>An omitted tenant id spans every database and concatenates</b>, which under
    ///         database-per-tenant means the same shard name appears once per tenant — deliberately, and
    ///         the same shape Marten settled on: collapsing them would have to pick a winner, and
    ///         "projection X is at 40 for one tenant and 900 for another" is the thing an operator came
    ///         to find out. Name a tenant for one database's rows.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<ShardState>> AllProjectionProgress(string? tenantId = null,
        CancellationToken token = default)
    {
        if (tenantId is not null)
        {
            return await _store.Tenancy.DatabaseFor(tenantId).AllProjectionProgress(token).ConfigureAwait(false);
        }

        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        var databases = _store.Tenancy.AllDatabases();

        if (databases.Count == 1)
        {
            return await databases[0].AllProjectionProgress(token).ConfigureAwait(false);
        }

        var states = new List<ShardState>();

        foreach (var database in databases)
        {
            states.AddRange(await database.AllProjectionProgress(token).ConfigureAwait(false));
        }

        return states;
    }

    /// <summary>
    ///     How far one shard has processed.
    /// </summary>
    /// <remarks>
    ///     An omitted tenant id returns the <em>highest</em> position that shard has reached in any of
    ///     the store's databases, mirroring Marten. Zero for a shard with no row, which is what the
    ///     daemon itself reads as "start from the beginning" — a missing row and a row at zero mean the
    ///     same thing and neither is an error.
    /// </remarks>
    public async Task<long> ProjectionProgressFor(ShardName name, string? tenantId = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (tenantId is not null)
        {
            return await _store.Tenancy.DatabaseFor(tenantId).ProjectionProgressFor(name, token)
                .ConfigureAwait(false);
        }

        await _store.RefreshTenantsAsync(token).ConfigureAwait(false);

        var highest = 0L;

        foreach (var database in _store.Tenancy.AllDatabases())
        {
            var sequence = await database.ProjectionProgressFor(name, token).ConfigureAwait(false);

            if (sequence > highest)
            {
                highest = sequence;
            }
        }

        return highest;
    }

    /// <summary>
    ///     Every shard name the store's asynchronous projections and subscriptions run under.
    /// </summary>
    /// <remarks>
    ///     The names <see cref="ProjectionProgressFor" /> and the daemon's rebuild overloads are
    ///     addressed by, so an operator does not have to reconstruct <c>{Name}:{ShardKey}</c> by hand.
    ///     Asynchronous only — an inline projection has no shard, because it has no progress to record.
    /// </remarks>
    public IReadOnlyList<ShardName> AllAsyncProjectionShardNames()
        => _store.Options.Projections.AllShards().Select(x => x.Name).ToList();

    private Events.Daemon.FisherHighWaterDetector DetectorFor(Storage.FisherDatabase database)
        => new(database, _store.Options.EventGraph);

    // ---- rebuilding one stream (fisher#173) ----

    /// <summary>
    ///     Rebuild the projected document of type <typeparamref name="T" /> for a single stream, by
    ///     live-aggregating it and storing the result.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The repair that does not need the daemon: one stream's read model is wrong, and a full
    ///         <c>RebuildProjectionAsync</c> would replay every stream the projection owns to fix it.
    ///     </para>
    ///     <para>
    ///         <b>A stream that folds to nothing deletes the document, where Marten's equivalent throws
    ///         an <see cref="ArgumentNullException" /> from inside <c>Store(null!)</c>.</b> That is not
    ///         an exotic case — an aggregate whose <c>ShouldDelete</c> fired, or an archived stream, both
    ///         land there — and "no document" is exactly what a real rebuild leaves behind for such a
    ///         stream, since teardown clears the rows and the replay never recreates that one. Throwing
    ///         would make the method unusable on the streams most likely to have gone wrong.
    ///     </para>
    ///     <para>
    ///         <b>Refused by name for a type whose storage is not Fisher's</b> — an EF Core-backed
    ///         projection registered through <c>Projections.StorageProviders</c> is deliberately never
    ///         mapped, so <c>Store</c> would create a <c>fi_doc_*</c> table nothing else ever reads.
    ///         Rebuild those through the daemon.
    ///     </para>
    /// </remarks>
    public Task RebuildSingleStreamAsync<T>(Guid id, string? tenantId = null,
        CancellationToken token = default) where T : class
        => RebuildSingleStreamAsync<T>(
            (session, ct) => session.Events.AggregateStreamAsync<T>(id, token: ct),
            session => session.Delete<T>(id), tenantId, token);

    /// <inheritdoc cref="RebuildSingleStreamAsync{T}(Guid, string, CancellationToken)" />
    public Task RebuildSingleStreamAsync<T>(string streamKey, string? tenantId = null,
        CancellationToken token = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);

        return RebuildSingleStreamAsync<T>(
            (session, ct) => session.Events.AggregateStreamAsync<T>(streamKey, token: ct),
            session => session.Delete<T>(streamKey), tenantId, token);
    }

    private async Task RebuildSingleStreamAsync<T>(
        Func<IDocumentSession, CancellationToken, Task<T?>> aggregate, Action<IDocumentSession> deleteById,
        string? tenantId, CancellationToken token) where T : class
    {
        if (!_store.Options.Schema.HasMappingFor(typeof(T)))
        {
            throw new NotSupportedException(
                $"'{typeof(T).Name}' has no Fisher document mapping, so there is no fi_doc_* table for this "
                + "to write into. A projection registered through Projections.StorageProviders (an EF Core "
                + "entity, say) deliberately keeps its rows elsewhere — rebuild it through the daemon's "
                + "RebuildProjectionAsync instead, which routes through the registered storage.");
        }

        await using var session = _store.LightweightSession(tenantId);

        var document = await aggregate(session, token).ConfigureAwait(false);

        if (document is null)
        {
            // Deleted rather than skipped: a real rebuild tears the rows down and replays, so a stream
            // that folds to nothing leaves no document behind. Skipping would keep whatever the
            // previous, wrong run wrote — which is the state this method is being called to repair.
            // Deleted by the stream's own identity, on the same assumption Store makes of the
            // aggregate it just built: a single-stream aggregate's id is its stream's.
            deleteById(session);
        }
        else
        {
            session.Store(document);
        }

        await session.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Run a projection scenario — a given/when/then harness for asserting projected state after a
    ///     set of events.
    /// </summary>
    /// <remarks>
    ///     The harness itself is JasperFx's; Fisher supplies only the store seam, the same way
    ///     <c>FisherProjectionDaemon</c> does for the daemon. <b>It deletes existing data first</b> —
    ///     the event store, and the document types the registered projections own, but nothing else,
    ///     so a scenario can seed unrelated documents beforehand and keep them.
    /// </remarks>
    public Task EventProjectionScenarioAsync(Action<Events.TestSupport.ProjectionScenario> configure,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var scenario = new Events.TestSupport.ProjectionScenario(_store);
        configure(scenario);

        return scenario.ExecuteAsync(token);
    }

    // ---- full-text index maintenance (fisher#215) ----

    /// <summary>
    ///     Rebuild a document type's full-text index from the documents currently stored.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not needed in the ordinary course of things, and that is the point of the design.</b>
    ///         The index is kept in step by database triggers, so it follows every writer on the file
    ///         rather than only the ones going through Fisher — which is the whole reason the trigger
    ///         shape was chosen over maintaining the index on the write path.
    ///     </para>
    ///     <para>
    ///         There are two ways to get out of step anyway, and this is the answer to both. A writer
    ///         that ran with the triggers absent — a bulk load into a copy of the file, a restore, a
    ///         window between creating the table and creating the triggers. And a <em>table rebuild</em>:
    ///         SQLite cannot alter most of a table, so Weasel recreates it and copies the rows, which
    ///         reassigns rowids — and the index is keyed on rowid. Weasel re-emits the triggers it
    ///         dropped, so writes from then on are fine; the rows copied across are not.
    ///     </para>
    ///     <para>
    ///         Cheap enough to reach for on suspicion: it is FTS5's own <c>rebuild</c> over the
    ///         content view, which is one statement.
    ///     </para>
    /// </remarks>
    public Task RebuildFullTextIndexAsync<T>(CancellationToken token = default) where T : notnull
        => ExecuteFullTextCommandAsync<T>("rebuild", token);

    /// <summary>
    ///     Ask FTS5 whether a document type's full-text index agrees with the documents stored.
    /// </summary>
    /// <remarks>
    ///     Runs the index's own <c>integrity-check</c>, which compares it against its content view and
    ///     throws if the two disagree. Worth having beside
    ///     <see cref="RebuildFullTextIndexAsync{T}" /> rather than only the repair: a stale full-text
    ///     index does not error, it returns fewer rows than it should, so without this there is no way
    ///     to ask the question at all.
    /// </remarks>
    public Task CheckFullTextIndexAsync<T>(CancellationToken token = default) where T : notnull
        => ExecuteFullTextCommandAsync<T>("integrity-check", token);

    private async Task ExecuteFullTextCommandAsync<T>(string command, CancellationToken token)
        where T : notnull
    {
        var mapping = _store.Options.Schema.MappingFor(typeof(T));

        if (mapping.FullTextIndex is null)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' declares no full-text index. Declare one with "
                + $"StoreOptions.Schema.For<{typeof(T).Name}>().FullTextIndex(...) or the "
                + "[FullTextIndex] attribute.");
        }

        var table = Weasel.Sqlite.SchemaUtils.QuoteName(
            Storage.FullText.FullTextSchema.TableNameFor(mapping).Name);

        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct)
                .ConfigureAwait(false);

            var builder = new Weasel.Sqlite.CommandBuilder();
            builder.Append($"insert into {table}({table}) values (");
            builder.AppendParameter(command);
            builder.Append(')');

            await using var sql = builder.Compile();
            sql.Connection = connection;

            await sql.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }
}

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Protected;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record HolderRegistered(string Holder);

/// <summary>
///     polecat#546 item 2 / marten#5234 — the destructive event operations must not reach past the
///     tenant the read was scoped to.
/// </summary>
/// <remarks>
///     <para>
///         <b>Polecat's exact bug is structurally unreachable in Fisher, and the first test is what
///         says so.</b> In Polecat, <c>Events.UseTenantPartitionedEvents</c> gives each tenant its own
///         <c>pc_events_sequence_{ordinal}</c>, so <c>seq_id = 1</c> exists once per tenant; a
///         <c>WHERE seq_id = @p</c> with no tenant predicate therefore matched rows in tenants the
///         caller never named. Fisher has no partitioned-events mode: <c>fi_events.seq_id</c> is a
///         single <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> shared by every tenant in the file, so a
///         sequence identifies exactly one row and the escape has nowhere to go. Under
///         database-per-tenant the tenants are not even in the same file.
///     </para>
///     <para>
///         That makes <c>seq_id</c> uniqueness a load-bearing precondition rather than an
///         implementation detail, so <see cref="the_two_tenants_events_draw_from_one_shared_sequence" />
///         asserts it directly. If Fisher ever adopts per-tenant sequencing, that test goes red before
///         the isolation tests below start silently proving nothing.
///     </para>
///     <para>
///         The isolation tests still earn their place: the guarantee they pin is that the <em>read</em>
///         half is tenant-scoped. A <c>FetchStreamAsync</c> or <c>QueryEventsAsync</c> that leaked
///         across tenants would hand the calling tenant another tenant's sequences, and every write
///         downstream would then be correctly aimed at the wrong rows. Both tenants use the <b>same
///         stream id</b>, which is legal under conjoined tenancy — identity there is
///         <c>(tenant_id, id)</c> — and is the shape that discriminates hardest.
///     </para>
///     <para>
///         Every assertion checks both directions, per the discipline in
///         <c>Documents.cross_tenant_writes</c>: a store that leaks still answers correctly for the
///         tenant that owns the data.
///     </para>
/// </remarks>
public class cross_tenant_protected_operations : IAsyncLifetime
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cross-tenant-protected");
    private DocumentStore _store = null!;

    /// <summary>One stream id, two tenants — the strongest shape available under conjoined tenancy.</summary>
    private readonly Guid _sharedStreamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
            options.Events.AddEventType(typeof(HolderRegistered));

            options.Events.AddMaskingRuleForProtectedInformation<HolderRegistered>(
                x => x with { Holder = "REDACTED" });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The precondition the whole verdict rests on: one AUTOINCREMENT sequence for the file, so no
    ///     two tenants' events ever share a <c>seq_id</c> and a bare <c>where seq_id = ?</c> cannot
    ///     cross a tenant boundary.
    /// </summary>
    [Fact]
    public async Task the_two_tenants_events_draw_from_one_shared_sequence()
    {
        await StartAsync(Alpha, new CoinsEarned(100), new CoinsSpent(30));
        await StartAsync(Beta, new CoinsEarned(7), new CoinsSpent(2));

        var alpha = await SequencesAsync(Alpha);
        var beta = await SequencesAsync(Beta);

        alpha.Count.ShouldBe(2);
        beta.Count.ShouldBe(2);

        // Disjoint, not merely different — a per-tenant sequence would give both tenants 1 and 2.
        alpha.Intersect(beta).ShouldBeEmpty();
    }

    // ---- masking ----

    [Fact]
    public async Task masking_one_tenant_must_not_rewrite_another_tenants_events()
    {
        await StartAsync(Alpha, new HolderRegistered("Frodo"));
        await StartAsync(Beta, new HolderRegistered("Samwise"));

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.ForTenant(Alpha).IncludeStream(_sharedStreamId), Token);

        // The tenant that asked was served...
        (await HolderAsync(Alpha)).ShouldBe("REDACTED");

        // ...and the one that did not ask still has its own data, unmasked and not replaced by alpha's.
        (await HolderAsync(Beta)).ShouldBe("Samwise");
    }

    /// <summary>
    ///     The cross-stream selector, which is the one translated to SQL and therefore the one that
    ///     could span tenants if its predicate lost the tenant column.
    /// </summary>
    [Fact]
    public async Task an_include_events_batch_masks_only_the_calling_tenant()
    {
        await StartAsync(Alpha, new HolderRegistered("Frodo"));
        await StartAsync(Beta, new HolderRegistered("Samwise"));

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.ForTenant(Beta).IncludeEvents(e => e.EventTypeName == "holder_registered"), Token);

        (await HolderAsync(Beta)).ShouldBe("REDACTED");
        (await HolderAsync(Alpha)).ShouldBe("Frodo");
    }

    // ---- compaction ----

    [Fact]
    public async Task compacting_one_tenants_stream_must_not_delete_another_tenants_events()
    {
        await StartAsync(Alpha, new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));
        await StartAsync(Beta, new CoinsEarned(7), new CoinsSpent(2), new CoinsEarned(1));

        await using (var session = _store.LightweightSession(Alpha))
        {
            await session.Events.CompactStreamAsync<Purse>(_sharedStreamId);
        }

        // Alpha collapsed to its snapshot...
        var alpha = await EventsAsync(Alpha);
        alpha.ShouldHaveSingleItem().Data.ShouldBeOfType<Compacted<Purse>>().Snapshot.Balance.ShouldBe(75);

        // ...and beta still has all three of its own events, none of them alpha's snapshot.
        var beta = await EventsAsync(Beta);
        beta.Count.ShouldBe(3);
        beta.ShouldAllBe(x => x.Data is CoinsEarned || x.Data is CoinsSpent);
    }

    /// <summary>
    ///     The compaction watermark is written to <c>fi_streams</c>, where identity really is
    ///     <c>(tenant_id, id)</c> and the two tenants' rows collide on <c>id</c> alone.
    /// </summary>
    [Fact]
    public async Task the_compaction_watermark_lands_on_the_calling_tenants_stream_only()
    {
        await StartAsync(Alpha, new CoinsEarned(100), new CoinsSpent(30));
        await StartAsync(Beta, new CoinsEarned(7), new CoinsSpent(2));

        await using (var session = _store.LightweightSession(Alpha))
        {
            await session.Events.CompactStreamAsync<Purse>(_sharedStreamId);
        }

        (await CompactedVersionAsync(Alpha)).ShouldBe(2);
        (await CompactedVersionAsync(Beta)).ShouldBe(0);
    }

    // ---- helpers ----

    private async Task StartAsync(string tenantId, params object[] events)
    {
        await using var session = _store.LightweightSession(tenantId);
        session.Events.StartStream<Purse>(_sharedStreamId, events);
        await session.SaveChangesAsync(Token);
    }

    private async Task<IReadOnlyList<IEvent>> EventsAsync(string tenantId)
    {
        await using var session = _store.LightweightSession(tenantId);
        return await session.Events.FetchStreamAsync(_sharedStreamId, token: Token);
    }

    private async Task<string> HolderAsync(string tenantId)
        => ((HolderRegistered)(await EventsAsync(tenantId))[0].Data).Holder;

    private async Task<List<long>> SequencesAsync(string tenantId)
        => (await EventsAsync(tenantId)).Select(x => x.Sequence).ToList();

    private async Task<long> CompactedVersionAsync(string tenantId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select compacted_version from fi_streams where id = $id and tenant_id = $tenant";
        command.Parameters.AddWithValue("$id", _sharedStreamId.ToString());
        command.Parameters.AddWithValue("$tenant", tenantId);

        var raw = await command.ExecuteScalarAsync(Token);
        return raw is null or DBNull ? 0 : Convert.ToInt64(raw);
    }
}

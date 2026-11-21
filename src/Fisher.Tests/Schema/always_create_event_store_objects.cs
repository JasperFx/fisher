using JasperFx;

namespace Fisher.Tests.Schema;

/// <summary>
///     fisher#664, the port of marten#5765 and marten#5763: the cheap event-store columns are created
///     whatever the flags say, and only the behaviour follows the flags.
/// </summary>
/// <remarks>
///     When the flags decided the schema, two stores over one file with different settings (fisher#353)
///     dropped each other's columns: the flags-off store's migration saw the flags-on store's columns as
///     extras, and the flags-on store then failed its next append or progression read.
/// </remarks>
public class always_create_event_store_objects
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] EventMetadataColumns = ["correlation_id", "causation_id", "headers", "user_name"];

    private static readonly string[] ExtendedProgressionColumns =
    [
        "heartbeat", "agent_status", "pause_reason", "running_on_node", "warning_behind_threshold",
        "critical_behind_threshold", "failure_category", "failure_event_sequence", "failure_event_type",
        "failure_event_tenant_id", "parked_count"
    ];

    private static void everyFlagOff(StoreOptions options)
    {
        options.Events.EnableCorrelationId = false;
        options.Events.EnableCausationId = false;
        options.Events.EnableHeaders = false;
        options.Events.EnableUserName = false;
        options.Events.EnableExtendedProgressionTracking = false;
    }

    private static DocumentStore storeFor(TemporaryDatabase database, Action<StoreOptions>? configure = null)
        => DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
            configure?.Invoke(o);
        });

    private static async Task<IReadOnlyList<string>> columnsOf(DocumentStore store, string table)
    {
        await using var session = store.QuerySession();
        return await session.AdvancedSql.QueryAsync<string>($"select name from pragma_table_info('{table}')", Token);
    }

    [Fact]
    public async Task a_store_with_every_flag_off_still_creates_the_columns()
    {
        using var database = TemporaryDatabase.Create("always-create-off");
        await using var store = storeFor(database, everyFlagOff);

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        EventMetadataColumns.ShouldBeSubsetOf(await columnsOf(store, "fi_events"));
        ExtendedProgressionColumns.ShouldBeSubsetOf(await columnsOf(store, "fi_event_progression"));

        // And the store agrees with the file it just shaped.
        await store.AssertDatabaseMatchesConfigurationAsync(Token);
    }

    [Fact]
    public async Task a_flags_off_store_applied_over_a_flags_on_file_drops_nothing()
    {
        using var database = TemporaryDatabase.Create("always-create-shared");
        var streamId = Guid.NewGuid();

        await using (var on = storeFor(database, o => o.Events.EnableAllEventMetadata()))
        {
            await on.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        await using (var off = storeFor(database, everyFlagOff))
        {
            await off.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            EventMetadataColumns.ShouldBeSubsetOf(await columnsOf(off, "fi_events"));
            ExtendedProgressionColumns.ShouldBeSubsetOf(await columnsOf(off, "fi_event_progression"));
        }

        // The flags-on store still matches the file the flags-off store migrated last, and still writes
        // and reads its metadata there.
        await using var again = storeFor(database, o => o.Events.EnableAllEventMetadata());
        await again.AssertDatabaseMatchesConfigurationAsync(Token);

        await using (var session = again.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.Events.StartStream(streamId, new Opened("a"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = again.QuerySession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);
        events.Single().CorrelationId.ShouldBe("corr-1");
    }

    [Fact]
    public async Task the_flags_still_decide_what_is_written()
    {
        using var database = TemporaryDatabase.Create("always-create-behaviour");
        await using var store = storeFor(database, everyFlagOff);

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.CausationId = "cause-1";
            session.CurrentUserName = "jane";
            session.Events.StartStream(streamId, new Opened("a"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var written = await query.AdvancedSql.QueryAsync<long>(
            "select count(*) from fi_events where correlation_id is not null or causation_id is not null "
            + "or user_name is not null or headers is not null", Token);

        written.Single().ShouldBe(0);
    }

    public record Opened(string Name);
}

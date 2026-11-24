using System.Diagnostics.Metrics;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Fisher.Projections;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#621 — the defaults Fisher 2.0 changed, following Marten 10 (marten#5812), and
///     <see cref="StoreOptions.RestoreV1Defaults" />, which puts every one of them back. fisher#682 (marten#6144)
///     put event metadata and the document metadata columns back to opt-in before release.
/// </summary>
/// <remarks>
///     Every default changed in 2.0 has a pair here: one fact for the new default and one for what
///     <c>RestoreV1Defaults()</c> restores. A new 2.0 default change goes in this file, in
///     <c>RestoreV1Defaults()</c> and in the migration guide together.
/// </remarks>
public class restore_v1_defaults
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StoreOptions Restored()
    {
        var options = new StoreOptions();
        options.RestoreV1Defaults();
        return options;
    }

    // ---- projection errors (fisher#652) ----

    [Fact]
    public void a_failing_projection_identity_parks_by_default()
    {
        var errors = new StoreOptions().Projections.Errors;

        errors.ApplyErrors.ShouldBe(JasperFx.Events.Daemon.ProjectionErrorAction.Park);
        errors.SerializationErrors.ShouldBe(JasperFx.Events.Daemon.ProjectionErrorAction.Park);
    }

    [Fact]
    public void restore_skips_failing_events_again()
    {
        var errors = Restored().Projections.Errors;

        errors.ApplyErrors.ShouldBe(JasperFx.Events.Daemon.ProjectionErrorAction.Skip);
        errors.SerializationErrors.ShouldBe(JasperFx.Events.Daemon.ProjectionErrorAction.Skip);
        errors.SkipApplyErrors.ShouldBeTrue();
        errors.SkipSerializationErrors.ShouldBeTrue();
    }

    // ---- event metadata (fisher#682: kept opt-in, as in Fisher 1, following marten#6144) ----

    [Fact]
    public void every_event_metadata_flag_is_off_by_default()
    {
        var events = new StoreOptions().Events;

        events.EnableCorrelationId.ShouldBeFalse();
        events.EnableCausationId.ShouldBeFalse();
        events.EnableHeaders.ShouldBeFalse();
        events.EnableUserName.ShouldBeFalse();
    }

    /// <summary>Nothing to restore, so a flag turned on before <c>RestoreV1Defaults()</c> stays on.</summary>
    [Fact]
    public void restore_leaves_event_metadata_turned_on_before_it_alone()
    {
        var options = new StoreOptions();
        options.Events.EnableAllEventMetadata();
        options.RestoreV1Defaults();

        options.Events.EnableCorrelationId.ShouldBeTrue();
        options.Events.EnableCausationId.ShouldBeTrue();
        options.Events.EnableHeaders.ShouldBeTrue();
        options.Events.EnableUserName.ShouldBeTrue();
    }

    /// <summary>
    ///     fisher#664: the columns are part of the schema whatever the flags say, so the flags only decide
    ///     whether they are written.
    /// </summary>
    [Fact]
    public async Task the_events_table_has_the_metadata_columns_whatever_the_flags()
    {
        var metadata = new[] { "correlation_id", "causation_id", "headers", "user_name" };

        (await columnsOf(o => { }, "fi_events")).Intersect(metadata).Count().ShouldBe(metadata.Length);
        (await columnsOf(o => o.Events.EnableAllEventMetadata(), "fi_events")).Intersect(metadata).Count()
            .ShouldBe(metadata.Length);
    }

    [Fact]
    public async Task event_metadata_is_not_written_by_default()
    {
        using var database = TemporaryDatabase.Create("v2-defaults-eventmeta");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.CausationId = "cause-1";
            session.CurrentUserName = "jane";
            session.Events.StartStream(streamId, new LedgerOpened("a"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var written = await query.AdvancedSql.QueryAsync<string>(
            "select coalesce(correlation_id, '') || coalesce(causation_id, '') || coalesce(user_name, '') from fi_events",
            Token);

        written.ShouldBe([""]);
    }

    // ---- document metadata (fisher#682: kept opt-in) ----

    [Fact]
    public void the_four_session_metadata_columns_are_off_by_default()
    {
        var metadata = new StoreOptions().Schema.For<Ledger>().Mapping.Metadata;

        metadata.CorrelationId.Enabled.ShouldBeFalse();
        metadata.CausationId.Enabled.ShouldBeFalse();
        metadata.LastModifiedBy.Enabled.ShouldBeFalse();
        metadata.Headers.Enabled.ShouldBeFalse();
        metadata.CreatedAt.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void a_type_that_asks_for_a_column_keeps_it_after_restore()
    {
        var options = new StoreOptions();
        options.RestoreV1Defaults();

        var metadata = options.Schema.For<Ledger>().Metadata(m => m.CorrelationId.Enabled = true).Mapping.Metadata;

        metadata.CorrelationId.Enabled.ShouldBeTrue();
        metadata.CausationId.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void a_column_this_configuration_turned_on_still_cannot_be_turned_off()
    {
        Should.Throw<InvalidOperationException>(() => new StoreOptions().Schema.For<Ledger>().Metadata(m =>
        {
            m.Headers.Enabled = true;
            m.Headers.Enabled = false;
        }));
    }

    [Fact]
    public async Task a_document_table_has_no_session_metadata_columns_by_default()
    {
        var metadata = new[] { "correlation_id", "causation_id", "last_modified_by", "headers", "created_at" };

        (await columnsOf(o => o.Schema.For<Ledger>(), "fi_doc_ledger")).Intersect(metadata).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_document_carries_the_session_metadata_once_its_type_asks()
    {
        using var database = TemporaryDatabase.Create("v2-defaults-docmeta");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Schema.For<Ledger>().Metadata(m =>
            {
                m.CorrelationId.Enabled = true;
                m.CausationId.Enabled = true;
                m.LastModifiedBy.Enabled = true;
            });
        });

        var ledger = new Ledger { Id = Guid.NewGuid() };
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.CausationId = "cause-1";
            session.CurrentUserName = "jane";
            session.Store(ledger);
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var metadata = (await query.MetadataForAsync(ledger, Token)).ShouldNotBeNull();

        metadata.CorrelationId.ShouldBe("corr-1");
        metadata.CausationId.ShouldBe("cause-1");
        metadata.LastModifiedBy.ShouldBe("jane");
    }

    // ---- extended progression tracking (fisher#664, marten#5763) ----

    [Fact]
    public void extended_progression_tracking_is_on_by_default()
    {
        var options = new StoreOptions();

        options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();
        ((JasperFx.Events.IEventStoreInstrumentation)options.Events).ExtendedProgressionEnabled.ShouldBeTrue();
    }

    [Fact]
    public void restore_turns_extended_progression_tracking_off()
    {
        var events = Restored().Events;

        events.EnableExtendedProgressionTracking.ShouldBeFalse();
        ((JasperFx.Events.IEventStoreInstrumentation)events).ExtendedProgressionEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task the_progression_table_has_the_extended_columns_by_default_and_after_restore()
    {
        string[] extended = ["heartbeat", "agent_status", "pause_reason", "running_on_node", "failure_category", "parked_count"];

        (await columnsOf(o => { }, "fi_event_progression")).Intersect(extended).Count().ShouldBe(extended.Length);

        var restored = await columnsOf(o => o.RestoreV1Defaults(), "fi_event_progression");
        restored.Intersect(extended).Count().ShouldBe(extended.Length);
    }

    // ---- side effects on inline projections ----

    [Fact]
    public void side_effects_run_on_inline_projections_by_default()
    {
        new StoreOptions().Events.EnableSideEffectsOnInlineProjections.ShouldBeTrue();
    }

    [Fact]
    public void restore_turns_side_effects_on_inline_projections_off()
    {
        Restored().Events.EnableSideEffectsOnInlineProjections.ShouldBeFalse();
    }

    /// <summary>
    ///     fisher#682 (marten#6144): kept on, with a startup warning naming each Inline projection that overrides
    ///     <c>RaiseSideEffects()</c>, because in Fisher 1 that override never ran inline.
    /// </summary>
    [Fact]
    public void the_startup_warning_names_an_inline_projection_that_raises_side_effects()
    {
        var options = new StoreOptions();
        options.Projections.Add(new Fisher.Tests.Projections.AnnouncingProjection(), ProjectionLifecycle.Inline);

        DocumentStore.InlineProjectionsThatRaiseSideEffects(options)
            .ShouldBe([new Fisher.Tests.Projections.AnnouncingProjection().Name]);
    }

    [Fact]
    public void no_startup_warning_for_an_async_projection_or_after_restore()
    {
        var asynchronous = new StoreOptions();
        asynchronous.Projections.Add(new Fisher.Tests.Projections.AnnouncingProjection(), ProjectionLifecycle.Async);
        DocumentStore.InlineProjectionsThatRaiseSideEffects(asynchronous).ShouldBeEmpty();

        var restored = new StoreOptions();
        restored.Projections.Add(new Fisher.Tests.Projections.AnnouncingProjection(), ProjectionLifecycle.Inline);
        restored.RestoreV1Defaults();
        DocumentStore.InlineProjectionsThatRaiseSideEffects(restored).ShouldBeEmpty();

        var plain = new StoreOptions();
        plain.Projections.Snapshot<LedgerTotal>(SnapshotLifecycle.Inline);
        DocumentStore.InlineProjectionsThatRaiseSideEffects(plain).ShouldBeEmpty();
    }

    // ---- the event counter ----

    [Fact]
    public async Task the_event_counter_is_on_by_default_and_off_after_restore()
    {
        using var database = TemporaryDatabase.Create("v1-defaults-counter");

        await using (var store = DocumentStore.For(o => o.ConnectionString = database.ConnectionString))
        {
            store.Options.OpenTelemetry.TracksEventCounters.ShouldBeTrue();
        }

        await using (var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.RestoreV1Defaults();
        }))
        {
            store.Options.OpenTelemetry.TracksEventCounters.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task the_default_event_counter_counts_appended_events()
    {
        const string storeName = "v1_defaults_event_counter";
        long counted = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == Fisher.Services.OpenTelemetryOptions.MeterName
                    && instrument.Name == "fisher.events.appended")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                // A MeterListener is process-wide; count only this test's store.
                if (tag.Key == "fisher.store" && tag.Value as string == storeName)
                {
                    Interlocked.Add(ref counted, value);
                }
            }
        });

        listener.Start();

        using var database = TemporaryDatabase.Create("v1-defaults-counted");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.StoreName = storeName;
        });

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new LedgerOpened("a"), new LedgerOpened("b"));
        await session.SaveChangesAsync(Token);

        Interlocked.Read(ref counted).ShouldBe(2);
    }

    // ---- System.Text.Json ----

    [Fact]
    public void the_default_serializer_allows_out_of_order_metadata_properties()
    {
        var serializer = new StoreOptions().Serializer.ShouldBeOfType<Fisher.Serialization.Serializer>();

        serializer.Options.AllowOutOfOrderMetadataProperties.ShouldBeTrue();
    }

    [Fact]
    public async Task restore_turns_out_of_order_metadata_properties_off_even_for_a_serializer_configured_after_it()
    {
        using var database = TemporaryDatabase.Create("v1-defaults-stj");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.RestoreV1Defaults();
            o.ConfigureSerialization(casing: Weasel.Core.Casing.Default);
        });

        var serializer = store.Options.Serializer.ShouldBeOfType<Fisher.Serialization.Serializer>();
        serializer.Options.AllowOutOfOrderMetadataProperties.ShouldBeFalse();
    }

    // ---- GateSideEffectsBehindPriorVersion (JasperFx 3.0, jasperfx#1075) ----

    [Fact]
    public async Task projections_gate_side_effects_behind_a_prior_version_by_default_and_not_after_restore()
    {
        using var database = TemporaryDatabase.Create("v1-defaults-gate");

        await using (var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.Projections.Snapshot<LedgerTotal>(SnapshotLifecycle.Inline);
        }))
        {
            gateOf(store).ShouldBeTrue();
        }

        await using (var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.RestoreV1Defaults();
            o.Projections.Snapshot<LedgerTotal>(SnapshotLifecycle.Inline);
        }))
        {
            gateOf(store).ShouldBeFalse();
        }
    }

    private static bool gateOf(DocumentStore store)
        => store.Options.Projections.All.OfType<ProjectionBase>().Single().GateSideEffectsBehindPriorVersion;

    private static async Task<IReadOnlyList<string>> columnsOf(Action<StoreOptions> configure, string table)
    {
        using var database = TemporaryDatabase.Create("v1-defaults-columns");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            configure(o);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        return await session.AdvancedSql.QueryAsync<string>($"select name from pragma_table_info('{table}')", Token);
    }

    public class Ledger
    {
        public Guid Id { get; set; }
    }

    public record LedgerOpened(string Name);

    public partial class LedgerTotal
    {
        public Guid Id { get; set; }
        public int Count { get; set; }

        public void Apply(LedgerOpened _) => Count++;
    }
}

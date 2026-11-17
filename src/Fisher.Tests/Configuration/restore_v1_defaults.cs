using System.Diagnostics.Metrics;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Fisher.Projections;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#621 — the defaults Fisher 2.0 changed, following Marten 10 (marten#5812), and
///     <see cref="StoreOptions.RestoreV1Defaults" />, which puts every one of them back.
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

    // ---- event metadata ----

    [Fact]
    public void every_event_metadata_column_is_on_by_default()
    {
        var events = new StoreOptions().Events;

        events.EnableCorrelationId.ShouldBeTrue();
        events.EnableCausationId.ShouldBeTrue();
        events.EnableHeaders.ShouldBeTrue();
        events.EnableUserName.ShouldBeTrue();
    }

    [Fact]
    public void restore_turns_the_event_metadata_columns_off()
    {
        var events = Restored().Events;

        events.EnableCorrelationId.ShouldBeFalse();
        events.EnableCausationId.ShouldBeFalse();
        events.EnableHeaders.ShouldBeFalse();
        events.EnableUserName.ShouldBeFalse();
    }

    [Fact]
    public async Task the_events_table_has_the_metadata_columns_by_default_and_not_after_restore()
    {
        var metadata = new[] { "correlation_id", "causation_id", "headers", "user_name" };

        (await columnsOf(o => { }, "fi_events")).Intersect(metadata).Count().ShouldBe(metadata.Length);

        var restored = await columnsOf(o => o.RestoreV1Defaults(), "fi_events");
        restored.Intersect(metadata).ShouldBeEmpty();
    }

    // ---- document metadata ----

    [Fact]
    public void the_four_session_metadata_columns_are_on_for_every_document_by_default()
    {
        var metadata = new StoreOptions().Schema.For<Ledger>().Mapping.Metadata;

        metadata.CorrelationId.Enabled.ShouldBeTrue();
        metadata.CausationId.Enabled.ShouldBeTrue();
        metadata.LastModifiedBy.Enabled.ShouldBeTrue();
        metadata.Headers.Enabled.ShouldBeTrue();

        // Not part of the change: created_at stays opt-in, as it is in Marten.
        metadata.CreatedAt.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void restore_turns_the_document_metadata_columns_off_even_for_a_type_mapped_before_it()
    {
        var options = new StoreOptions();
        var metadata = options.Schema.For<Ledger>().Mapping.Metadata;

        options.RestoreV1Defaults();

        metadata.CorrelationId.Enabled.ShouldBeFalse();
        metadata.CausationId.Enabled.ShouldBeFalse();
        metadata.LastModifiedBy.Enabled.ShouldBeFalse();
        metadata.Headers.Enabled.ShouldBeFalse();
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
    public void one_type_can_keep_a_default_column_off()
    {
        var metadata = new StoreOptions().Schema.For<Ledger>().Metadata(m => m.Headers.Enabled = false).Mapping.Metadata;

        metadata.Headers.Enabled.ShouldBeFalse();
        metadata.CorrelationId.Enabled.ShouldBeTrue();
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
    public async Task a_document_table_has_the_metadata_columns_by_default_and_not_after_restore()
    {
        var metadata = new[] { "correlation_id", "causation_id", "last_modified_by", "headers" };

        var columns = await columnsOf(o => o.Schema.For<Ledger>(), "fi_doc_ledger");
        columns.Intersect(metadata).Count().ShouldBe(metadata.Length);
        columns.ShouldNotContain("created_at");

        var restored = await columnsOf(o =>
        {
            o.Schema.For<Ledger>();
            o.RestoreV1Defaults();
        }, "fi_doc_ledger");
        restored.Intersect(metadata).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_document_carries_the_session_metadata_by_default()
    {
        using var database = TemporaryDatabase.Create("v1-defaults-docmeta");
        await using var store = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
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

    [Fact]
    public async Task a_fisher_1_file_gains_the_metadata_columns_when_a_2_0_store_migrates_it()
    {
        using var database = TemporaryDatabase.Create("v1-defaults-upgrade");
        var ledger = new Ledger { Id = Guid.NewGuid() };
        var streamId = Guid.NewGuid();

        await using (var v1 = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.RestoreV1Defaults();
            o.Schema.For<Ledger>();
        }))
        {
            await v1.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var session = v1.LightweightSession();
            session.Store(ledger);
            session.Events.StartStream(streamId, new LedgerOpened("before"));
            await session.SaveChangesAsync(Token);
        }

        await using var v2 = DocumentStore.For(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
            o.Schema.For<Ledger>();
        });

        await v2.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = v2.LightweightSession())
        {
            session.CorrelationId = "after";
            session.Events.Append(streamId, new LedgerOpened("after"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = v2.QuerySession();
        (await query.LoadAsync<Ledger>(ledger.Id, Token)).ShouldNotBeNull();

        var events = await query.Events.FetchStreamAsync(streamId, token: Token);
        events.Count.ShouldBe(2);
        events[0].CorrelationId.ShouldBeNull();
        events[1].CorrelationId.ShouldBe("after");
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

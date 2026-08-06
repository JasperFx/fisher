using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>Marks the event types a masking rule can be registered against wholesale.</summary>
public interface IPersonalData;

public record CustomerRegistered(string Name, string Email) : IPersonalData;

public class ContactRecorded : IPersonalData
{
    public string Phone { get; set; } = string.Empty;
}

public record OrderPlaced(string Sku);

/// <summary>
///     fisher#9 — rewriting protected information out of events that are already stored.
/// </summary>
public class masking_event_data : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("masking");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.AddEventType(typeof(CustomerRegistered));
            options.Events.AddEventType(typeof(ContactRecorded));
            options.Events.AddEventType(typeof(OrderPlaced));

            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = "REDACTED", Email = "REDACTED" });

            options.Events.AddMaskingRuleForProtectedInformation<ContactRecorded>(
                x => x.Phone = "REDACTED");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- the rules ----

    /// <summary>
    ///     A record needs the <c>Func</c> overload: a <c>with</c> expression produces a new instance,
    ///     and mutating the old one would be discarded.
    /// </summary>
    [Fact]
    public async Task a_func_rule_replaces_a_record_body()
    {
        var streamId = await StartStreamAsync(new CustomerRegistered("Frodo", "frodo@shire.test"));

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId), TestContext.Current.CancellationToken);

        var masked = (CustomerRegistered)(await EventsOfAsync(streamId))[0].Data;
        masked.Name.ShouldBe("REDACTED");
        masked.Email.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     A class with settable members takes the <c>Action</c> overload and is mutated in place.
    /// </summary>
    [Fact]
    public async Task an_action_rule_mutates_a_class_body()
    {
        var streamId = await StartStreamAsync(new ContactRecorded { Phone = "555-0100" });

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId), TestContext.Current.CancellationToken);

        ((ContactRecorded)(await EventsOfAsync(streamId))[0].Data).Phone.ShouldBe("REDACTED");
    }

    [Fact]
    public async Task an_event_no_rule_matches_is_left_alone()
    {
        var streamId = await StartStreamAsync(new OrderPlaced("MITHRIL-1"));

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId), TestContext.Current.CancellationToken);

        ((OrderPlaced)(await EventsOfAsync(streamId))[0].Data).Sku.ShouldBe("MITHRIL-1");
    }

    /// <summary>
    ///     <c>IEvent&lt;T&gt;</c> is covariant, so an <c>Action</c> rule registered against an
    ///     interface reaches every event body implementing it. The <c>Func</c> overload cannot: it has
    ///     to assign the replacement back, and only the closed <c>Event&lt;T&gt;</c> exposes a setter.
    ///     That asymmetry is why a hierarchy-wide rule has to be the mutating one.
    /// </summary>
    [Fact]
    public async Task an_action_rule_against_an_interface_reaches_every_implementor()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "wholesale";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType(typeof(ContactRecorded));

            var seen = 0;
            options.Events.AddMaskingRuleForProtectedInformation<IPersonalData>(_ => seen++);
            options.Events.AddMaskingRuleForProtectedInformation<ContactRecorded>(
                x => x.Phone = $"REDACTED-{seen}");
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new ContactRecorded { Phone = "555-0100" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId), TestContext.Current.CancellationToken);

        await using var query = store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        // The interface rule ran (seen became 1) before the concrete rule read it.
        ((ContactRecorded)events[0].Data).Phone.ShouldBe("REDACTED-1");
    }

    // ---- selecting what to mask ----

    [Fact]
    public async Task a_stream_filter_masks_only_the_events_it_matches()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId,
                new CustomerRegistered("Frodo", "frodo@shire.test"),
                new CustomerRegistered("Sam", "sam@shire.test"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId, e => e.Version == 2), TestContext.Current.CancellationToken);

        var events = await EventsOfAsync(streamId);
        ((CustomerRegistered)events[0].Data).Name.ShouldBe("Frodo");
        ((CustomerRegistered)events[1].Data).Name.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     <c>IncludeEvents</c> takes an expression rather than a <c>Func</c>, and is the one selector
    ///     translated to SQL — so it spans streams and never fetches the events it does not want.
    /// </summary>
    [Fact]
    public async Task include_events_selects_across_streams()
    {
        var first = await StartStreamAsync(new CustomerRegistered("Frodo", "frodo@shire.test"));
        var second = await StartStreamAsync(new CustomerRegistered("Sam", "sam@shire.test"));
        var untouched = await StartStreamAsync(new OrderPlaced("MITHRIL-1"));

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeEvents(e => e.EventTypeName == "customer_registered"),
            TestContext.Current.CancellationToken);

        ((CustomerRegistered)(await EventsOfAsync(first))[0].Data).Name.ShouldBe("REDACTED");
        ((CustomerRegistered)(await EventsOfAsync(second))[0].Data).Name.ShouldBe("REDACTED");
        ((OrderPlaced)(await EventsOfAsync(untouched))[0].Data).Sku.ShouldBe("MITHRIL-1");
    }

    [Fact]
    public async Task a_batch_naming_nothing_is_refused()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.Advanced.ApplyEventDataMaskingAsync(_ => { }, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("at least one stream or event filter");
    }

    /// <summary>
    ///     Two sources reaching the same event must not apply its rule twice. Pinned with a rule that
    ///     is deliberately not idempotent, because an idempotent one would agree either way.
    /// </summary>
    [Fact]
    public async Task an_event_reached_by_two_sources_is_masked_once()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "twice";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType(typeof(CustomerRegistered));
            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = x.Name + "*" });
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new CustomerRegistered("Frodo", "f@shire.test"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId).IncludeStream(streamId),
            TestContext.Current.CancellationToken);

        await using var query = store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        ((CustomerRegistered)events[0].Data).Name.ShouldBe("Frodo*");
    }

    // ---- headers ----

    [Fact]
    public async Task a_header_marks_the_events_that_were_masked()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "headers";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableHeaders = true;
            options.Events.AddEventType(typeof(CustomerRegistered));
            options.Events.AddEventType(typeof(OrderPlaced));
            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = "REDACTED" });
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId,
                new CustomerRegistered("Frodo", "f@shire.test"),
                new OrderPlaced("MITHRIL-1"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId).AddHeader("masked-by", "ticket-42"),
            TestContext.Current.CancellationToken);

        await using var query = store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        events[0].Headers.ShouldNotBeNull();
        events[0].Headers!["masked-by"].ToString().ShouldBe("ticket-42");

        // The order event matched no rule, so it was never rewritten — and so never got the header.
        (events[1].Headers?.ContainsKey("masked-by") ?? false).ShouldBeFalse();
    }

    // ---- the caveat ----

    /// <summary>
    ///     Masking rewrites <c>fi_events.data</c> and nothing else. A projection that already folded
    ///     the unmasked body keeps what it derived, because the daemon's high-water mark is a sequence
    ///     and masking does not move it. This pins the documented limitation rather than a bug — Marten
    ///     behaves the same way, and a rebuild is what clears a snapshot.
    /// </summary>
    [Fact]
    public async Task masking_does_not_reach_a_snapshot_already_written()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "snapshots";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType(typeof(CustomerRegistered));
            options.Projections.Snapshot<CustomerRoster>(SnapshotLifecycle.Inline);
            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = "REDACTED" });
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new CustomerRegistered("Frodo", "f@shire.test"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId), TestContext.Current.CancellationToken);

        await using var query = store.LightweightSession();

        // The event is masked...
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);
        ((CustomerRegistered)events[0].Data).Name.ShouldBe("REDACTED");

        // ...and the snapshot derived from it before the masking still is not.
        var roster = await query.LoadAsync<CustomerRoster>(streamId, TestContext.Current.CancellationToken);
        roster.ShouldNotBeNull();
        roster.LatestName.ShouldBe("Frodo");
    }

    // ---- helpers ----

    private async Task<Guid> StartStreamAsync(object data)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, data);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<IReadOnlyList<IEvent>> EventsOfAsync(Guid streamId)
    {
        await using var session = _store.LightweightSession();
        return await session.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);
    }
}

public class CustomerRoster
{
    public Guid Id { get; set; }
    public string LatestName { get; set; } = string.Empty;

    public void Apply(CustomerRegistered registered) => LatestName = registered.Name;
}

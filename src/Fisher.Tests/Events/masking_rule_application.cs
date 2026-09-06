using JasperFx;

namespace Fisher.Tests.Events;

/// <summary>Two independent pieces of protected information on one class body.</summary>
public class ApplicantScreened : IPersonalData
{
    public string Name { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
}

/// <summary>The record shape, so the <c>Func</c> overload is exercised the same way.</summary>
public record ReferenceChecked(string Referee, string Phone) : IPersonalData;

/// <summary>
///     marten#5199 / polecat#422 — <b>every</b> matching masking rule has to run, not just the first
///     one that matches.
/// </summary>
/// <remarks>
///     <para>
///         Marten's bug was a <c>||</c> where a <c>|</c> was meant: the first rule that returned true
///         short-circuited the rest, so an event covered by two rules had only one of them applied and
///         the batch still reported success. The half-masked event is the worst possible outcome for a
///         right-to-erasure feature — it looks done.
///     </para>
///     <para>
///         <b>These tests are deliberately not about rule reach.</b>
///         <c>masking_event_data.an_action_rule_against_an_interface_reaches_every_implementor</c>
///         already covers an interface rule and a concrete rule together, and a store that keyed its
///         rules by event type would pass that test — an interface and a class are two different keys.
///         The discriminating shape is <b>two rules registered against the same type</b>, which a
///         type-keyed registry collapses to one and a short-circuiting apply loop runs once. Every test
///         here therefore asserts two separate transformations landed on one event body.
///     </para>
/// </remarks>
public class masking_rule_application : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("masking-rules");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Two <c>Action</c> rules on the same concrete type, each erasing a different member. A
    ///     registry keyed by event type keeps only one of them; an apply loop that stops at the first
    ///     match runs only one of them. Either way one member survives, and the test says which.
    /// </summary>
    [Fact]
    public async Task both_rules_registered_against_the_same_type_are_applied()
    {
        await using var store = StoreFor("same_type", options =>
        {
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name = "REDACTED");
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(
                x => x.NationalId = "REDACTED");
        });

        var masked = await MaskAsync<ApplicantScreened>(store,
            new ApplicantScreened { Name = "Frodo", NationalId = "SH-1234" });

        masked.Name.ShouldBe("REDACTED");
        masked.NationalId.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     The mirror of the test above with the registrations swapped. A short-circuiting loop passes
    ///     one order and fails the other, so pinning only one order pins nothing.
    /// </summary>
    [Fact]
    public async Task both_rules_are_applied_regardless_of_registration_order()
    {
        await using var store = StoreFor("same_type_reversed", options =>
        {
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(
                x => x.NationalId = "REDACTED");
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name = "REDACTED");
        });

        var masked = await MaskAsync<ApplicantScreened>(store,
            new ApplicantScreened { Name = "Frodo", NationalId = "SH-1234" });

        masked.Name.ShouldBe("REDACTED");
        masked.NationalId.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     The same, through the <c>Func</c> overload — the one a record needs. Each rule replaces the
    ///     body, so the second has to see the first one's replacement rather than the original.
    /// </summary>
    [Fact]
    public async Task two_func_rules_on_one_record_type_compose()
    {
        await using var store = StoreFor("func_pair", options =>
        {
            options.Events.AddMaskingRuleForProtectedInformation<ReferenceChecked>(
                x => x with { Referee = "REDACTED" });
            options.Events.AddMaskingRuleForProtectedInformation<ReferenceChecked>(
                x => x with { Phone = "REDACTED" });
        });

        var masked = await MaskAsync<ReferenceChecked>(store, new ReferenceChecked("Gandalf", "555-0100"));

        masked.Referee.ShouldBe("REDACTED");
        masked.Phone.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     An interface rule and a concrete rule, both asserted by their effect on the body rather than
    ///     by a counter. This is the shape a type-keyed registry passes, which is exactly why it is not
    ///     the only test here.
    /// </summary>
    [Fact]
    public async Task an_interface_rule_and_a_concrete_rule_both_apply()
    {
        await using var store = StoreFor("interface_and_concrete", options =>
        {
            options.Events.AddMaskingRuleForProtectedInformation<IPersonalData>(x =>
            {
                if (x is ApplicantScreened applicant)
                {
                    applicant.NationalId = "REDACTED";
                }
            });

            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name = "REDACTED");
        });

        var masked = await MaskAsync<ApplicantScreened>(store,
            new ApplicantScreened { Name = "Frodo", NationalId = "SH-1234" });

        masked.Name.ShouldBe("REDACTED");
        masked.NationalId.ShouldBe("REDACTED");
    }

    /// <summary>
    ///     Three rules, so a fix that merely un-short-circuits the second one still has something to
    ///     fail on.
    /// </summary>
    [Fact]
    public async Task a_third_matching_rule_runs_too()
    {
        await using var store = StoreFor("three_rules", options =>
        {
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name = "A");
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name += "B");
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantScreened>(x => x.Name += "C");
        });

        var masked = await MaskAsync<ApplicantScreened>(store,
            new ApplicantScreened { Name = "Frodo", NationalId = "SH-1234" });

        masked.Name.ShouldBe("ABC");
    }

    // ---- helpers ----

    private DocumentStore StoreFor(string schema, Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType(typeof(ApplicantScreened));
            options.Events.AddEventType(typeof(ReferenceChecked));

            configure(options);
        });

    private async Task<T> MaskAsync<T>(DocumentStore store, object @event)
    {
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, @event);
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Token);

        await using var query = store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);

        return (T)events.ShouldHaveSingleItem().Data;
    }
}

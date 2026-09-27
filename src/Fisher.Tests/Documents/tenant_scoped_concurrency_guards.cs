using JasperFx;
using JasperFx.Metadata;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Documents;

/// <summary>
///     Both concurrency guards are scoped to <c>(tenant, id)</c> rather than to <c>id</c> alone
///     (jasperfx#898).
/// </summary>
/// <remarks>
///     <para>
///         <b>Written because the shared fact was unsatisfiable, kept because it still says more.</b>
///         <c>DocumentConjoinedTenancyCompliance.optimistic_concurrency_is_scoped_to_the_tenant_for_a_shared_id</c>
///         shipped in 2.75.0 requiring a refusal no correct store can produce: it re-stored the very
///         instance that had just performed the winning write, which a committed <c>Store</c> has
///         written the landed version back onto, so nothing about it was stale. That contradicted
///         <c>GuidOptimisticConcurrencyCompliance.a_successful_write_moves_the_instances_own_version_on</c>
///         — <c>ComplianceShipment.Version</c>'s own doc comment says it carries "the landed version on
///         the way out", and Fisher is the reference store for that fact (fisher#245) — so no store
///         could be green on both. jasperfx#903, fixed in 2.75.1 exactly as proposed: the stale instance
///         is now separately loaded, which is what "stale" has to mean on a store with write-back.
///     </para>
///     <para>
///         So the suite now covers the two Guid facts below, and this class overlaps it there. It stays
///         for the third: <b>revisions counted per <c>(tenant, id)</c> over a deliberately advanced
///         count</b>, which needs <c>UpdateRevision</c> rather than <c>Store</c>, because Fisher follows
///         Marten's rule that an explicit revision must be strictly <em>greater</em> than the stored one
///         (fisher#228). That interaction — a tenancy rule and a concurrency rule that arrived from
///         different directions — is not something a portable fixture has the vocabulary for, and it is
///         the half most likely to be broken by a change to either.
///     </para>
///     <para>
///         The arrangement is the shared fact's verbatim, including the shared id across two tenants,
///         which is the whole point: a guard whose <c>WHERE</c> matches on <c>(id, version)</c> and omits
///         the tenant reads whichever row it finds first, so it refuses a write that conflicts with
///         nothing and admits one that does.
///     </para>
///     <para>
///         <b>Both directions on both guards</b>, following the shared suite's own discipline. A store
///         that ignored the tenant would still answer correctly for whichever tenant happened to own
///         the row the guard found, so asserting only the refusal — or only the success — catches
///         nothing.
///     </para>
/// </remarks>
public class tenant_scoped_concurrency_guards : IAsyncLifetime
{
    private const string North = "north";
    private const string South = "south";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tenant-scoped-guards");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<GuardedShipment>().MultiTenanted();
            options.Schema.For<CountedLedger>().MultiTenanted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task StoreForAsync<T>(string tenantId, T document) where T : notnull
    {
        await using var session = _store.LightweightSession(tenantId);

        session.Store(document);
        await session.SaveChangesAsync(Token);
    }

    private async Task<T> LoadForAsync<T>(string tenantId, Guid id) where T : class
    {
        await using var session = _store.LightweightSession(tenantId);

        return (await session.LoadAsync<T>(id, Token))
               ?? throw new InvalidOperationException($"No {typeof(T).Name} '{id}' in tenant '{tenantId}'.");
    }

    /// <summary>
    ///     A <see cref="IVersioned" /> write in one tenant is not in conflict with another tenant's copy
    ///     of the same id, however far the two versions have diverged.
    /// </summary>
    [Fact]
    public async Task a_guid_version_guard_does_not_see_another_tenants_copy_of_the_id()
    {
        var shared = Guid.NewGuid();

        await StoreForAsync(North, new GuardedShipment { Id = shared, Supplier = North, Status = "new" });
        await StoreForAsync(South, new GuardedShipment { Id = shared, Supplier = South, Status = "new" });

        // Move north's version on twice, so the two tenants' stored versions cannot coincide by luck.
        // From here a guard that ignored the tenant is reading whichever row it finds first.
        for (var i = 0; i < 2; i++)
        {
            var advancing = await LoadForAsync<GuardedShipment>(North, shared);
            advancing.Status = $"advanced-{i}";
            await StoreForAsync(North, advancing);
        }

        var theirs = await LoadForAsync<GuardedShipment>(South, shared);
        theirs.Status = "shipped";

        // Not in conflict with anything. A tenant-blind guard matches no row and reports a
        // ConcurrencyException over a write that conflicts with nothing.
        await StoreForAsync(South, theirs);

        (await LoadForAsync<GuardedShipment>(South, shared)).Status.ShouldBe("shipped");

        // And north is untouched by south's write, which is the other direction.
        (await LoadForAsync<GuardedShipment>(North, shared)).Status.ShouldBe("advanced-1");
    }

    /// <summary>
    ///     The guard still has teeth <em>inside</em> a tenant: a separately loaded instance whose row has
    ///     moved on is refused.
    /// </summary>
    /// <remarks>
    ///     Separately loaded rather than re-stored, for the write-back reason in this class's remarks.
    ///     Without this test the one above would pass against a store that had simply stopped guarding.
    /// </remarks>
    [Fact]
    public async Task a_genuinely_stale_guid_version_is_still_refused_within_the_tenant()
    {
        var shared = Guid.NewGuid();

        await StoreForAsync(North, new GuardedShipment { Id = shared, Supplier = North, Status = "new" });
        await StoreForAsync(South, new GuardedShipment { Id = shared, Supplier = South, Status = "new" });

        // Two independent reads of north's row. The second write makes the first instance stale.
        var stale = await LoadForAsync<GuardedShipment>(North, shared);
        var winner = await LoadForAsync<GuardedShipment>(North, shared);

        winner.Status = "shipped";
        await StoreForAsync(North, winner);

        stale.Status = "stale";

        await Should.ThrowAsync<ConcurrencyException>(() => StoreForAsync(North, stale));

        // Refused rather than partially applied, and south's copy never entered into it.
        (await LoadForAsync<GuardedShipment>(North, shared)).Status.ShouldBe("shipped");
        (await LoadForAsync<GuardedShipment>(South, shared)).Status.ShouldBe("new");
    }

    /// <summary>
    ///     <see cref="IRevisioned" /> revisions are counted per <c>(tenant, id)</c>, so each tenant's
    ///     first insert of a shared id is revision 1.
    /// </summary>
    /// <remarks>
    ///     The landed revision is the sharp assertion rather than a refusal, which is the shared suite's
    ///     reading too: a store counting per id hands the second tenant's first insert the next number
    ///     after the first tenant's, which throws nothing and is invisible to a caller who never
    ///     compares across tenants.
    /// </remarks>
    [Fact]
    public async Task numeric_revisions_are_counted_per_tenant_for_a_shared_id()
    {
        var shared = Guid.NewGuid();

        await StoreForAsync(North, new CountedLedger { Id = shared, Owner = North });

        // Advance north well past 1, so "per id" and "per (tenant, id)" cannot agree by accident.
        // Through UpdateRevision rather than Store, because Fisher follows Marten's rule that an
        // explicit revision must be strictly GREATER than the stored one — re-storing an instance
        // still carrying the revision it was loaded with is a ConcurrencyException, not an increment
        // (fisher#228). That is orthogonal to what this test is about and is pinned by numeric_revisions.
        for (var i = 0; i < 3; i++)
        {
            var advancing = await LoadForAsync<CountedLedger>(North, shared);
            advancing.Owner = $"{North}-{i}";

            await using var session = _store.LightweightSession(North);
            session.UpdateRevision(advancing, advancing.Version + 1);
            await session.SaveChangesAsync(Token);
        }

        (await LoadForAsync<CountedLedger>(North, shared)).Version.ShouldBe(4);

        await StoreForAsync(South, new CountedLedger { Id = shared, Owner = South });

        var theirs = await LoadForAsync<CountedLedger>(South, shared);
        theirs.Version.ShouldBe(1);
        theirs.Owner.ShouldBe(South);

        // North's count is unmoved by south's insert.
        (await LoadForAsync<CountedLedger>(North, shared)).Version.ShouldBe(4);
    }

    public class GuardedShipment : IVersioned
    {
        public Guid Id { get; set; }
        public string Supplier { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid Version { get; set; }
    }

    public class CountedLedger : IRevisioned
    {
        public Guid Id { get; set; }
        public string Owner { get; set; } = string.Empty;
        public int Version { get; set; }
    }
}

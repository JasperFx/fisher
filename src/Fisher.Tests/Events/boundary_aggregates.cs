using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;

namespace Fisher.Tests.Events;

public record WardId(Guid Value);

public record PatientId(Guid Value);

public record Admitted(string Patient);

public record Discharged(string Patient);

/// <summary>
///     A pure boundary aggregate: it spans streams by tag and is keyed to none of them, so it carries no
///     <c>Id</c> and no <c>[Identity]</c> member. <c>[BoundaryAggregate]</c> is what tells JasperFx's
///     source generator to emit an evolver for it anyway — keyed on a vestigial <c>string</c> TId, which
///     is why <see cref="AggregateIdentity.ResolveIdType" /> has to answer <c>string</c> for it.
/// </summary>
[BoundaryAggregate]
public partial class WardOccupancy
{
    public List<string> Patients { get; } = [];

    public void Apply(Admitted e) => Patients.Add(e.Patient);

    public void Apply(Discharged e) => Patients.Remove(e.Patient);
}

/// <summary>
///     The same shape without the marker. Nothing here should work — this type exists to pin that the
///     exemption is the attribute rather than "no identity member is fine now".
/// </summary>
public class UnmarkedWardOccupancy
{
    public List<string> Patients { get; } = [];

    public void Apply(Admitted e) => Patients.Add(e.Patient);
}

/// <summary>
///     An aggregate reached only through a DCB tag boundary must not need a single-stream identity
///     (gh-135). <c>[BoundaryAggregate]</c> is JasperFx's marker for exactly that case, and honouring it
///     is what keeps the same model compiling and running against Fisher and Polecat alike.
/// </summary>
/// <remarks>
///     <para>
///         The coverage that matters is <b>with events present</b>. <c>FetchForWritingByTags</c> only
///         resolves the aggregator when the query finds something, so a boundary over an empty result —
///         the ordinary "this must not exist yet" assertion — succeeded even before this was honoured.
///         A suite that only exercised the empty path would be green over a model that throws the first
///         time the boundary actually matches.
///     </para>
/// </remarks>
public class boundary_aggregates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("boundary-aggregate");
    private readonly WardId _ward = new(Guid.NewGuid());
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<WardId>("ward").ForAggregate<WardOccupancy>();
            options.Events.RegisterTagType<PatientId>("patient").ForAggregate<WardOccupancy>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private EventTagQuery WardQuery => new EventTagQuery().Or<WardId>(_ward);

    private async Task AdmitAsync(params string[] patients)
    {
        await using var session = _store.LightweightSession();

        foreach (var patient in patients)
        {
            var admitted = session.Events.BuildEvent(new Admitted(patient));
            admitted.WithTag(new PatientId(Guid.NewGuid()), _ward);
            session.Events.StartStream(Guid.NewGuid(), admitted);
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task fetch_for_writing_by_tags_folds_an_identity_less_aggregate()
    {
        await AdmitAsync("Frodo", "Sam");

        await using var session = _store.LightweightSession();
        var boundary = await session.Events.FetchForWritingByTags<WardOccupancy>(
            WardQuery, TestContext.Current.CancellationToken);

        boundary.Aggregate.ShouldNotBeNull();
        boundary.Aggregate.Patients.ShouldBe(["Frodo", "Sam"]);
    }

    [Fact]
    public async Task aggregate_by_tags_folds_an_identity_less_aggregate()
    {
        await AdmitAsync("Frodo", "Sam");

        await using var session = _store.LightweightSession();
        var occupancy = await session.Events.AggregateByTagsAsync<WardOccupancy>(
            WardQuery, TestContext.Current.CancellationToken);

        occupancy.ShouldNotBeNull();
        occupancy.Patients.ShouldBe(["Frodo", "Sam"]);
    }

    /// <summary>
    ///     The boundary is still a boundary: an append through it commits, and the events it wrote are
    ///     what the next fetch folds. Pinned separately from the read above because resolving the
    ///     aggregator is only half of what the DCB path does with the type.
    /// </summary>
    [Fact]
    public async Task an_identity_less_boundary_still_appends_and_guards()
    {
        await AdmitAsync("Frodo");

        await using (var session = _store.LightweightSession())
        {
            var boundary = await session.Events.FetchForWritingByTags<WardOccupancy>(
                WardQuery, TestContext.Current.CancellationToken);

            boundary.Aggregate!.Patients.ShouldBe(["Frodo"]);

            var discharged = session.Events.BuildEvent(new Discharged("Frodo"));
            discharged.WithTag(new PatientId(Guid.NewGuid()), _ward);
            boundary.AppendOne(discharged);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = _store.LightweightSession();
        var occupancy = await reader.Events.AggregateByTagsAsync<WardOccupancy>(
            WardQuery, TestContext.Current.CancellationToken);

        occupancy!.Patients.ShouldBeEmpty();
    }

    /// <summary>
    ///     The empty-boundary path, which never reached the aggregator and so passed before gh-135 was
    ///     fixed. Kept as the counterpart to the tests above rather than as the coverage: what it pins is
    ///     that honouring the marker did not change the "nothing matched" answer from null.
    /// </summary>
    [Fact]
    public async Task an_empty_boundary_over_an_identity_less_aggregate_is_null()
    {
        await using var session = _store.LightweightSession();
        var boundary = await session.Events.FetchForWritingByTags<WardOccupancy>(
            WardQuery, TestContext.Current.CancellationToken);

        boundary.Aggregate.ShouldBeNull();
    }

    /// <summary>
    ///     The marker is the whole exemption. An identity-less aggregate without it is still refused —
    ///     it is far more often a forgotten <c>Id</c> than a deliberate boundary aggregate, which is the
    ///     same reason the source generator emits nothing for one.
    /// </summary>
    [Fact]
    public void an_unmarked_identity_less_aggregate_is_still_refused()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => AggregateIdentity.ResolveIdType(typeof(UnmarkedWardOccupancy), StreamIdentity.AsGuid));

        ex.Message.ShouldContain(nameof(UnmarkedWardOccupancy));

        // And the message names the boundary case, so somebody who arrived here down a DCB path is not
        // sent looking for a missing Id their model has no use for.
        ex.Message.ShouldContain("BoundaryAggregate");
    }

    /// <summary>
    ///     The identity type the marker resolves to is <c>string</c>, not the store's stream identity
    ///     primitive — that is the type the generated evolver is keyed on, and a Guid-identity store
    ///     must not talk itself into <c>Guid</c> here.
    /// </summary>
    [Theory]
    [InlineData(StreamIdentity.AsGuid)]
    [InlineData(StreamIdentity.AsString)]
    public void a_boundary_aggregate_resolves_to_the_vestigial_string_identity(StreamIdentity identity)
        => AggregateIdentity.ResolveIdType(typeof(WardOccupancy), identity).ShouldBe(typeof(string));
}

using JasperFx;
using Fisher.Projections;
using Fisher.Storage;
using Fisher.Tests.Configuration;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     The managed half of fisher#398. Whether these paths work in a native image is
///     <c>smoke/aot-consumer</c>'s to say, since nothing under CoreCLR can see an AOT failure. What can
///     be pinned here is that the AOT-safe paths are the ones actually taken.
/// </summary>
public class native_aot_event_paths
{
    /// <remarks>
    ///     The four canonical identity types are closed with ordinary generic calls, which is what makes
    ///     them work in a native image. The concrete type is the evidence, since the reflective path
    ///     builds the identical type.
    /// </remarks>
    [Fact]
    public void the_canonical_identity_types_are_closed_statically()
    {
        var factory = new SingleStreamProjectionFactory();

        factory.Create<TallySnapshot>(typeof(Guid))
            .ShouldBeOfType<SingleStreamProjection<TallySnapshot, Guid>>();
        factory.Create<KeyedTally>(typeof(string))
            .ShouldBeOfType<SingleStreamProjection<KeyedTally, string>>();
    }

    /// <remarks>
    ///     An undeclared wrapper still works under the JIT through the reflective fallback. In a native
    ///     image it is refused by name, naming the two registrations that declare it, which this process
    ///     cannot exercise.
    /// </remarks>
    [Fact]
    public void an_undeclared_strong_typed_identity_still_works_under_the_jit()
    {
        var factory = new SingleStreamProjectionFactory();

        factory.Create<Kiln>(typeof(KilnId)).ShouldBeOfType<SingleStreamProjection<Kiln, KilnId>>();
        factory.IsDeclared(typeof(Kiln)).ShouldBeFalse();
    }

    /// <remarks>
    ///     fisher#423. The concrete type cannot tell the static path from the reflective one, so the
    ///     declaration is what is asserted: it is the thing a native image depends on, and the smoke
    ///     consumer is what shows the image running it.
    /// </remarks>
    [Fact]
    public void snapshot_with_a_named_identity_type_declares_it()
    {
        var options = new StoreOptions();
        options.Projections.Snapshot<Kiln, KilnId>(SnapshotLifecycle.Inline);

        options.EventGraph.AggregateProjections.IsDeclared(typeof(Kiln)).ShouldBeTrue();
        options.EventGraph.AggregateProjections.Create<Kiln>(typeof(KilnId))
            .ShouldBeOfType<SingleStreamProjection<Kiln, KilnId>>();
    }

    [Fact]
    public void live_stream_aggregation_declares_the_identity_type()
    {
        var options = new StoreOptions();
        options.Projections.LiveStreamAggregation<Kiln, KilnId>();

        options.EventGraph.AggregateProjections.IsDeclared(typeof(Kiln)).ShouldBeTrue();
        options.Projections.All.ShouldBeEmpty();
    }

    /// <remarks>
    ///     The source generator keys the dispatcher on the identity member's type, so a projection closed
    ///     over anything else would fail at the first event, far from the registration.
    /// </remarks>
    [Fact]
    public void an_identity_type_the_member_disagrees_with_is_refused()
    {
        var options = new StoreOptions();

        Should.Throw<ArgumentException>(() => options.Projections.Snapshot<Kiln, Guid>(SnapshotLifecycle.Inline))
            .Message.ShouldContain("KilnId");
        Should.Throw<ArgumentException>(() => options.Projections.LiveStreamAggregation<Kiln, Guid>());
    }

    [Fact]
    public async Task a_declared_strong_typed_aggregate_snapshots_and_aggregates_end_to_end()
    {
        var token = TestContext.Current.CancellationToken;
        using var database = TemporaryDatabase.Create("declared-strong-aggregate");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<Kiln, KilnId>(SnapshotLifecycle.Inline);
        });

        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Kiln>(id, new KilnFired(900), new KilnFired(1100));
            await session.SaveChangesAsync(token);
        }

        await using var query = store.QuerySession();

        (await query.LoadAsync<Kiln, KilnId>(new KilnId(id), token))!.Firings.ShouldBe(2);
        (await query.Events.AggregateStreamAsync<Kiln>(id, token: token))!.Firings.ShouldBe(2);
    }

    /// <remarks>
    ///     The one json_each array writer. A Guid is the lowercase canonical form every Fisher column
    ///     holds; anything else binds to text nothing matches.
    /// </remarks>
    [Fact]
    public void id_arrays_are_written_in_the_form_the_columns_hold()
    {
        var guid = Guid.Parse("A1B2C3D4-0000-0000-0000-00000000000F");

        SqliteJsonArrays.Write(new object[] { guid, "key", 7, 8L })
            .ShouldBe("[\"a1b2c3d4-0000-0000-0000-00000000000f\",\"key\",7,8]");

        SqliteJsonArrays.Write(Array.Empty<object>()).ShouldBe("[]");
    }

    [Fact]
    public void an_unsupported_id_value_is_refused_by_name()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SqliteJsonArrays.Write(new object[] { 1.5m }))
            .Message.ShouldContain("System.Decimal");
    }
}

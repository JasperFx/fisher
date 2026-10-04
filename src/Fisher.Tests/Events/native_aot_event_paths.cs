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
        SingleStreamProjectionFactory.Create<TallySnapshot>(typeof(Guid))
            .ShouldBeOfType<SingleStreamProjection<TallySnapshot, Guid>>();
        SingleStreamProjectionFactory.Create<KeyedTally>(typeof(string))
            .ShouldBeOfType<SingleStreamProjection<KeyedTally, string>>();
    }

    /// <remarks>
    ///     A strong-typed wrapper still works under the JIT through the reflective fallback. In a native
    ///     image it is refused by name (jasperfx#950), which this process cannot exercise.
    /// </remarks>
    [Fact]
    public void a_strong_typed_identity_still_works_under_the_jit()
    {
        SingleStreamProjectionFactory.Create<Kiln>(typeof(KilnId))
            .ShouldBeOfType<SingleStreamProjection<Kiln, KilnId>>();
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

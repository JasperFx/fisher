using Fisher.Tests.Documents;
using JasperFx.Core.Reflection;

namespace Fisher.Tests.Configuration;

/// <summary>
///     <c>StoreOptions.RegisterValueType&lt;T&gt;()</c> (fisher#75) — the call Fisher does not need and
///     carries anyway, so a strong-typed-id configuration block reads identically against all three
///     stores.
/// </summary>
/// <remarks>
///     The interesting property is <em>not</em> that registering makes a wrapper work — it already did,
///     and <c>strong_typed_identities</c> covers that. It is that the call means something rather than
///     being an accepted no-op: naming a type is an assertion it is a wrapper, so a type that is not one
///     is a configuration error reported here instead of surfacing later as "has no identity member"
///     from a place that cannot mention the wrapper.
/// </remarks>
public class registering_value_types
{
    [Fact]
    public void a_wrapper_resolves_to_its_inner_type()
    {
        var options = new StoreOptions();

        options.RegisterValueType<RodId>().SimpleType.ShouldBe(typeof(Guid));
        options.RegisterValueType<HookId>().SimpleType.ShouldBe(typeof(string));
        options.RegisterValueType<SwivelId>().SimpleType.ShouldBe(typeof(int));
    }

    /// <remarks>
    ///     The builder shape rather than a constructor — the other form <c>ValueTypeInfo</c> accepts,
    ///     included because a registration call that only understood constructors would look correct.
    /// </remarks>
    [Fact]
    public void a_wrapper_built_by_a_static_factory_resolves_too()
    {
        new StoreOptions().RegisterValueType<SpoonId>().SimpleType.ShouldBe(typeof(long));
    }

    [Fact]
    public void the_non_generic_overload_answers_the_same()
    {
        var options = new StoreOptions();

        options.RegisterValueType(typeof(RodId)).SimpleType
            .ShouldBe(options.RegisterValueType<RodId>().SimpleType);
    }

    [Fact]
    public void a_type_that_is_not_a_wrapper_is_refused()
    {
        Should.Throw<InvalidValueTypeException>(() => new StoreOptions().RegisterValueType<NotAWrapper>())
            .Message.ShouldContain(nameof(NotAWrapper));
    }

    /// <remarks>
    ///     A perfectly well-shaped wrapper around something Fisher cannot store as an identity. The
    ///     message has to say which four it can, because the shape is not what is wrong.
    /// </remarks>
    [Fact]
    public void a_wrapper_around_an_unstorable_type_is_refused_by_its_inner_type()
    {
        var message = Should.Throw<InvalidValueTypeException>(
            () => new StoreOptions().RegisterValueType<PriceId>()).Message;

        message.ShouldContain("Decimal");
        message.ShouldContain("Guid, string, int or long");
    }

    /// <remarks>
    ///     Registration is a validation, not a mutation — the store discovers wrappers either way, so
    ///     calling it twice, or not at all, has to reach the same place.
    /// </remarks>
    [Fact]
    public void registering_twice_is_harmless()
    {
        var options = new StoreOptions();

        options.RegisterValueType<RodId>();
        options.RegisterValueType<RodId>().SimpleType.ShouldBe(typeof(Guid));
    }
}

public class NotAWrapper
{
    public string First { get; set; } = string.Empty;
    public string Second { get; set; } = string.Empty;
}

public readonly record struct PriceId(decimal Value);

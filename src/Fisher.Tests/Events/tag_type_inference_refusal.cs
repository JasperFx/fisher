using JasperFx;
using JasperFx.Core.Reflection;

namespace Fisher.Tests.Events;

public record TwoPartTag(Guid Region, Guid Bay);

/// <summary>
///     fisher#493. A failed tag inference names the overload that does not infer. In a native image this
///     is every tag, which the smoke consumer covers. Here it is a type that genuinely has no single value.
/// </summary>
public class tag_type_inference_refusal
{
    [Fact]
    public void a_failed_inference_names_the_explicit_overload()
    {
        // JasperFx's refusal since fisher#574, which adopted EventRegistry's tag registration. It names the
        // whole registration line, suffix included, so the suffix is Fisher's own.
        var ex = Should.Throw<InvalidOperationException>(() => new StoreOptions().Events.RegisterTagType<TwoPartTag>());

        ex.InnerException.ShouldBeOfType<InvalidValueTypeException>();
        ex.Message.ShouldContain("RegisterTagType<TwoPartTag, TInner>(x => x.Value, \"two_part_tag\")");
    }
}

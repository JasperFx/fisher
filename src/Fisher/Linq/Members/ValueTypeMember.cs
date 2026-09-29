using JasperFx.Core.Reflection;

namespace Fisher.Linq.Members;

/// <summary>
///     A member whose CLR type is a registered strong-typed wrapper, read and compared as the primitive
///     it wraps (fisher#356).
/// </summary>
/// <remarks>
///     The JSON holds the primitive once the wrapper is registered — see
///     <c>ValueTypeJsonConverterFactory</c> — so everything about the column is the inner member's: the
///     locator, the timestamp or Guid handling, whether it orders. What this adds is the one thing the
///     inner member cannot know: a comparison value arrives as the WRAPPER, and Microsoft.Data.Sqlite
///     refuses to bind one ("No mapping exists from object type …"). It is unwrapped here and then handed
///     to the inner member's own conversion, so a Guid-backed wrapper still binds lowercase canonical text.
/// </remarks>
internal sealed class ValueTypeMember : IQueryableMember
{
    private readonly ValueTypeInfo _info;

    public ValueTypeMember(IQueryableMember inner, Type memberType, ValueTypeInfo info)
    {
        Inner = inner;
        MemberType = memberType;
        _info = info;
    }

    /// <summary>The member as its primitive — what <c>x.OrderId.Value</c> resolves to.</summary>
    public IQueryableMember Inner { get; }

    public Type MemberType { get; }
    public string TypedLocator => Inner.TypedLocator;
    public string RawLocator => Inner.RawLocator;
    public bool IsBoolean => false;
    public bool AllowsRangeComparison => Inner.AllowsRangeComparison;

    public object? ConvertValue(object? value) => Inner.ConvertValue(Unwrap(_info, value));

    /// <summary>The wrapper's inner value, or the value unchanged when it is not that wrapper.</summary>
    internal static object? Unwrap(ValueTypeInfo info, object? value)
        => value is not null && value.GetType() == info.OuterType ? info.ValueProperty.GetValue(value) : value;
}

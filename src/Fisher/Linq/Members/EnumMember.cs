using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weasel.Core;

namespace Fisher.Linq.Members;

/// <summary>
///     An enum member, aware of how the serializer stores enums.
/// </summary>
/// <remarks>
///     Mirrors Polecat's <c>EnumMember</c> minus its <c>CAST(... AS int)</c>: under
///     <see cref="EnumStorage.AsInteger" /> the value is a JSON number and <c>json_extract</c> already
///     returns it as INTEGER, so there is nothing to cast.
/// </remarks>
internal class EnumMember : IQueryableMember
{
    private readonly EnumStorage _enumStorage;
    private readonly JsonNamingPolicy? _namingPolicy;

    public EnumMember(string locator, Type memberType, EnumStorage enumStorage, JsonNamingPolicy? namingPolicy)
    {
        RawLocator = locator;
        TypedLocator = locator;
        MemberType = memberType;
        _enumStorage = enumStorage;
        _namingPolicy = namingPolicy;
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator { get; }
    public bool IsBoolean => false;

    public object? ConvertValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (_enumStorage != EnumStorage.AsString)
        {
            return Convert.ToInt32(value);
        }

        // AsString names go through the configured naming policy, because the default Serializer wires
        // JsonStringEnumConverter(PropertyNamingPolicy) — so a Minute member is stored as "minute"
        // under camelCase and a predicate comparing 'Minute' matches nothing.
        //
        // The value arrives one of three ways depending on how the expression was lowered: an already
        // resolved string, the enum instance, or — for an equality predicate — the constant converted
        // to its underlying integer. All three resolve back to the member name first.
        string? name;
        if (value is string s)
        {
            name = s;
        }
        else if (value.GetType().IsEnum)
        {
            name = value.ToString();
        }
        else
        {
            name = Enum.GetName(MemberType, value) ?? value.ToString();
        }

        if (name == null)
        {
            return null;
        }

        // An explicit [JsonStringEnumMemberName] wins verbatim: STJ writes the attribute name as-is
        // and does not apply the naming policy on top of it, so neither may the literal.
        var attribute = MemberType.GetField(name)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
        if (attribute != null)
        {
            return attribute.Name;
        }

        return _namingPolicy != null ? _namingPolicy.ConvertName(name) : name;
    }
}

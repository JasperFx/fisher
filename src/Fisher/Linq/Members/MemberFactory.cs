using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fisher.Serialization;
using Fisher.Storage;
using Weasel.Core;

namespace Fisher.Linq.Members;

/// <summary>
///     Resolves C# member expressions to the SQL locators Fisher queries documents through.
/// </summary>
/// <remarks>
///     <para>
///         The SQLite counterpart of Polecat's <c>MemberFactory</c>, and markedly smaller. Everything
///         Polecat needs for <c>CAST</c>/<c>RETURNING</c> selection — <c>SqlTypeMap</c>,
///         <c>BuildTypedLocator</c>, <c>SupportsReturning</c>, the native-json-type switch — has no
///         analogue here, because <c>json_extract</c> returns a JSON number as INTEGER or REAL and a
///         JSON string as TEXT without being asked. The index-aware locator rewriting is likewise
///         absent: Fisher has no computed-column indexes to line a predicate up with yet.
///     </para>
///     <para>
///         The JSON path must be built with the <em>serializer's</em> naming policy, not the CLR member
///         name. Fisher defaults to camelCase (see <see cref="Serializer.DefaultOptions" />), so a
///         <c>Name</c> property lives at <c>$.name</c> and a path built from the member name alone
///         would match nothing.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
    Justification = "Class-level: reflects over document property/field members to resolve JSON paths. Document types and their property graph are preserved at the LINQ registration boundary on the caller side, per the AOT publishing guide.")]
internal class MemberFactory : IMemberResolver
{
    private readonly JsonNamingPolicy? _namingPolicy;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly EnumStorage _enumStorage;
    private readonly DocumentMapping _mapping;

    public MemberFactory(StoreOptions options, DocumentMapping mapping)
    {
        _mapping = mapping;
        _enumStorage = options.Serializer.EnumStorage;

        if (options.Serializer is Serializer serializer)
        {
            _serializerOptions = serializer.Options;
            _namingPolicy = serializer.Options.PropertyNamingPolicy;
        }
        else
        {
            // A custom ISerializer does not expose its options. camelCase is the documented default,
            // so assume it rather than the identity policy — guessing wrong here yields a path that
            // silently matches nothing.
            _serializerOptions = Serializer.DefaultOptions();
            _namingPolicy = JsonNamingPolicy.CamelCase;
        }
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        // The identity lives in its own column, not in the JSON body.
        if (expression.Expression is ParameterExpression && IsIdentityMember(expression.Member))
        {
            return new IdMember(_mapping.IdType);
        }

        var jsonPath = BuildJsonPath(expression);
        var memberType = GetMemberType(expression.Member);
        return CreateMember(jsonPath, memberType);
    }

    private bool IsIdentityMember(MemberInfo member)
        => member.Name == _mapping.IdMember.Name;

    private IQueryableMember CreateMember(string jsonPath, Type memberType)
    {
        var locator = $"json_extract(data, '{jsonPath}')";
        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (underlying.IsEnum)
        {
            return new EnumMember(locator, underlying, _enumStorage, _namingPolicy);
        }

        if (underlying == typeof(bool))
        {
            return new QueryableMember(locator, memberType, isBoolean: true);
        }

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
                                           || underlying == typeof(DateOnly) || underlying == typeof(TimeOnly))
        {
            return new DateMember(locator, memberType, _serializerOptions);
        }

        return new QueryableMember(locator, memberType);
    }

    /// <summary>
    ///     Walks a possibly-nested member access back to the parameter, producing <c>$.a.b.c</c>.
    /// </summary>
    private string BuildJsonPath(MemberExpression expression)
    {
        var segments = new List<string>();
        var current = expression;

        while (current != null)
        {
            segments.Insert(0, GetJsonPropertyName(current.Member));

            if (current.Expression is ParameterExpression)
            {
                break;
            }

            current = current.Expression as MemberExpression;
        }

        return "$." + string.Join(".", segments);
    }

    /// <summary>
    ///     The JSON key a member serializes to. An explicit <c>[JsonPropertyName]</c> wins verbatim —
    ///     System.Text.Json does not apply the naming policy on top of an explicit name, so neither may
    ///     the path.
    /// </summary>
    private string GetJsonPropertyName(MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attribute != null)
        {
            return attribute.Name;
        }

        return _namingPolicy?.ConvertName(member.Name) ?? member.Name;
    }

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f => f.FieldType,
            _ => throw new NotSupportedException($"Unsupported member type: {member.MemberType}")
        };
}

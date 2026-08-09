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
///         JSON string as TEXT without being asked. Index-aware locator rewriting <em>is</em> present,
///         as the one line of <see cref="ResolveMember(MemberInfo[])" /> that swaps in a
///         <see cref="DuplicatedMember" />.
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
    private readonly DocumentMapping? _mapping;
    private readonly bool _hasIdentityColumn;
    private readonly string _qualifier;

    /// <param name="options">The store's options — serializer naming policy and enum storage.</param>
    /// <param name="mapping">The document the members belong to.</param>
    /// <param name="tableAlias">
    ///     The table alias every locator is qualified with, or null for the unqualified form.
    /// </param>
    /// <remarks>
    ///     <b>The alias belongs here rather than being applied to a finished locator</b> (fisher#25). A
    ///     join needs <c>json_extract(outer_t.data, '$.x')</c>, and the alias goes on <c>data</c> —
    ///     inside the call — not on the <c>json_extract</c> result. Rewriting the rendered string
    ///     afterwards, which is Polecat's answer, produces valid SQL that reads the wrong table's column
    ///     when a pattern matches something it should not; building it qualified from the start cannot.
    /// </remarks>
    public MemberFactory(StoreOptions options, DocumentMapping mapping, string? tableAlias = null)
        : this(options, mapping, true, tableAlias)
    {
    }

    /// <summary>
    ///     A factory with no document mapping behind it — for JSON that is not a document.
    /// </summary>
    /// <remarks>
    ///     <b>An event body is the case (fisher#41).</b> It is a JSON document in a column called
    ///     <c>data</c>, so every locator applies verbatim, but it has no mapping: no identity member
    ///     (most event types have none, and <c>DocumentMapping</c> refuses a type without one) and no
    ///     duplicated fields. Both of those are the only things the mapping is consulted for, so
    ///     leaving it out is the whole difference.
    ///     <para>
    ///         Not having an identity is load-bearing rather than incidental: <c>fi_events.id</c> is the
    ///         <em>event's</em> identity, so resolving a body member called <c>Id</c> to it would compare
    ///         against the wrong column and return rows rather than an error.
    ///     </para>
    /// </remarks>
    public MemberFactory(StoreOptions options) : this(options, null, false, null)
    {
    }

    private MemberFactory(StoreOptions options, DocumentMapping? mapping, bool hasIdentityColumn,
        string? tableAlias)
    {
        _mapping = mapping;
        _hasIdentityColumn = hasIdentityColumn;
        _qualifier = tableAlias is null ? "" : tableAlias + ".";
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
            return new IdMember(_mapping!.IdType, _qualifier);
        }

        return ResolveMember(ChainOf(expression));
    }

    /// <summary>
    ///     Resolve a member chain that arrived as reflection rather than as an expression — the shape a
    ///     duplicated field is registered in, and how the table definition asks for the expression its
    ///     generated column is derived from.
    /// </summary>
    /// <remarks>
    ///     Both entry points converge here so that a duplicated column's generated expression and the
    ///     locator it replaces are produced by the same code. If they were built separately, a change to
    ///     the JSON path or to a member type's wrapping would silently make the column hold something
    ///     the query no longer looks for — and the query would simply return nothing.
    /// </remarks>
    public IQueryableMember ResolveMember(MemberInfo[] chain)
    {
        var member = CreateMember(BuildJsonPath(chain), GetMemberType(chain[^1]));
        var duplicate = _mapping?.DuplicateFor(chain);

        return duplicate is null ? member : new DuplicatedMember(member, duplicate.ColumnName, _qualifier);
    }

    /// <summary>
    ///     The member access walked back to the parameter, outermost member last.
    /// </summary>
    private static MemberInfo[] ChainOf(MemberExpression expression)
    {
        var chain = new List<MemberInfo>();
        MemberExpression? current = expression;

        while (current != null)
        {
            chain.Insert(0, current.Member);

            if (current.Expression is ParameterExpression)
            {
                break;
            }

            current = current.Expression as MemberExpression;
        }

        return chain.ToArray();
    }

    private bool IsIdentityMember(MemberInfo member)
        => _hasIdentityColumn && member.Name == _mapping!.IdMember.Name;

    private IQueryableMember CreateMember(string jsonPath, Type memberType)
    {
        var locator = $"json_extract({_qualifier}data, '{jsonPath}')";
        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (underlying.IsEnum)
        {
            return new EnumMember(locator, underlying, _enumStorage, _namingPolicy);
        }

        if (underlying == typeof(bool))
        {
            return new QueryableMember(locator, memberType, isBoolean: true);
        }

        // A timestamp's stored text carries an un-normalised offset and a trimmed fractional part, so
        // it is compared through SQLite's date parser. DateOnly and TimeOnly have neither problem and
        // sort as written — see the two member types for why they are not one.
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            return new TimestampMember(locator, memberType);
        }

        if (underlying == typeof(DateOnly) || underlying == typeof(TimeOnly))
        {
            return new DateMember(locator, memberType, _serializerOptions);
        }

        return new QueryableMember(locator, memberType);
    }

    /// <summary>
    ///     A possibly-nested member chain rendered as <c>$.a.b.c</c>.
    /// </summary>
    private string BuildJsonPath(MemberInfo[] chain)
        => "$." + string.Join(".", chain.Select(GetJsonPropertyName));

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

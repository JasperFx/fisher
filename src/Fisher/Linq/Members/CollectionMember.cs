using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Fisher.Linq.Parsing;

namespace Fisher.Linq.Members;

/// <summary>
///     A document member that is a collection — a JSON array in storage — and knows how to unroll
///     itself through SQLite's <c>json_each</c> table-valued function.
/// </summary>
/// <remarks>
///     <para>
///         It is still a <see cref="QueryableMember" /> with the ordinary <c>json_extract</c> locator,
///         which is what <c>IsEmpty()</c> and a null test read, so nothing that resolved a collection
///         member before this type existed behaves differently. What is added is the correlated
///         sub-query surface: <see cref="JsonEachSource" /> and <see cref="Alias" /> are what
///         <c>Contains</c> / <c>Any</c> / <c>All</c> / <c>Count</c> compose their
///         <c>exists (select 1 from json_each(data, '$.tags') as each_1 where …)</c> from.
///     </para>
///     <para>
///         <b>The <c>key is not null</c> guard the sub-query fragments carry is load-bearing, not
///         defensive.</b> <c>json_each</c> over a member whose stored value is JSON <c>null</c> —
///         which is what System.Text.Json writes for a null collection property — returns <em>one</em>
///         row holding that null, where an absent key returns zero and an empty array returns zero.
///         Verified against SQLite 3.51. Without the guard, <c>Contains(null)</c> and any predicate
///         satisfied by NULL (<c>c =&gt; c.Port == null</c>) would match a document whose collection is
///         null — a false positive with nothing anywhere to signal it. The guard works because that
///         one row is the only <c>json_each</c> row whose <c>key</c> is NULL: an array element's key is
///         its index, even for a null element.
///     </para>
///     <para>
///         Aliases are numbered by nesting depth (<c>each_1</c>, <c>each_2</c>, …) so a collection
///         predicate inside an <c>Any</c> gets a fresh alias. Two sub-queries at the same depth never
///         collide, because each is its own <c>exists (…)</c> scope.
///     </para>
/// </remarks>
internal class CollectionMember : QueryableMember
{
    private readonly MemberFactory _parent;

    public CollectionMember(MemberFactory parent, string dataExpression, string jsonPath,
        Type memberType, Type elementType, int depth)
        : base($"json_extract({dataExpression}, '{jsonPath}')", memberType)
    {
        _parent = parent;
        ElementType = elementType;
        Depth = depth;
        Alias = $"each_{depth}";
        JsonEachSource = $"json_each({dataExpression}, '{jsonPath}')";
    }

    public Type ElementType { get; }

    /// <summary>The <c>json_each(…)</c> call a correlated sub-query selects from.</summary>
    public string JsonEachSource { get; }

    /// <summary>The alias the sub-query gives its <c>json_each</c> — <c>each_1</c> at the top level.</summary>
    public string Alias { get; }

    internal int Depth { get; }

    /// <summary>
    ///     Whether the elements are scalars a <c>Contains</c> can compare directly. A complex element
    ///     has no single stored form to compare against — <c>Any(c =&gt; …)</c> is the operator for it.
    /// </summary>
    public bool HasScalarElements => IsScalarType(ElementType);

    /// <summary>
    ///     The element as a queryable member over <c>each_N.value</c> — same typing rules as a
    ///     document member of the same CLR type, so an enum element honours the store's
    ///     <c>EnumStorage</c> and a Guid element compares as lowercase canonical text. Null for a
    ///     complex element type.
    /// </summary>
    public IQueryableMember? ElementMember
        => HasScalarElements ? _parent.CreateScalarMember($"{Alias}.value", ElementType) : null;

    /// <summary>
    ///     A resolver for the members of one element, scoped to the given lambda parameter — what an
    ///     <c>Any(c =&gt; …)</c> predicate's member accesses resolve through.
    /// </summary>
    public IMemberResolver CreateElementResolver(ParameterExpression parameter)
        => new ElementMemberResolver(
            _parent.CreateElementFactory($"{Alias}.value", Depth), parameter, ElementType);

    /// <summary>
    ///     Whether the CLR type is stored as a JSON array of elements worth unrolling, and which
    ///     element type. Deliberately narrower than "implements IEnumerable": a string is characters,
    ///     a byte array serializes as a base64 string, and a dictionary serializes as a JSON object.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "Reflects over a document member's interfaces to classify it as a collection. Document types and their property graph are preserved at the LINQ registration boundary on the caller side, per the AOT publishing guide.")]
    public static bool TryGetElementType(Type type, out Type elementType)
    {
        elementType = typeof(object);

        if (type == typeof(string) || type == typeof(byte[]))
        {
            return false;
        }

        if (typeof(IDictionary).IsAssignableFrom(type) || ImplementsDictionaryInterface(type))
        {
            return false;
        }

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerable == null)
        {
            return false;
        }

        elementType = enumerable.GetGenericArguments()[0];
        return true;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
        Justification = "Same classification reflection as TryGetElementType; see its justification.")]
    private static bool ImplementsDictionaryInterface(Type type)
        => type.GetInterfaces().Any(i => i.IsGenericType
                                         && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                                             || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    /// <summary>
    ///     Whether elements of this type are stored as single JSON scalars rather than objects.
    /// </summary>
    public static bool IsScalarType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
               || underlying.IsEnum
               || underlying == typeof(string)
               || underlying == typeof(decimal)
               || underlying == typeof(Guid)
               || underlying == typeof(DateTime)
               || underlying == typeof(DateTimeOffset)
               || underlying == typeof(DateOnly)
               || underlying == typeof(TimeOnly)
               || underlying == typeof(TimeSpan);
    }
}

/// <summary>
///     Resolves member accesses inside an <c>Any</c>/<c>All</c>/<c>Count</c> predicate against one
///     collection element, refusing anything that is not the element's own member.
/// </summary>
/// <remarks>
///     Both refusals exist because the alternative is wrong SQL rather than an error. A chain rooted
///     at the <em>outer</em> document's parameter would otherwise resolve against the element and read
///     the wrong JSON; a member access on a <em>scalar</em> element (<c>t.Length</c>) would build a
///     JSON path (<c>$.length</c>) into a value that is not an object and match nothing.
/// </remarks>
internal sealed class ElementMemberResolver : IMemberResolver
{
    private readonly MemberFactory _inner;
    private readonly ParameterExpression _parameter;
    private readonly Type _elementType;

    public ElementMemberResolver(MemberFactory inner, ParameterExpression parameter, Type elementType)
    {
        _inner = inner;
        _parameter = parameter;
        _elementType = elementType;
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        var root = RootParameterOf(expression);
        if (!ReferenceEquals(root, _parameter))
        {
            throw new BadLinqExpressionException(
                $"The predicate member '{expression}' does not belong to the collection element "
                + $"'{_parameter.Name}'. Inside Any/All/Count over a child collection, only the "
                + "element's own members can be translated to SQL.");
        }

        if (CollectionMember.IsScalarType(_elementType))
        {
            throw new BadLinqExpressionException(
                $"Cannot translate the member access '{expression}' inside a collection predicate: "
                + $"the elements are stored as plain {_elementType.Name} values with no members to "
                + "extract. Use Contains(value) for an equality test against the elements.");
        }

        return _inner.ResolveMember(expression);
    }

    private static ParameterExpression? RootParameterOf(MemberExpression expression)
    {
        Expression? current = expression;
        while (current is MemberExpression member)
        {
            current = member.Expression;
        }

        return current as ParameterExpression;
    }
}

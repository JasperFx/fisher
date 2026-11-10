using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JasperFx.Events;
using Weasel.Storage;

namespace Fisher.Linq.Members;

/// <summary>
///     Recognises the expression-tree shapes a query over a C# 15 union member takes (fisher#466).
/// </summary>
/// <remarks>
///     <para>
///         C# refuses a union pattern inside an expression tree (<c>x.Payment is Card</c> is CS8122), so
///         the shapes a LINQ query can actually contain all go through the union's <c>Value</c>:
///     </para>
///     <list type="bullet">
///         <item><c>x.Payment.Value is Card</c>: a <see cref="TypeBinaryExpression" /> over <c>Value</c>, the type test.</item>
///         <item><c>((Card)x.Payment.Value).Last4</c>: a member read through a <c>Convert</c> of <c>Value</c>, case access.</item>
///         <item><c>(int)x.Payment.Value</c>: a primitive case read through the same <c>Convert</c>.</item>
///         <item><c>x.Payment.Value == null</c>: the default union, which System.Text.Json writes as <c>null</c>.</item>
///     </list>
///     <para>
///         Before .NET 11 no type is a union (<see cref="UnionTypes.IsUnion" /> is false), so none of
///         these shapes ever matches and nothing here needs an <c>#if</c>.
///     </para>
/// </remarks>
internal static class UnionMemberAccess
{
    /// <summary>Whether <paramref name="type" />, or the type a <see cref="Nullable{T}" /> wraps, is a union.</summary>
    internal static bool IsUnionTyped(Type type) => (Nullable.GetUnderlyingType(type) ?? type).IsUnion();

    /// <summary>
    ///     <c>x.Payment.Value</c>: the <c>Value</c> property of a union-typed member, with the union
    ///     member it reads.
    /// </summary>
    internal static bool TryMatchValueAccess(Expression expression,
        [NotNullWhen(true)] out MemberExpression? unionMember)
    {
        unionMember = null;

        if (expression is MemberExpression { Member.Name: "Value", Expression: MemberExpression owner }
            && owner.Type.IsUnion())
        {
            unionMember = owner;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     <c>(Card)x.Payment.Value</c>: a conversion of a union's <c>Value</c> to one of its cases.
    /// </summary>
    internal static bool TryMatchCaseConversion(Expression expression,
        [NotNullWhen(true)] out MemberExpression? unionMember, [NotNullWhen(true)] out Type? caseType)
    {
        unionMember = null;
        caseType = null;

        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            && TryMatchValueAccess(convert.Operand, out unionMember))
        {
            caseType = convert.Type;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     The expression a member access is read from, stepping through a case conversion to the union
    ///     member behind it, so a "is this rooted at the document" walk reaches the parameter.
    /// </summary>
    internal static Expression? ParentOf(MemberExpression member)
        => TryMatchCaseConversion(member.Expression!, out var unionMember, out _)
            ? unionMember
            : member.Expression;

    internal static string RefuseBareValue(MemberExpression unionMember)
        => $"Cannot translate '{unionMember}.Value' on its own: '{unionMember.Member.Name}' is a union "
           + $"({unionMember.Type.Name}), stored as its bare active case with no discriminator, so its "
           + "Value has no single SQL type. Test the case with "
           + $"'{unionMember}.Value is <Case>', read a case with '((<Case>){unionMember}.Value).Member' "
           + $"or '(<Case>){unionMember}.Value', or test for the default union with '{unionMember}.Value == null'.";

    internal static string RefuseWholeUnion(string memberName, Type unionType)
        => $"Cannot translate the union member '{memberName}' ({unionType.Name}) as a value: it is stored as "
           + "its bare active case, which has no single SQL type to compare, order, index or select. "
           + $"Test the case with '.{memberName}.Value is <Case>' or read a member of one case with "
           + $"'((<Case>)x.{memberName}.Value).Member'. Loading the whole document reads the union as usual.";
}

/// <summary>
///     Builds the <see cref="UnionCaseTest" /> that recognises one case of a union in its stored JSON,
///     from the store's own serializer metadata (weasel#744 leaves this half to the stores).
/// </summary>
/// <remarks>
///     <para>
///         System.Text.Json writes the bare active case, so the stored value's JSON kind is the first thing
///         that tells cases apart and, for object cases, the properties are the rest. Two decisions:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A property is only required when it is always written.</b> Fisher's serializer defaults to
///             <c>WhenWritingNull</c>, so a nullable property may be missing from a row of the very case it
///             belongs to. Requiring it would silently drop those rows. Absence has no such problem: a
///             property a case does not have is never in its JSON, so absence is used for everything the
///             other cases have and this one does not.
///         </item>
///         <item>
///             <b>Two cases that cannot be told apart are refused by name</b>, rather than answered with a
///             test that matches both. Two primitive cases of one JSON kind (<c>int</c> and <c>long</c>,
///             <c>string</c> and <c>Guid</c>) and two object cases neither of which always writes a
///             property the other lacks are the shapes.
///         </item>
///     </list>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Reads the JsonTypeInfo of a union's case types through the store's own options, which under trimming or Native AOT carry the application's source-generated context, as every document read already requires.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Reads the JsonTypeInfo of a union's case types through the store's own options, which under trimming or Native AOT carry the application's source-generated context, as every document read already requires.")]
internal static class UnionCaseTests
{
    private sealed record CaseShape(Type CaseType, UnionCaseJsonKind Kind, HashSet<string> Properties,
        HashSet<string> AlwaysWritten);

    /// <summary>The test for <paramref name="caseType" /> within <paramref name="unionType" />.</summary>
    internal static UnionCaseTest For(Type unionType, Type caseType, JsonSerializerOptions options,
        bool enumsAsStrings)
    {
        var cases = unionType.CaseTypes();

        if (!cases.Contains(caseType))
        {
            throw new BadLinqExpressionException(
                $"'{caseType.Name}' is not a case of the union {unionType.Name}, whose cases are "
                + $"{string.Join(", ", cases.Select(x => x.Name))}. A type test or a cast against a union's "
                + "Value has to name one of its cases exactly; a base type or an interface cannot be "
                + "recognised in the stored JSON.");
        }

        if (caseType.IsUnion())
        {
            throw new BadLinqExpressionException(
                $"'{caseType.Name}' is itself a union, nested inside {unionType.Name}. Querying a nested union "
                + "is not supported; test the inner union's cases from a document member of that union type.");
        }

        var shapes = cases.Select(x => ShapeOf(x, options, enumsAsStrings)).ToList();
        var target = shapes.Single(x => x.CaseType == caseType);
        var others = shapes.Where(x => x.CaseType != caseType && x.Kind == target.Kind).ToList();

        if (target.Kind != UnionCaseJsonKind.Object)
        {
            if (others.Count > 0)
            {
                throw Ambiguous(unionType, target, others[0],
                    $"both are stored as a JSON {target.Kind.ToString().ToLowerInvariant()}");
            }

            return UnionCaseTest.OfKind(target.Kind);
        }

        foreach (var other in others)
        {
            var targetOnly = target.AlwaysWritten.Except(other.Properties);
            var otherOnly = other.AlwaysWritten.Except(target.Properties);

            if (!targetOnly.Any() && !otherOnly.Any())
            {
                throw Ambiguous(unionType, target, other,
                    "both are JSON objects and neither always writes a property the other lacks");
            }
        }

        var otherProperties = others.SelectMany(x => x.Properties).ToHashSet();
        var required = target.AlwaysWritten.Except(otherProperties).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var absent = otherProperties.Except(target.Properties).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        return UnionCaseTest.ObjectWith(required, absent);
    }

    private static BadLinqExpressionException Ambiguous(Type unionType, CaseShape target, CaseShape other,
        string reason)
        => new($"Cannot tell the '{target.CaseType.Name}' case of the union {unionType.Name} from its "
               + $"'{other.CaseType.Name}' case in the stored JSON: {reason}. System.Text.Json writes a union as "
               + "its bare active case with no discriminator, so a SQL test for one would also match the other. "
               + "Give the cases distinguishable shapes, or load the documents and test the case in memory.");

    private static CaseShape ShapeOf(Type caseType, JsonSerializerOptions options, bool enumsAsStrings)
    {
        var target = Nullable.GetUnderlyingType(caseType) ?? caseType;

        var primitive = PrimitiveKind(target, enumsAsStrings);
        if (primitive is { } kind)
        {
            return new CaseShape(caseType, kind, [], []);
        }

        if (!options.IsReadOnly)
        {
            options.MakeReadOnly(populateMissingResolver: true);
        }

        var info = options.GetTypeInfo(target);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return new CaseShape(caseType, UnionCaseJsonKind.Array, [], []);
            case JsonTypeInfoKind.Dictionary:
                return new CaseShape(caseType, UnionCaseJsonKind.Object, [], []);
            case JsonTypeInfoKind.Object:
            {
                var properties = new HashSet<string>(StringComparer.Ordinal);
                var always = new HashSet<string>(StringComparer.Ordinal);

                foreach (var property in info.Properties.Where(x => x.Get != null))
                {
                    properties.Add(property.Name);
                    if (IsAlwaysWritten(property, options))
                    {
                        always.Add(property.Name);
                    }
                }

                return new CaseShape(caseType, UnionCaseJsonKind.Object, properties, always);
            }
            default:
                // A type with a custom converter: what it writes is the converter's business. A string is
                // the overwhelmingly common answer (strong-typed ids, Uri-like values), and claiming it
                // means a second string case is refused as ambiguous rather than silently mis-tested.
                return new CaseShape(caseType, UnionCaseJsonKind.String, [], []);
        }
    }

    private static UnionCaseJsonKind? PrimitiveKind(Type type, bool enumsAsStrings)
    {
        if (type == typeof(bool)) return UnionCaseJsonKind.Boolean;

        if (type.IsEnum)
        {
            return enumsAsStrings ? UnionCaseJsonKind.String : UnionCaseJsonKind.Number;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)
            || type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return UnionCaseJsonKind.Number;
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan) || type == typeof(Uri))
        {
            return UnionCaseJsonKind.String;
        }

        return null;
    }

    /// <summary>
    ///     Whether a property is in the JSON of every instance: no per-property ignore condition, and either
    ///     nothing is ever omitted or the property is a non-nullable value type that only a
    ///     <c>WhenWritingDefault</c> policy would omit.
    /// </summary>
    private static bool IsAlwaysWritten(JsonPropertyInfo property, JsonSerializerOptions options)
    {
        if (property.ShouldSerialize != null) return false;

        return options.DefaultIgnoreCondition switch
        {
            JsonIgnoreCondition.Never => true,
            JsonIgnoreCondition.WhenWritingNull => property.PropertyType.IsValueType
                                                   && Nullable.GetUnderlyingType(property.PropertyType) == null,
            _ => false
        };
    }
}

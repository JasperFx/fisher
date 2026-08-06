using System.Reflection;

namespace Fisher.Storage.Metadata;

/// <summary>
///     One metadata column, and the document member (if any) its stored value is projected onto when a
///     document is read.
/// </summary>
/// <remarks>
///     <para>
///         Fisher writes five metadata columns and four of them can be mapped. <c>dotnet_type</c> is
///         the exception: <c>Weasel.Storage.DocumentDotNetTypeBinder</c> takes no member, so there is
///         nothing to map it onto short of an upstream change, and it is absent from
///         <see cref="DocumentMetadata" /> rather than present and quietly inert.
///     </para>
///     <para>
///         A mapping is refused at configuration time when the member cannot carry the column's value.
///         The alternative is <c>LambdaBuilder.Setter</c> throwing when the document's storage is first
///         built, which happens on first use — a long way from the line that caused it, and in a
///         message about expression trees rather than about metadata.
///     </para>
/// </remarks>
public class MetadataColumn
{
    internal MetadataColumn(string name, Type memberType)
    {
        Name = name;
        MemberType = memberType;
    }

    /// <summary>The column's name on the document table.</summary>
    public string Name { get; }

    /// <summary>The CLR type a member has to have to carry this column's value.</summary>
    internal Type MemberType { get; }

    /// <summary>
    ///     The document member the column is projected onto, or null when the column is written but
    ///     never read back.
    /// </summary>
    public MemberInfo? Member { get; private set; }

    /// <summary>
    ///     Project this column onto a document member.
    /// </summary>
    public void MapTo(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);

        AssertCanCarry(member);
        Member = member;
    }

    /// <summary>
    ///     Map only if nothing has claimed the column yet, so an explicit registration is not undone by
    ///     a convention.
    /// </summary>
    /// <remarks>
    ///     Used for the interface conventions only. An attribute maps with <see cref="MapTo" /> and so
    ///     wins over the interface that would otherwise have claimed the column: implementing
    ///     <c>ISoftDeleted</c> says a member exists, whereas marking a member says which one, and the
    ///     more specific statement should be the one that holds. The fluent DSL runs later still and
    ///     overrides both.
    /// </remarks>
    internal void MapToIfUnset(MemberInfo member)
    {
        if (Member is null)
        {
            MapTo(member);
        }
    }

    private void AssertCanCarry(MemberInfo member)
    {
        var (type, settable) = member switch
        {
            PropertyInfo property => (property.PropertyType, property.GetSetMethod(nonPublic: true) is not null),
            FieldInfo field => (field.FieldType, !field.IsInitOnly),
            _ => throw new ArgumentException(
                $"'{member.Name}' is not a property or field, so metadata column '{Name}' cannot be mapped onto it.",
                nameof(member))
        };

        if (type != MemberType)
        {
            throw new ArgumentException(
                $"Metadata column '{Name}' holds a {Describe(MemberType)} and cannot be mapped onto "
                + $"'{member.DeclaringType?.Name}.{member.Name}', which is a {Describe(type)}.",
                nameof(member));
        }

        if (!settable)
        {
            throw new ArgumentException(
                $"Metadata column '{Name}' is projected onto '{member.DeclaringType?.Name}.{member.Name}' "
                + "when a document is read, so that member needs a setter.",
                nameof(member));
        }
    }

    private static string Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        return underlying is null ? type.Name : $"{underlying.Name}?";
    }
}

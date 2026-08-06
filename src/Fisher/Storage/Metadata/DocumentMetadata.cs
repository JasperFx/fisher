using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Fisher.Attributes;
using JasperFx.Metadata;

namespace Fisher.Storage.Metadata;

/// <summary>
///     Which of a document type's own members Fisher's metadata columns are projected onto when a
///     document is read.
/// </summary>
/// <remarks>
///     <para>
///         Every column here is written whether or not it is mapped — mapping only decides whether the
///         value comes back out. An unmapped column is simply absent from the SELECT, which is why
///         adding a mapping widens the read projection and why the binder arrays and the select field
///         list in <see cref="SqliteDocumentStorageDescriptorBuilder" /> are both derived from the same
///         ordered array rather than maintained side by side.
///     </para>
///     <para>
///         Three ways to say it, each overriding the one before: the JasperFx metadata interfaces,
///         then the <c>Fisher.Attributes</c> metadata attributes, then
///         <c>Schema.For&lt;T&gt;().Metadata(...)</c>. The first two are conventions applied when the
///         mapping is created; the third runs afterwards, as configuration should.
///     </para>
/// </remarks>
public class DocumentMetadata
{
    /// <summary>
    ///     <c>guid_version</c> — the optimistic concurrency version. Mapped by
    ///     <see cref="IVersioned" />, which also turns optimistic concurrency on: without it the column
    ///     is neither written nor read, and a mapping onto it would mean nothing.
    /// </summary>
    public MetadataColumn Version { get; } = new("guid_version", typeof(Guid));

    /// <summary>
    ///     <c>last_modified</c> — when the row was last written, generated server-side. No JasperFx
    ///     interface declares it, so <see cref="LastModifiedMetadataAttribute" /> and the fluent DSL are
    ///     the only two ways to reach it.
    /// </summary>
    public MetadataColumn LastModified { get; } = new("last_modified", typeof(DateTimeOffset));

    /// <summary>
    ///     <c>is_deleted</c> — the soft-delete flag. Mapped by <see cref="ISoftDeleted.Deleted" />.
    /// </summary>
    public MetadataColumn IsSoftDeleted { get; } = new(SoftDelete.IsDeletedColumn, typeof(bool));

    /// <summary>
    ///     <c>deleted_at</c> — when the row was soft deleted, null while it is live. Mapped by
    ///     <see cref="ISoftDeleted.DeletedAt" />.
    /// </summary>
    public MetadataColumn DeletedAt { get; } = new(SoftDelete.DeletedAtColumn, typeof(DateTimeOffset?));

    /// <summary>
    ///     Every mappable column, in a stable order. Not the read order — that is decided by the binder
    ///     array, which only holds the columns a type actually has.
    /// </summary>
    public IEnumerable<MetadataColumn> AllColumns()
    {
        yield return Version;
        yield return LastModified;
        yield return IsSoftDeleted;
        yield return DeletedAt;
    }

    /// <summary>
    ///     Apply the interface and attribute conventions for a document type.
    /// </summary>
    /// <returns>
    ///     True when the type implements <see cref="IVersioned" />, which is also a request for
    ///     optimistic concurrency — reported back rather than acted on here, because the mapping owns
    ///     that flag.
    /// </returns>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification =
            "Reflects over the document type's own members to resolve metadata mappings. Document types are preserved at the registration boundary (Schema.For<T>() / Store<T>()) on the caller side.")]
    internal bool ApplyConventions(Type documentType)
    {
        var versioned = typeof(IVersioned).IsAssignableFrom(documentType);

        // The interfaces first, and by interface map rather than by name: an explicit implementation
        // is not a public member called "Deleted", and a document is free to have a member of its own
        // by that name meaning something else entirely.
        if (typeof(ISoftDeleted).IsAssignableFrom(documentType))
        {
            MapInterfaceProperty(documentType, typeof(ISoftDeleted), nameof(ISoftDeleted.Deleted), IsSoftDeleted);
            MapInterfaceProperty(documentType, typeof(ISoftDeleted), nameof(ISoftDeleted.DeletedAt), DeletedAt);
        }

        if (versioned)
        {
            MapInterfaceProperty(documentType, typeof(IVersioned), nameof(IVersioned.Version), Version);
        }

        foreach (var member in documentType.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            if (member is not (PropertyInfo or FieldInfo)) continue;

            foreach (var attribute in member.GetCustomAttributes<MetadataAttribute>())
            {
                attribute.Apply(this, member);
            }
        }

        return versioned;
    }

    /// <summary>
    ///     Resolve the member a document type uses to implement one interface property, following the
    ///     interface map so an explicit implementation is found too.
    /// </summary>
    /// <remarks>
    ///     An explicitly implemented property is private and its accessors are named
    ///     <c>Namespace.IInterface.set_Member</c>, so neither <c>GetProperty(name)</c> nor a scan of
    ///     public members finds it. The interface map is the only thing that does. A private setter is
    ///     fine here — <c>LambdaBuilder.Setter</c> emits a call to the accessor it is given.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "See ApplyConventions.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "See ApplyConventions.")]
    private static void MapInterfaceProperty(Type documentType, Type interfaceType, string propertyName,
        MetadataColumn column)
    {
        var declared = interfaceType.GetProperty(propertyName)!;
        var map = documentType.GetInterfaceMap(interfaceType);

        var setter = declared.GetSetMethod()!;

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i] != setter) continue;

            var target = map.TargetMethods[i];

            var implementation = documentType
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(x => x.GetSetMethod(nonPublic: true) == target);

            if (implementation is not null)
            {
                column.MapToIfUnset(implementation);
            }

            return;
        }
    }
}

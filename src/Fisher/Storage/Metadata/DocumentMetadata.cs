using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Fisher.Attributes;
using JasperFx;
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
    ///     <c>revision</c> — the numeric concurrency revision, and the alternative to
    ///     <see cref="Version" /> rather than a companion to it. Mapped by
    ///     <see cref="JasperFx.IRevisioned" />, which also turns numeric revisions on.
    /// </summary>
    /// <remarks>
    ///     A separate column from <see cref="Version" /> because the two carry different CLR types —
    ///     a Guid and an int — and <see cref="MetadataColumn" /> refuses a member that cannot hold its
    ///     value. Sharing one slot would mean either dropping that check or making it lie.
    /// </remarks>
    public MetadataColumn Revision { get; } = new(NumericRevision.Column, typeof(int));

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
    ///     <c>created_at</c> — when the row first appeared, and the one column here that an ordinary
    ///     write must never touch (fisher#29).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>It is filled by the column's own DEFAULT and read back, never written.</b> That is
    ///         what keeps it from being clobbered on every save: Fisher's upsert assigns every column
    ///         in its write list from <c>excluded.*</c>, and a column no write binder contributes is
    ///         not in that list at all. So the rule the rest of the descriptor follows needs no
    ///         exception — which is worth saying, because the obvious implementation is to add a write
    ///         binder and then carve <c>created_at</c> out of the <c>do update set</c> clause.
    ///     </para>
    ///     <para>
    ///         <c>last_modified</c> answers "when was this last written"; without this there was no way
    ///         to ask when it first appeared.
    ///     </para>
    /// </remarks>
    public MetadataColumn CreatedAt { get; } = new("created_at", typeof(DateTimeOffset), optional: true);

    /// <summary>
    ///     <c>correlation_id</c> — the session's correlation id, copied onto every row it writes.
    /// </summary>
    /// <remarks>
    ///     The document-side counterpart of what <c>AppendPlanner.ApplySessionMetadata</c> already does
    ///     for events, and from the same source: <c>FisherSession</c> seeds correlation and causation
    ///     from <c>Activity.Current</c> at construction, so a document and an event written in one unit
    ///     of work carry identical values with no application code. There is deliberately no second
    ///     source for it.
    /// </remarks>
    public MetadataColumn CorrelationId { get; } = new("correlation_id", typeof(string), optional: true);

    /// <inheritdoc cref="CorrelationId" />
    public MetadataColumn CausationId { get; } = new("causation_id", typeof(string), optional: true);

    /// <summary>
    ///     <c>last_modified_by</c> — the session's <c>CurrentUserName</c> at the time of the write.
    /// </summary>
    public MetadataColumn LastModifiedBy { get; } = new("last_modified_by", typeof(string), optional: true);

    /// <summary>
    ///     <c>headers</c> — the session's header dictionary, as JSON TEXT.
    /// </summary>
    public MetadataColumn Headers { get; }
        = new("headers", typeof(Dictionary<string, object>), optional: true);

    /// <summary>
    ///     <c>tenant_id</c> — read back onto a member, on a conjoined table.
    /// </summary>
    /// <remarks>
    ///     Read-only, like <see cref="CreatedAt" /> but for a different reason: the column is part of
    ///     the primary key and the storage operations bind it inline ahead of the binder loop, so a
    ///     write binder would be a second writer of a value that already has one. Enabling it does not
    ///     create the column either — <c>MultiTenanted()</c> does that — so this one only ever decides
    ///     whether the value is projected back onto a member.
    /// </remarks>
    public MetadataColumn TenantId { get; }
        = new(StorageConstants.TenantIdColumn, typeof(string), optional: true);

    /// <summary>
    ///     Every mappable column, in a stable order. Not the read order — that is decided by the binder
    ///     array, which only holds the columns a type actually has.
    /// </summary>
    public IEnumerable<MetadataColumn> AllColumns()
    {
        yield return Version;
        yield return Revision;
        yield return LastModified;
        yield return IsSoftDeleted;
        yield return DeletedAt;
        yield return CreatedAt;
        yield return CorrelationId;
        yield return CausationId;
        yield return LastModifiedBy;
        yield return Headers;
        yield return TenantId;
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

        // IRevisioned is the numeric alternative, and it shares the Version slot: a document type is
        // versioned one way or the other, never both, so one MetadataColumn serves whichever column
        // the mapping ends up with. DocumentMapping is what refuses the pair.
        if (typeof(JasperFx.IRevisioned).IsAssignableFrom(documentType))
        {
            MapInterfaceProperty(documentType, typeof(JasperFx.IRevisioned),
                nameof(JasperFx.IRevisioned.Version), Revision);
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

using System.Reflection;
using Fisher.Storage.Metadata;

namespace Fisher.Attributes;

/// <summary>
///     Base for the attributes that project a stored metadata column onto the member they mark.
/// </summary>
/// <remarks>
///     Mirrors Polecat's <c>MetadataAttribute</c> and Marten's metadata attributes, narrowed to the
///     columns a Fisher document table actually has. <see cref="Apply" /> is internal, so the set is
///     closed — a column Fisher does not write has no attribute that could claim to map it.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public abstract class MetadataAttribute : Attribute
{
    internal abstract void Apply(DocumentMetadata metadata, MemberInfo member);
}

/// <summary>
///     Projects <c>guid_version</c> onto this member. The member must be a settable <see cref="Guid" />.
/// </summary>
/// <remarks>
///     Does not by itself turn optimistic concurrency on — implementing
///     <see cref="JasperFx.Metadata.IVersioned" /> or calling <c>UseOptimisticConcurrency()</c> does
///     that, and without it there is no version to read.
/// </remarks>
public sealed class VersionMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.Version.MapTo(member);
}

/// <summary>
///     Projects <c>last_modified</c> onto this member. The member must be a settable
///     <see cref="DateTimeOffset" />.
/// </summary>
public sealed class LastModifiedMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.LastModified.MapTo(member);
}

/// <summary>
///     Projects <c>is_deleted</c> onto this member. The member must be a settable <see cref="bool" />.
/// </summary>
public sealed class IsSoftDeletedMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.IsSoftDeleted.MapTo(member);
}

/// <summary>
///     Projects <c>deleted_at</c> onto this member. The member must be a settable
///     <c>DateTimeOffset?</c> — it is null for as long as the document is live.
/// </summary>
public sealed class DeletedAtMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.DeletedAt.MapTo(member);
}

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

/// <summary>
///     Projects <c>created_at</c> onto this member, and creates the column (fisher#29). The member must
///     be a settable <see cref="DateTimeOffset" />.
/// </summary>
/// <remarks>
///     Each of the five attributes below marks an <em>optional</em> column, so marking a member both
///     maps the column and turns it on — see <see cref="MetadataColumn.MapTo" /> for why a mapping onto
///     a column that would not exist is not a thing worth allowing.
/// </remarks>
public sealed class CreatedAtMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.CreatedAt.MapTo(member);
}

/// <inheritdoc cref="CreatedAtMetadataAttribute" />
/// <summary>Projects <c>correlation_id</c> onto this member. The member must be a settable string.</summary>
public sealed class CorrelationIdMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member)
        => metadata.CorrelationId.MapTo(member);
}

/// <inheritdoc cref="CreatedAtMetadataAttribute" />
/// <summary>Projects <c>causation_id</c> onto this member. The member must be a settable string.</summary>
public sealed class CausationIdMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member)
        => metadata.CausationId.MapTo(member);
}

/// <inheritdoc cref="CreatedAtMetadataAttribute" />
/// <summary>Projects <c>last_modified_by</c> onto this member. The member must be a settable string.</summary>
public sealed class LastModifiedByMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member)
        => metadata.LastModifiedBy.MapTo(member);
}

/// <inheritdoc cref="CreatedAtMetadataAttribute" />
/// <summary>
///     Projects <c>headers</c> onto this member. The member must be a settable
///     <c>Dictionary&lt;string, object&gt;</c>.
/// </summary>
public sealed class HeadersMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.Headers.MapTo(member);
}

/// <summary>
///     Projects <c>tenant_id</c> onto this member. The member must be a settable string.
/// </summary>
/// <remarks>
///     Unlike the five above this creates nothing — <c>MultiTenanted()</c> is what puts the column on
///     the table, and on a single-tenant type there is no column to read, so the mapping is inert.
/// </remarks>
public sealed class TenantIdMetadataAttribute : MetadataAttribute
{
    internal override void Apply(DocumentMetadata metadata, MemberInfo member) => metadata.TenantId.MapTo(member);
}

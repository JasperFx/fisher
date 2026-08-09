namespace Fisher.Metadata;

/// <summary>
///     A read-only snapshot of what a document's metadata columns hold, returned by
///     <see cref="IQuerySession.MetadataForAsync{T}(T,CancellationToken)" /> (fisher#29).
/// </summary>
/// <remarks>
///     <para>
///         The point of it is to answer "who last touched this document, and under what correlation
///         context" without the value having to be projected onto a member of the document first.
///         Mapping a column is for when the document wants to carry the value; this is for when a
///         caller wants to ask.
///     </para>
///     <para>
///         <b>Named <c>StoredDocumentMetadata</c> rather than <c>DocumentMetadata</c>, which Polecat
///         and Marten both call theirs.</b> Fisher already has a <c>DocumentMetadata</c> — the
///         configuration object that says which columns are mapped onto which members — and two types
///         one namespace apart with the same name and opposite jobs is the kind of collision that only
///         ever gets noticed by whoever imports the wrong one.
///     </para>
///     <para>
///         <b>Every optional column is nullable here, where Polecat's constructor requires
///         <c>CreatedAt</c>.</b> On Fisher these columns exist only once a type has asked for them, so
///         null means "the column is not on this table" rather than "the row has no value" — and a
///         default <c>DateTimeOffset</c> would be indistinguishable from a real one written in year 1.
///     </para>
/// </remarks>
public sealed class StoredDocumentMetadata
{
    internal StoredDocumentMetadata(object id, string tenantId, DateTimeOffset lastModified)
    {
        Id = id;
        TenantId = tenantId;
        LastModified = lastModified;
    }

    /// <summary>The document's identity, as stored.</summary>
    public object Id { get; }

    /// <summary>
    ///     The owning tenant.
    /// </summary>
    /// <remarks>
    ///     Read from the row on a conjoined table and taken from the session otherwise, because a
    ///     single-tenant table has no column to read and the session's tenant is the honest answer
    ///     rather than a null.
    /// </remarks>
    public string TenantId { get; }

    /// <summary>When the row was last written. Always present — every document table has the column.</summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>When the row first appeared, or null when the type has no <c>created_at</c> column.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The Guid optimistic-concurrency version, when the type uses optimistic concurrency.</summary>
    public Guid? Version { get; init; }

    /// <summary>The numeric revision, when the type uses numeric revisions.</summary>
    public int? Revision { get; init; }

    /// <summary>Whether the row is soft-deleted. False for a type that does not soft delete.</summary>
    /// <remarks>
    ///     <b>A soft-deleted document has metadata and this is how it is reached.</b> The read carries
    ///     the tenant and hierarchy filters but deliberately not the soft-delete one — asking when and
    ///     whether something was deleted is one of the questions this method exists for, and an
    ///     ordinary load cannot answer it.
    /// </remarks>
    public bool Deleted { get; init; }

    /// <inheritdoc cref="Deleted" />
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>The assembly-qualified .NET type the row was written as.</summary>
    public string? DotNetType { get; init; }

    /// <summary>The hierarchy discriminator, when the type is a hierarchy.</summary>
    public string? DocumentType { get; init; }

    /// <summary>The correlation id of the session that last wrote the row, when the column is enabled.</summary>
    public string? CorrelationId { get; init; }

    /// <inheritdoc cref="CorrelationId" />
    public string? CausationId { get; init; }

    /// <summary>The <c>CurrentUserName</c> of the session that last wrote the row, when enabled.</summary>
    public string? LastModifiedBy { get; init; }

    /// <summary>The headers of the session that last wrote the row, when the column is enabled.</summary>
    public Dictionary<string, object>? Headers { get; init; }
}

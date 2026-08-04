using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Sqlite;
using Weasel.Storage;

namespace Fisher.Storage;

/// <summary>
///     How one document type is stored: its identity, its table, and the metadata columns that table
///     carries.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately a subset of Marten's and Polecat's <c>DocumentMapping</c>. There is no
///         hierarchy or sub-classing, no duplicated fields or user indexes, no foreign keys, no soft
///         delete, and no numeric revisions — every one of those is a column-shape or SQL-generation
///         concern that can be added later without disturbing what is here.
///     </para>
///     <para>
///         Identity resolution goes through <see cref="AggregateIdentity" />, the same helper live
///         aggregation uses. That is deliberate: an aggregate snapshotted into a document table must
///         agree with its live-aggregation counterpart about which member is the id and what type it
///         is, and the only way to guarantee that is to ask once.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
    Justification =
        "Class-level: reflects over the document type's members to resolve identity. Document types are preserved at the registration boundary (Schema.For<T>() / Store<T>()) on the caller side.")]
public class DocumentMapping
{
    /// <summary>
    ///     The identity types Fisher can store a document by.
    /// </summary>
    /// <remarks>
    ///     The canonical four from <c>JasperFx.DocumentIdentity</c>. Strongly typed id wrappers are not
    ///     supported yet — they need an identity strategy that unwraps them, and Fisher has no
    ///     strong-typed-id support anywhere else either.
    /// </remarks>
    public static readonly Type[] SupportedIdTypes = [typeof(Guid), typeof(string), typeof(int), typeof(long)];

    internal DocumentMapping(Type documentType, StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        DocumentType = documentType;
        StoreOptions = options;

        IdMember = AggregateIdentity.FindIdMember(documentType) ?? throw DescribeMissingIdentity(documentType);

        IdType = TypeOf(IdMember);

        Alias = DefaultAliasFor(documentType);
    }

    /// <summary>The .NET type being stored.</summary>
    public Type DocumentType { get; }

    /// <summary>The member carrying the document's identity.</summary>
    public MemberInfo IdMember { get; }

    /// <summary>The identity member's type, with any <c>Nullable&lt;T&gt;</c> unwrapped.</summary>
    public Type IdType { get; }

    internal StoreOptions StoreOptions { get; }

    /// <summary>
    ///     The short name used to build the table name. Defaults to the type name lowercased, with a
    ///     generic type's arity folded in so <c>Wrapper&lt;A&gt;</c> and <c>Wrapper&lt;B&gt;</c> do not
    ///     collide.
    /// </summary>
    public string Alias { get; set; }

    /// <summary>
    ///     Whether writes to this document type carry an optimistic concurrency check against the
    ///     <c>guid_version</c> column.
    /// </summary>
    public bool UseOptimisticConcurrency { get; set; }

    /// <summary>
    ///     Whether each row carries a tenant id. Follows the store's event tenancy style by default,
    ///     which keeps a snapshot in the same tenancy shape as the stream it came from.
    /// </summary>
    public TenancyStyle TenancyStyle { get; set; } = TenancyStyle.Single;

    internal bool IsConjoined => TenancyStyle == TenancyStyle.Conjoined;

    /// <summary>
    ///     The concurrency mode the closed-shape storage runtime should use for this document.
    /// </summary>
    internal ConcurrencyMode ConcurrencyMode
        => UseOptimisticConcurrency ? ConcurrencyMode.Optimistic : ConcurrencyMode.Off;

    /// <summary>
    ///     The table suffix, before <see cref="FisherTableNaming" /> folds in the schema prefix —
    ///     <c>doc_user</c> becomes <c>fi_doc_user</c>.
    /// </summary>
    internal string TableSuffix => $"doc_{Alias}";

    internal SqliteObjectName TableName
        => FisherTableNaming.ObjectFor(StoreOptions.DatabaseSchemaName, TableSuffix);

    internal string QuotedTableName
        => FisherTableNaming.QuotedTableName(StoreOptions.DatabaseSchemaName, TableSuffix);

    /// <summary>
    ///     The SQLite column type for the identity column.
    /// </summary>
    /// <remarks>
    ///     SQLite's type system is affinity-based, so this is a two-way split rather than a type map:
    ///     integral ids get INTEGER, and Guids and strings both land in TEXT. A Guid bound without
    ///     conversion would be written as a 16-byte BLOB that never matches the TEXT the column holds —
    ///     see <see cref="SqliteStorageDialect{T}.ToDatabaseValue" />.
    /// </remarks>
    internal string IdColumnType => IdType == typeof(int) || IdType == typeof(long) ? "INTEGER" : "TEXT";

    internal StorageColumnType IdStorageColumnType => IdType switch
    {
        var t when t == typeof(Guid) => StorageColumnType.Guid,
        var t when t == typeof(string) => StorageColumnType.String,
        var t when t == typeof(int) => StorageColumnType.Int,
        _ => StorageColumnType.Long
    };

    internal DocumentTable BuildTable() => new(this);

    /// <summary>
    ///     Write an identity onto a document instance.
    /// </summary>
    /// <remarks>
    ///     Weasel's <c>IIdentification</c> can read and generate an identity but not assign an
    ///     arbitrary one, so the setter lives here. Reflection per call rather than a compiled
    ///     delegate: this is reached only when something else has already decided the id — the
    ///     identity-from-string and identity-from-guid seams — not on the store or load hot paths.
    /// </remarks>
    internal void SetRawId(object document, object id)
    {
        switch (IdMember)
        {
            case PropertyInfo property:
                property.SetValue(document, id);
                break;

            case FieldInfo field:
                field.SetValue(document, id);
                break;
        }
    }

    private static Type TypeOf(MemberInfo member)
    {
        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    /// <summary>
    ///     Explain why a document type has no usable identity.
    /// </summary>
    /// <remarks>
    ///     The shared <c>DocumentIdentity.FindIdMember</c> filters candidates <em>by type</em>, so a
    ///     document with a perfectly good <c>Id</c> of an unsupported type comes back indistinguishable
    ///     from one with no <c>Id</c> at all. A second permissive pass — used only to build this
    ///     message, never to resolve — is what lets "your Id is a DateTime" be said out loud instead of
    ///     the flatly untrue "you have no Id".
    /// </remarks>
    private static InvalidOperationException DescribeMissingIdentity(Type documentType)
    {
        var supported = string.Join(", ", SupportedIdTypes.Select(x => x.Name));
        var anyIdMember = DocumentIdentity.FindIdMember(documentType, _ => true);

        if (anyIdMember is not null)
        {
            return new InvalidOperationException(
                $"Document type '{documentType.FullName}' has an identity member '{anyIdMember.Name}' of " +
                $"type '{TypeOf(anyIdMember).Name}', which Fisher cannot store. Supported identity types " +
                $"are {supported}.");
        }

        return new InvalidOperationException(
            $"Document type '{documentType.FullName}' has no identity member. Fisher needs a public " +
            $"property or field named 'Id', or one marked with [Identity], of type {supported}.");
    }

    /// <summary>
    ///     <c>User</c> becomes <c>user</c>; <c>Wrapper&lt;User&gt;</c> becomes <c>wrapper_of_user</c>.
    /// </summary>
    private static string DefaultAliasFor(Type documentType)
    {
        if (!documentType.IsGenericType)
        {
            return documentType.Name.ToLowerInvariant();
        }

        var name = documentType.Name[..documentType.Name.IndexOf('`')].ToLowerInvariant();
        var args = documentType.GetGenericArguments().Select(x => x.Name.ToLowerInvariant());

        return $"{name}_of_{string.Join("_", args)}";
    }
}

/// <summary>
///     The document types this store knows how to persist.
/// </summary>
/// <remarks>
///     Mappings are created on demand, so a document type never explicitly registered still works —
///     but only a type the store has been <em>told</em> about can have its table created ahead of
///     time by <c>ApplyAllConfiguredChangesToDatabaseAsync</c>. That is what <see cref="For{T}" /> is
///     for.
/// </remarks>
public class DocumentSchema
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, DocumentMapping> _mappings = new();
    private readonly StoreOptions _options;

    internal DocumentSchema(StoreOptions options)
    {
        _options = options;
    }

    /// <summary>
    ///     Register (or reach) the mapping for a document type, so its table is created with the rest
    ///     of the schema and its storage can be configured.
    /// </summary>
    public DocumentMapping For<T>() where T : notnull => MappingFor(typeof(T));

    /// <inheritdoc cref="For{T}" />
    public DocumentMapping MappingFor(Type documentType)
        => _mappings.GetOrAdd(documentType, type => new DocumentMapping(type, _options));

    /// <summary>
    ///     Every document mapping registered so far.
    /// </summary>
    public IReadOnlyList<DocumentMapping> AllMappings() => _mappings.Values.ToList();

    /// <summary>
    ///     Whether this type has been mapped — asked before creating a table for it, so that the
    ///     commit path can tell a document operation from an event one without a mapping appearing as
    ///     a side effect of the question.
    /// </summary>
    public bool HasMappingFor(Type documentType) => _mappings.ContainsKey(documentType);

    internal bool HasAny => !_mappings.IsEmpty;
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Fisher.Attributes;
using Fisher.Storage.Metadata;
using JasperFx;
using JasperFx.Metadata;
using JasperFx.MultiTenancy;
using Weasel.Core.Sequences;
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
///         hierarchy or sub-classing and no foreign keys — both are column-shape or SQL-generation
///         concerns that can be added later without disturbing what is here.
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
    ///     The canonical four from <c>JasperFx.DocumentIdentity</c>. A strong-typed wrapper around any
    ///     of them is supported too and resolved separately — see <see cref="StrongTypedId" />.
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

        // Read once, here, because the table shape and the storage's SQL are both derived from it —
        // see the DeleteStyle remarks for what changing it after that point does not do.
        if (documentType.GetCustomAttribute<SoftDeletedAttribute>() is not null
            || typeof(ISoftDeleted).IsAssignableFrom(documentType))
        {
            DeleteStyle = DeleteStyle.SoftDelete;
        }

        // IVersioned asks for a Guid version on the document, which is only meaningful if the column is
        // written and read — so it turns optimistic concurrency on, as it does on both siblings. The
        // reverse does not hold: UseOptimisticConcurrency() alone maps nothing, because there is no
        // member to name.
        if (Metadata.ApplyConventions(documentType))
        {
            UseOptimisticConcurrency = true;
        }

        // IRevisioned is the numeric counterpart, and turns on the mode it names for the same reason:
        // the member is only meaningful if the column backing it is written and read.
        if (typeof(JasperFx.IRevisioned).IsAssignableFrom(documentType))
        {
            UseNumericRevisions = true;
        }
    }

    /// <summary>The .NET type being stored.</summary>
    public Type DocumentType { get; }

    /// <summary>The member carrying the document's identity.</summary>
    public MemberInfo IdMember { get; }

    /// <summary>The identity member's type, with any <c>Nullable&lt;T&gt;</c> unwrapped.</summary>
    public Type IdType { get; }

    /// <summary>
    ///     Which of this type's members Fisher's metadata columns are projected onto when a document is
    ///     read. Every column is written regardless; mapping decides whether the value comes back.
    /// </summary>
    public DocumentMetadata Metadata { get; } = new();

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
    ///     Whether writes to this document type carry a numeric revision check against the
    ///     <c>revision</c> column, instead of the Guid one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two are alternatives, never both — a type carries <c>guid_version</c> or
    ///         <c>revision</c>, and <see cref="AssertConcurrencyIsCoherent" /> refuses the pair rather
    ///         than letting the descriptor pick one silently.
    ///     </para>
    ///     <para>
    ///         A numeric revision answers a question a Guid version cannot: it is readable, so a caller
    ///         can pass one over an API boundary, show it in a UI, or say "store this only if it is
    ///         still revision 4". That is what <c>Store(doc, revision)</c> and <c>UpdateRevision</c>
    ///         exist for, and neither has a <c>guid_version</c> equivalent.
    ///     </para>
    /// </remarks>
    public bool UseNumericRevisions { get; set; }

    /// <summary>
    ///     Whether each row carries a tenant id. Follows the store's event tenancy style by default,
    ///     which keeps a snapshot in the same tenancy shape as the stream it came from.
    /// </summary>
    public TenancyStyle TenancyStyle { get; set; } = TenancyStyle.Single;

    /// <summary>
    ///     Per-type override of the Hi-Lo sequence configuration used to assign an <c>int</c> or
    ///     <c>long</c> identity. Null falls back to <see cref="StoreOptions.HiloSequenceDefaults" />;
    ///     ignored entirely for Guid and string identities, which need no sequence.
    /// </summary>
    public HiloSettings? HiloSettings { get; set; }

    /// <summary>
    ///     Whether <c>Delete</c> removes the row or flags it. Defaults to
    ///     <see cref="JasperFx.DeleteStyle.Remove" />, and is set to
    ///     <see cref="JasperFx.DeleteStyle.SoftDelete" /> by a <see cref="SoftDeletedAttribute" />, by
    ///     implementing <see cref="ISoftDeleted" />, or by <see cref="SoftDeleted" />.
    /// </summary>
    /// <remarks>
    ///     Read when the document's table and its storage are built, which happens on first use — so
    ///     like <see cref="UseOptimisticConcurrency" />, changing it after a document of this type has
    ///     been stored in this process does not reshape what is already there. Configure it where the
    ///     rest of the store is configured.
    /// </remarks>
    public DeleteStyle DeleteStyle { get; set; } = DeleteStyle.Remove;

    /// <summary>
    ///     Flag this document type for soft deletion, returning the mapping so configuration chains.
    /// </summary>
    public DocumentMapping SoftDeleted()
    {
        DeleteStyle = DeleteStyle.SoftDelete;
        return this;
    }

    internal bool IsSoftDeleted => DeleteStyle == DeleteStyle.SoftDelete;

    internal bool IsConjoined => TenancyStyle == TenancyStyle.Conjoined;

    /// <summary>
    ///     Members lifted out of the JSON body into indexable columns of their own. Registered through
    ///     <see cref="DocumentMappingExpression{T}.Duplicate{TValue}" />, which is where the member
    ///     expression can be typed.
    /// </summary>
    internal List<DuplicatedField> DuplicatedFields { get; } = [];

    /// <summary>
    ///     User-declared indexes over members that were <em>not</em> duplicated. Registered through
    ///     <see cref="DocumentMappingExpression{T}.Index{TValue}" />.
    /// </summary>
    internal List<DocumentIndex> Indexes { get; } = [];

    /// <summary>
    ///     Sub-classes registered against this type, making it a hierarchy.
    /// </summary>
    internal List<SubClassMapping> SubClasses { get; } = [];

    /// <summary>
    ///     Whether rows of this type carry a <c>doc_type</c> discriminator.
    /// </summary>
    /// <remarks>
    ///     An abstract or interface document type counts even with nothing registered yet: it can never
    ///     be the concrete type of a row, so the column has to be there from the first migration —
    ///     adding it later would leave the rows already written with no discriminator to read.
    /// </remarks>
    internal bool IsHierarchy
        => SubClasses.Count > 0 || DocumentType.IsAbstract || DocumentType.IsInterface;

    /// <summary>The discriminator alias standing for a runtime type.</summary>
    internal string AliasFor(Type subclassType)
    {
        if (subclassType == DocumentType)
        {
            return Alias;
        }

        var sub = SubClasses.FirstOrDefault(x => x.DocumentType == subclassType);

        return sub?.Alias ?? throw new ArgumentOutOfRangeException(nameof(subclassType),
            $"'{subclassType.Name}' is not a registered subclass of '{DocumentType.Name}'. Register it "
            + $"with Schema.For<{DocumentType.Name}>().AddSubClass<{subclassType.Name}>().");
    }

    /// <summary>The runtime type a stored discriminator alias stands for.</summary>
    /// <remarks>
    ///     Throws rather than falling back to the base type for an unknown alias. A row written by a
    ///     deployment that knew a sub-class this one does not is a real gap in configuration, and
    ///     deserializing it as the base would hand back an object quietly missing whatever the sub-class
    ///     added. Deliberately the opposite of the event reads' policy, which skip an unresolvable
    ///     <c>dotnet_type</c> so a deployment stays able to read events it does not know — an event
    ///     store must remain readable, where a document load has one right answer and either has it or
    ///     does not.
    /// </remarks>
    internal Type TypeFor(string alias)
    {
        if (string.Equals(alias, Alias, StringComparison.OrdinalIgnoreCase))
        {
            return DocumentType;
        }

        var sub = SubClasses.FirstOrDefault(
            x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));

        return sub?.DocumentType ?? throw new ArgumentOutOfRangeException(nameof(alias),
            $"Unknown doc_type alias '{alias}' on a row of '{DocumentType.Name}'. It was written by a "
            + "deployment that had a subclass registered which this one does not.");
    }

    /// <summary>Register a sub-class, making this type a hierarchy.</summary>
    internal SubClassMapping AddSubClass(Type subclassType, string? alias = null)
    {
        if (!DocumentType.IsAssignableFrom(subclassType))
        {
            throw new ArgumentException(
                $"'{subclassType.Name}' does not inherit from '{DocumentType.Name}'.", nameof(subclassType));
        }

        var existing = SubClasses.FirstOrDefault(x => x.DocumentType == subclassType);
        if (existing is not null)
        {
            return existing;
        }

        var name = alias ?? SubClassMapping.DefaultAliasFor(subclassType);

        if (string.Equals(name, Alias, StringComparison.OrdinalIgnoreCase)
            || SubClasses.Any(x => string.Equals(x.Alias, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{DocumentType.Name}' already uses the doc_type alias '{name}'. Give one of the two "
                + "an explicit alias — the alias is what is stored, so a collision would make two types "
                + "indistinguishable coming back out.");
        }

        var mapping = new SubClassMapping(subclassType, name);
        SubClasses.Add(mapping);

        return mapping;
    }

    /// <summary>
    ///     Register an index over one or more member chains.
    /// </summary>
    /// <remarks>
    ///     Registering the same index twice is idempotent rather than an error, so a configuration
    ///     lambda that runs a registration helper more than once does not fail — the same discipline
    ///     <see cref="Duplicate" /> follows. Two <em>different</em> member sets landing on one index
    ///     name is a real mistake and says so.
    /// </remarks>
    internal DocumentIndex Index(MemberInfo[][] memberChains, string? name, bool isUnique)
    {
        if (memberChains.Length == 0)
        {
            throw new ArgumentException("An index needs at least one member.", nameof(memberChains));
        }

        if (Array.Exists(memberChains, chain => chain.Length == 0))
        {
            throw new ArgumentException("An indexed member chain cannot be empty.", nameof(memberChains));
        }

        var index = new DocumentIndex(memberChains, name, isUnique);

        var existing = Indexes.FirstOrDefault(x =>
            string.Equals(x.Name ?? x.DefaultNameSuffix(), index.Name ?? index.DefaultNameSuffix(),
                StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!existing.MemberNames.SequenceEqual(index.MemberNames, StringComparer.Ordinal)
                || existing.IsUnique != index.IsUnique)
            {
                throw new InvalidOperationException(
                    $"'{DocumentType.Name}' already declares an index named "
                    + $"'{existing.Name ?? existing.DefaultNameSuffix()}' over "
                    + $"{string.Join(", ", existing.MemberNames)}. Give one of the two an explicit name.");
            }

            return existing;
        }

        Indexes.Add(index);

        return index;
    }

    /// <summary>
    ///     Register a member as a duplicated field.
    /// </summary>
    /// <remarks>
    ///     Duplicating the same member twice is idempotent rather than an error, so a configuration
    ///     lambda that runs a registration helper more than once does not fail — but two <em>different</em>
    ///     members landing on one column name is a real mistake and says so.
    /// </remarks>
    internal DuplicatedField Duplicate(MemberInfo[] members, string? columnName, string? columnType,
        bool shouldIndex)
    {
        if (members.Length == 0)
        {
            throw new ArgumentException("A duplicated field needs at least one member.", nameof(members));
        }

        var name = columnName ?? DuplicatedField.DefaultColumnNameFor(members);
        DuplicatedField.AssertColumnNameIsAvailable(name, DocumentType);

        var existing = DuplicatedFields.FirstOrDefault(
            x => string.Equals(x.ColumnName, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!existing.MemberNames.SequenceEqual(members.Select(x => x.Name), StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"'{DocumentType.Name}' already duplicates {string.Join(".", existing.MemberNames)} "
                    + $"into column '{name}'. Give one of the two an explicit column name.");
            }

            return existing;
        }

        var field = new DuplicatedField(members, name, columnType, shouldIndex);
        DuplicatedFields.Add(field);

        return field;
    }

    /// <summary>
    ///     The duplicated field this member chain resolves to, or null when the member is only in the
    ///     JSON body.
    /// </summary>
    internal DuplicatedField? DuplicateFor(MemberInfo[] chain)
    {
        if (DuplicatedFields.Count == 0)
        {
            return null;
        }

        return DuplicatedFields.FirstOrDefault(
            x => x.MemberNames.SequenceEqual(chain.Select(m => m.Name), StringComparer.Ordinal));
    }

    /// <summary>
    ///     The concurrency mode the closed-shape storage runtime should use for this document.
    /// </summary>
    internal ConcurrencyMode ConcurrencyMode
        => UseNumericRevisions
            ? ConcurrencyMode.Numeric
            : UseOptimisticConcurrency
                ? ConcurrencyMode.Optimistic
                : ConcurrencyMode.Off;

    /// <summary>
    ///     Refuse a type configured for both concurrency styles at once.
    /// </summary>
    /// <remarks>
    ///     Only one version column is created, so a type asking for both would silently get whichever
    ///     the descriptor checked first — and the loser's <c>Store(doc, revision)</c> or version guard
    ///     would then do nothing at all. Said at configuration time rather than discovered as a guard
    ///     that never fires.
    /// </remarks>
    internal void AssertConcurrencyIsCoherent()
    {
        if (UseOptimisticConcurrency && UseNumericRevisions)
        {
            throw new InvalidOperationException(
                $"'{DocumentType.Name}' asks for both Guid optimistic concurrency and numeric "
                + "revisions. They are alternatives — a document type carries guid_version or "
                + "revision, not both. Choose one.");
        }
    }

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
    /// <summary>
    ///     The identity type as the database sees it — a strong-typed id wrapper's inner type, or
    ///     <see cref="IdType" /> itself.
    /// </summary>
    /// <remarks>
    ///     Everything about the <em>column</em> derives from here rather than from <see cref="IdType" />,
    ///     because the column holds the inner value; the wrapper exists only in .NET. Deriving the
    ///     column type from the wrapper would give an int-backed id a TEXT column, and a Guid-backed one
    ///     the wrong <see cref="StorageColumnType" /> — neither of which any compliance suite exercises,
    ///     since both only use Guid- and string-backed wrappers where TEXT happens to be right.
    /// </remarks>
    internal Type StoredIdType => StrongTypedId.StoredTypeFor(IdType);

    internal string IdColumnType
        => StoredIdType == typeof(int) || StoredIdType == typeof(long) ? "INTEGER" : "TEXT";

    internal StorageColumnType IdStorageColumnType => StoredIdType switch
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
                $"are {supported}, or a strong-typed wrapper around one — a type with a single public " +
                "gettable property and either a matching constructor or a static builder taking that " +
                "property's type.");
        }

        return new InvalidOperationException(
            $"Document type '{documentType.FullName}' has no identity member. Fisher needs a public " +
            $"property or field named 'Id', or one marked with [Identity], of type {supported}.");
    }

    /// <summary>
    ///     <c>User</c> becomes <c>user</c>; <c>Wrapper&lt;User&gt;</c> becomes <c>wrapper_of_user</c>.
    /// </summary>
    internal static string DefaultAliasFor(Type documentType)
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
    /// <remarks>
    ///     Returns a typed expression rather than the mapping itself, because <c>Duplicate</c> takes a
    ///     member lambda and C# cannot infer the document type from one. The mapping is a property on
    ///     it; everything that does not need the type parameter still lives there.
    /// </remarks>
    public DocumentMappingExpression<T> For<T>() where T : notnull
        => new(MappingFor(typeof(T)));

    /// <inheritdoc cref="For{T}" />
    public DocumentMapping MappingFor(Type documentType)
        => BaseMappingFor(documentType)
           ?? _mappings.GetOrAdd(documentType, type => new DocumentMapping(type, _options));

    /// <summary>
    ///     The hierarchy mapping a type is a registered sub-class of, or null when it is not one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is what makes a hierarchy share one table. Without it <c>Store(derived)</c> would
    ///         reach <c>MappingFor(typeof(Derived))</c>, create a mapping of its own, and write to
    ///         <c>fi_doc_derived</c> — so the sub-class would be registered, carry an alias, and still
    ///         land in the wrong table. It is checked before the cache rather than after, because a
    ///         sub-class must never acquire a mapping of its own.
    ///     </para>
    ///     <para>
    ///         Deliberately a scan rather than a second index. Hierarchies are few and this runs once
    ///         per type per store — <c>DocumentProviderRegistry</c> caches the provider it builds from
    ///         the answer.
    ///     </para>
    /// </remarks>
    internal DocumentMapping? BaseMappingFor(Type documentType)
        => _mappings.Values.FirstOrDefault(mapping =>
            mapping.DocumentType != documentType
            && mapping.SubClasses.Any(sub => sub.DocumentType == documentType));

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

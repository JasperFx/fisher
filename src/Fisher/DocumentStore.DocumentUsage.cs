using JasperFx.Descriptors;
using JasperFx.Events;
using Weasel.Sqlite;

namespace Fisher;

/// <summary>
///     The document half of the tooling contract: a description of every document mapping, for
///     monitoring tools (fisher#44).
/// </summary>
/// <remarks>
///     <para>
///         <c>IEventStore.TryCreateUsage</c> has answered the event half since the explorer work
///         landed. Without this, a tool pointed at a Fisher store renders half a picture — and, worse,
///         renders "no documents" rather than "this store does not answer that", which is the outcome
///         CLAUDE.md's standing discipline exists to prevent.
///     </para>
///     <para>
///         Implemented <b>explicitly</b>, as every other tooling surface on this store is, so a
///         monitoring-only API does not crowd <see cref="IDocumentStore" />. See that interface's
///         remarks.
///     </para>
/// </remarks>
public partial class DocumentStore : IDocumentStoreUsageSource
{
    // fisher#353: the STORE, not the file backing it -- the same identity IEventStore.Subject answers
    // with since fisher#279, and what Marten reports on both sides. The database uri named the file and
    // nothing narrower, so two stores sharing one file under different DatabaseSchemaNames -- the
    // layout AddFisherStore<T> exists to support -- reported one document identity between them, and a
    // console addressing "this store's documents" by it could not tell which store it had. It also
    // meant one DocumentStore answered "which store are you?" two ways depending on which interface
    // asked. The file is still reported, on DocumentStoreUsage.Database.
    Uri IDocumentStoreUsageSource.Subject => Internal.StoreSubject.For(Options.StoreName);

    /// <remarks>
    ///     Pure description — no queries, nothing that can fail on a database that is not there.
    ///     Everything reported is already on <see cref="Storage.DocumentMapping" />.
    /// </remarks>
    Task<DocumentStoreUsage?> IDocumentStoreUsageSource.TryCreateUsage(CancellationToken token)
    {
        var usage = new DocumentStoreUsage
        {
            Subject = "Fisher.DocumentStore",
            SubjectUri = ((IDocumentStoreUsageSource)this).Subject,
            Version = GetType().Assembly.GetName().Version?.ToString(),
            // fisher#240 — the tenancy's answer, not a hardcoded Single. See DescribeDatabases.
            Database = DescribeDatabases(),
            StoreName = Options.StoreName,
            DatabaseSchemaName = Options.DatabaseSchemaName,
            AutoCreateSchemaObjects = Options.AutoCreateSchemaObjects.ToString(),
            EnumStorage = Options.Serializer.EnumStorage.ToString(),

            // jasperfx#870 §5 — the member-name casing the stored JSON uses, so a console can build a
            // member path into raw JSON or explain why "Status" matched nothing in a document stored as
            // "status". Weasel's own enum, whose names are exactly the contract's three spellings.
            SerializerCasing = Options.Serializer.Casing.ToString()
        };

        var migrator = new SqliteMigrator();
        var mappings = MaterializeMappings();

        foreach (var mapping in mappings.OrderBy(x => x.Alias, StringComparer.Ordinal))
        {
            usage.Documents.Add(Describe(mapping, migrator));
        }

        usage.AddValue(nameof(Options.CommandTimeout), Options.CommandTimeout);
        usage.AddValue("HiloMaxLo", Options.HiloSequenceDefaults.MaxLo);
        usage.AddValue("JournalMode", Options.PragmaSettings.JournalMode.ToString());

        // Which optional document metadata this store actually captures, so a console can gate its
        // query facets on what is persisted rather than offering filters that match nothing.
        usage.DocumentMetadata = new DocumentMetadataCapabilities
        {
            StoreType = "Fisher",
            CorrelationId = mappings.Any(x => x.Metadata.CorrelationId.Enabled),
            CausationId = mappings.Any(x => x.Metadata.CausationId.Enabled),
            LastModifiedBy = mappings.Any(x => x.Metadata.LastModifiedBy.Enabled)
        };

        return Task.FromResult<DocumentStoreUsage?>(usage);
    }

    /// <summary>
    ///     Every document type this store knows about, forcing the lazily-created mappings into
    ///     existence first.
    /// </summary>
    /// <remarks>
    ///     A mapping is created on first use, so a store that has opened no session has none — which is
    ///     exactly the state a monitoring tool sees on a fresh boot. Both sources are swept: explicit
    ///     <c>Schema.For&lt;T&gt;()</c> registrations are already mappings, and a projection's aggregate
    ///     type is one only once something has asked for it.
    /// </remarks>
    internal IReadOnlyList<Storage.DocumentMapping> MaterializeMappings()
    {
        foreach (var aggregate in Options.Projections.All
                     .OfType<JasperFx.Events.Aggregation.IAggregateProjection>())
        {
            Options.Schema.MappingFor(aggregate.AggregateType);
        }

        return Options.Schema.AllMappings();
    }

    /// <remarks>
    ///     <b><c>PartitioningStrategy</c> is reported as null rather than omitted</b>, which is the
    ///     honest answer and not the same as saying nothing: SQLite has no table partitioning, so the
    ///     field has a value — "none" — rather than being unknown. See
    ///     <see cref="Storage.StorePolicies" /> for why that will not change.
    /// </remarks>
    private static DocumentMappingDescriptor Describe(Storage.DocumentMapping mapping, SqliteMigrator migrator)
    {
        var descriptor = new DocumentMappingDescriptor
        {
            DocumentType = TypeDescriptor.For(mapping.DocumentType),

            // The logical schema, which on SQLite is a table prefix rather than a real schema — see
            // FisherTableNaming. Reported as the name the operator configured, because that is what
            // they would search for.
            DatabaseSchemaName = mapping.StoreOptions.DatabaseSchemaName,
            Alias = mapping.Alias,
            IdStrategy = mapping.IdType.Name,
            TenancyStyle = mapping.TenancyStyle.ToString(),
            DeleteStyle = mapping.DeleteStyle.ToString(),
            UseOptimisticConcurrency = mapping.UseOptimisticConcurrency,
            UseNumericRevisions = mapping.UseNumericRevisions,
            SubClassCount = mapping.SubClasses.Count,
            SubClasses = mapping.SubClasses.Select(x => TypeDescriptor.For(x.DocumentType)).ToArray(),
            PartitioningStrategy = null,
            Partitioning = null,
            Ddl = WriteCreateStatement(mapping, migrator)
        };

        DescribeIndexesAndDuplicatedFields(mapping, descriptor);

        return descriptor;
    }

    /// <summary>
    ///     jasperfx#870 §5 — the table's indexes and duplicated fields in structured form, so a console
    ///     can say whether a filter can use an index without parsing <see cref="DocumentMappingDescriptor.Ddl" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read off the same <see cref="Storage.DocumentTable" /> the migration builds rather than
    ///         re-derived from the mapping, so the names and expressions are the ones SQLite will hold —
    ///         including the <c>doc_type</c> discriminator index and a duplicated field's own index, which
    ///         nothing in the mapping names.
    ///     </para>
    ///     <para>
    ///         <b>An index's <c>Members</c> are empty over metadata columns</b>, as the contract says, and
    ///         a duplicated field's index reports the member it duplicates: that column <em>is</em> the
    ///         member. <c>Method</c> is null throughout — SQLite has one index method, the b-tree.
    ///     </para>
    ///     <para>
    ///         Like the DDL, a mapping whose table cannot be built reports nothing here rather than
    ///         taking the whole store's description with it.
    ///     </para>
    /// </remarks>
    private static void DescribeIndexesAndDuplicatedFields(Storage.DocumentMapping mapping,
        DocumentMappingDescriptor descriptor)
    {
        Storage.DocumentTable table;
        try
        {
            table = new Storage.DocumentTable(mapping);
        }
        catch (Exception)
        {
            return;
        }

        static string PathOf(IEnumerable<System.Reflection.MemberInfo> chain)
            => string.Join(".", chain.Select(x => x.Name));

        var duplicatedByColumn = mapping.DuplicatedFields
            .ToDictionary(x => x.ColumnName, x => PathOf(x.Members), StringComparer.OrdinalIgnoreCase);

        foreach (var field in mapping.DuplicatedFields)
        {
            descriptor.DuplicatedFields.Add(new DuplicatedFieldDescriptor
            {
                MemberPath = duplicatedByColumn[field.ColumnName],
                ColumnName = field.ColumnName,
                DbType = table.ColumnFor(field.ColumnName)?.Type ?? field.ColumnType ?? string.Empty
            });
        }

        // The declared (fisher#16) indexes, by the name the table gave them — the same formula
        // DocumentTable applies when a declaration names none.
        var declaredMembers = mapping.Indexes
            .Where(x => x.MemberChains.Length > 0)
            .ToDictionary(
                x => x.Name ?? $"idx_{table.Identifier.Name}_{x.DefaultNameSuffix()}",
                x => x.MemberChains.Select(PathOf).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var index in table.Indexes)
        {
            var columns = index.Columns is { Length: > 0 } declared
                ? declared
                : index.Expression is { } expression ? [expression] : [];

            var members = declaredMembers.TryGetValue(index.Name, out var paths)
                ? paths
                : columns.Length == 1 && duplicatedByColumn.TryGetValue(columns[0], out var path)
                    ? [path]
                    : [];

            descriptor.Indexes.Add(new DocumentIndexDescriptor
            {
                Name = index.Name,
                Members = members,
                Columns = columns,
                IsUnique = index.IsUnique,
                Method = null,
                Predicate = index.Predicate
            });
        }
    }

    /// <remarks>
    ///     The DDL is what makes the descriptor useful for a schema diff, and it is also the one part
    ///     that can throw — a mapping with a configuration mistake fails here rather than when the
    ///     migration runs. Reported as a SQL comment, because a usage snapshot that threw would take
    ///     the whole store's description with it over one bad type.
    /// </remarks>
    private static string WriteCreateStatement(Storage.DocumentMapping mapping, SqliteMigrator migrator)
    {
        try
        {
            using var writer = new StringWriter();
            new Storage.DocumentTable(mapping).WriteCreateStatement(migrator, writer);

            return writer.ToString();
        }
        catch (Exception e)
        {
            return $"-- Failed to generate DDL: {e.Message}";
        }
    }
}

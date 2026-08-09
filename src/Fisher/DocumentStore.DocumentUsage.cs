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
    Uri IDocumentStoreUsageSource.Subject => Database.Describe().DatabaseUri();

    /// <remarks>
    ///     Pure description — no queries, nothing that can fail on a database that is not there.
    ///     Everything reported is already on <see cref="Storage.DocumentMapping" />.
    /// </remarks>
    Task<DocumentStoreUsage?> IDocumentStoreUsageSource.TryCreateUsage(CancellationToken token)
    {
        var usage = new DocumentStoreUsage
        {
            Subject = "Fisher.DocumentStore",
            SubjectUri = Database.Describe().DatabaseUri(),
            Version = GetType().Assembly.GetName().Version?.ToString(),
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = Database.Describe()
            },
            StoreName = Options.StoreName,
            DatabaseSchemaName = Options.DatabaseSchemaName,
            AutoCreateSchemaObjects = Options.AutoCreateSchemaObjects.ToString(),
            EnumStorage = Options.Serializer.EnumStorage.ToString()
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
        => new()
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

using Fisher.Attributes;
using Fisher.Linq;
using Fisher.Pagination;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Core;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind the docs/documents/* pages.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

#region sample_documents_country
public class Country
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
#endregion

public class Invoice
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

#region sample_documents_strong_typed_id
public readonly record struct CatchId(Guid Value);

public class TaggedCatch
{
    public CatchId Id { get; set; }
    public string Species { get; set; } = "";
}
#endregion

#region sample_documents_soft_deleted_interface
public class Angler : JasperFx.Metadata.ISoftDeleted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
#endregion

#region sample_documents_indexing_attributes
public class Catch
{
    public Guid Id { get; set; }

    [DuplicateField] public string Species { get; set; } = "";
    [Index] public DateTimeOffset Landed { get; set; }
    [UniqueIndex] public string Tag { get; set; } = "";

    public Guid AnglerId { get; set; }
    public Guid WaterId { get; set; }
    public decimal Weight { get; set; }
}
#endregion

#region sample_documents_metadata_attributes
public class AuditedOrder
{
    public Guid Id { get; set; }

    [VersionMetadata] public Guid Version { get; set; }
    [LastModifiedMetadata] public DateTimeOffset UpdatedAt { get; set; }
    [CreatedAtMetadata] public DateTimeOffset CreatedAt { get; set; }
    [CorrelationIdMetadata] public string? CorrelationId { get; set; }
    [CausationIdMetadata] public string? CausationId { get; set; }
    [LastModifiedByMetadata] public string? UpdatedBy { get; set; }
    [TenantIdMetadata] public string? TenantId { get; set; }
}
#endregion

#region sample_documents_versioned
public class VersionedOrder : JasperFx.Metadata.IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
}
#endregion

#region sample_documents_revisioned
public class RevisionedOrder : IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}
#endregion

#region sample_documents_hierarchy
public abstract class Vehicle
{
    public Guid Id { get; set; }
    public string Registration { get; set; } = "";
}

public class Car : Vehicle
{
    public int Doors { get; set; }
}

public class Truck : Vehicle
{
    public decimal PayloadTonnes { get; set; }
}
#endregion

#region sample_documents_initial_data
public class SeedCountries : IInitialData
{
    public async Task Populate(IDocumentStore store, CancellationToken token)
    {
        await using var session = store.LightweightSession();

        session.Store(new Country { Id = "no", Name = "Norway" });
        session.Store(new Country { Id = "se", Name = "Sweden" });

        await session.SaveChangesAsync(token);
    }
}
#endregion

public static class document_configuration_samples
{
    public static void schema_dsl(StoreOptions opts)
    {
        #region sample_documents_schema_dsl
        opts.Schema.For<Catch>()
            .DocumentAlias("catches")
            .SoftDeleted()
            .UseOptimisticConcurrency()
            .MultiTenanted()
            .Duplicate(x => x.Species)
            .Index(x => x.Landed)
            .UniqueIndex(x => x.Tag)
            .ForeignKey<Angler>(x => x.AnglerId);
        #endregion
    }

    public static void store_policies(StoreOptions opts)
    {
        #region sample_documents_store_policies
        opts.Policies.AllDocumentsAreMultiTenanted();
        opts.Policies.AllDocumentsSoftDeleted();
        opts.Policies.AllDocumentsUseOptimisticConcurrency();

        // A policy configures the DocumentMapping directly rather than through the Schema.For<T>()
        // expression, so it sets properties rather than calling the DSL methods.
        opts.Policies.ForAllDocuments(m => m.UseOptimisticConcurrency = true);

        // ForDocument<T> does *not* create the mapping — a type nothing ever stores stays
        // unmapped and gets no table. It means "if you store one of these, store it like so".
        opts.Policies.ForDocument<Catch>(m => m.TenancyStyle = TenancyStyle.Conjoined);
        #endregion
    }

    public static void indexing(StoreOptions opts)
    {
        #region sample_documents_duplicate_and_index
        opts.Schema.For<Catch>()
            .Duplicate(x => x.Species)      // generated column + index
            .Index(x => x.Landed)           // expression index only — no column at all
            .UniqueIndex(x => x.Tag);
        #endregion

        #region sample_documents_composite_index
        opts.Schema.For<Catch>()
            .Index([x => (object?)x.Species, x => (object?)x.Landed]);
        #endregion

        #region sample_documents_duplicate_named_column
        opts.Schema.For<Catch>().Duplicate(x => x.Landed, columnName: "landed");
        #endregion
    }

    public static void foreign_keys(StoreOptions opts)
    {
        #region sample_documents_foreign_key
        opts.Schema.For<Catch>()
            .ForeignKey<Angler>(x => x.AnglerId);
        #endregion

        #region sample_documents_foreign_key_cascade
        opts.Schema.For<Catch>()
            .ForeignKey<Angler>(x => x.AnglerId, CascadeAction.Cascade, columnName: "angler_id");
        #endregion
    }

    public static void hierarchies(StoreOptions opts)
    {
        #region sample_documents_add_subclass
        opts.Schema.For<Vehicle>()
            .AddSubClass<Car>()
            .AddSubClass<Truck>("lorry");     // an explicit alias
        #endregion

        #region sample_documents_add_subclass_hierarchy
        opts.Schema.For<Vehicle>().AddSubClassHierarchy();
        #endregion
    }

    public static void concurrency(StoreOptions opts)
    {
        #region sample_documents_optimistic_concurrency
        opts.Schema.For<VersionedOrder>().UseOptimisticConcurrency();
        #endregion

        #region sample_documents_numeric_revisions
        opts.Schema.For<RevisionedOrder>().UseNumericRevisions();
        #endregion
    }

    public static void metadata(StoreOptions opts)
    {
        #region sample_documents_enable_metadata_columns
        opts.Schema.For<AuditedOrder>().Metadata(m =>
        {
            m.CreatedAt.Enabled = true;
            m.CorrelationId.Enabled = true;
            m.CausationId.Enabled = true;
            m.LastModifiedBy.Enabled = true;
            m.Headers.Enabled = true;
        });
        #endregion

        #region sample_documents_map_metadata
        opts.Schema.For<AuditedOrder>().Metadata(m =>
        {
            m.Version.MapTo(x => x.Version);
            m.LastModified.MapTo(x => x.UpdatedAt);
            m.CreatedAt.MapTo(x => x.CreatedAt);
        });
        #endregion
    }

    public static void hilo(StoreOptions opts)
    {
        #region sample_documents_hilo
        // Per document type. Schema.For<T>() returns an expression; the mapping hangs off it.
        opts.Schema.For<Invoice>().Mapping.HiloSettings =
            new Weasel.Core.Sequences.HiloSettings { MaxLo = 100 };

        // Or store-wide, for every type with no settings of its own
        opts.HiloSequenceDefaults.MaxLo = 100;
        #endregion
    }

    public static void multi_tenanted(StoreOptions opts)
    {
        #region sample_documents_multi_tenanted
        opts.Schema.For<Catch>().MultiTenanted();
        #endregion
    }

    public static void initial_data(StoreOptions opts)
    {
        #region sample_documents_register_initial_data
        opts.InitialData.Add(new SeedCountries());
        #endregion
    }
}

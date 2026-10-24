using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;

namespace Fisher.Tests.Configuration;

/// <summary>
///     jasperfx#1081 and the per-type half of jasperfx#1077: a document descriptor says whether its
///     <c>Indexes</c> and <c>DuplicatedFields</c> lists are complete, and which metadata columns the type
///     itself captures.
/// </summary>
/// <remarks>
///     <para>
///         <b>An empty list was ambiguous before the flags.</b> A type with no secondary index and a type
///         whose table model failed to load both reported <c>Indexes = []</c>, and a console can only tell
///         a full scan from "unknown" if the store says which one it means.
///         <c>DocumentStore.DescribeIndexesAndDuplicatedFields</c> returns early with both lists empty when
///         the table cannot be built, and that is the one case that must leave the flags false.
///     </para>
/// </remarks>
public class document_descriptor_reporting_signals : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("descriptor-signals");
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Lighthouse>()
                .Duplicate(x => x.Keeper)
                .Index(x => x.Name);

            options.Schema.For<MooringBuoy>();

            options.Schema.For<Beacon>().UseOptimisticConcurrency().MultiTenanted()
                .Metadata(m =>
                {
                    m.CorrelationId.Enabled = true;
                    m.LastModifiedBy.Enabled = true;
                });

            options.Schema.For<NauticalChart>().UseNumericRevisions().SoftDeleted()
                .Metadata(m => m.CausationId.Enabled = true);
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<DocumentMappingDescriptor> DescriptorFor<T>()
    {
        var usage = await ((IDocumentStoreUsageSource)_store).TryCreateUsage(Token);
        return usage!.Documents.Single(x => x.DocumentType.Name == typeof(T).Name);
    }

    [Fact]
    public async Task a_type_with_indexes_and_duplicated_fields_reports_both_lists_as_complete()
    {
        var descriptor = await DescriptorFor<Lighthouse>();

        descriptor.Indexes.ShouldNotBeEmpty();
        descriptor.DuplicatedFields.ShouldHaveSingleItem();
        descriptor.IndexesReported.ShouldBeTrue();
        descriptor.DuplicatedFieldsReported.ShouldBeTrue();
    }

    /// <summary>
    ///     The case the flag exists for: two empty lists that MEAN empty. A filter on any member of this
    ///     type reads every row, and the descriptor can now say so.
    /// </summary>
    [Fact]
    public async Task a_type_with_nothing_declared_reports_its_empty_lists_as_complete()
    {
        var descriptor = await DescriptorFor<MooringBuoy>();

        descriptor.Indexes.ShouldBeEmpty();
        descriptor.DuplicatedFields.ShouldBeEmpty();
        descriptor.IndexesReported.ShouldBeTrue();
        descriptor.DuplicatedFieldsReported.ShouldBeTrue();
    }

    /// <summary>
    ///     A table model that fails to load — <c>DocumentStore.DocumentUsage.cs</c>'s <c>catch { return; }</c>
    ///     — must leave both flags false, so its two empty lists read as "not reported" rather than as a
    ///     type with no index. No configuration reaches that branch on purpose, so the table is handed in.
    /// </summary>
    [Fact]
    public void a_table_that_cannot_be_built_leaves_both_flags_false()
    {
        var mapping = _store.Options.Schema.MappingFor(typeof(Lighthouse));
        var descriptor = new DocumentMappingDescriptor();

        DocumentStore.DescribeIndexesAndDuplicatedFields(mapping, descriptor,
            () => throw new InvalidOperationException("the table model failed to load"));

        descriptor.Indexes.ShouldBeEmpty();
        descriptor.DuplicatedFields.ShouldBeEmpty();
        descriptor.IndexesReported.ShouldBeFalse();
        descriptor.DuplicatedFieldsReported.ShouldBeFalse();
    }

    [Fact]
    public async Task each_type_reports_the_metadata_its_own_table_captures()
    {
        var plain = (await DescriptorFor<MooringBuoy>()).Metadata.ShouldNotBeNull();
        plain.StoreType.ShouldBe("Fisher");
        plain.CorrelationId.ShouldBeFalse();
        plain.CausationId.ShouldBeFalse();
        plain.LastModifiedBy.ShouldBeFalse();
        plain.Version.ShouldBeFalse();
        plain.TenantId.ShouldBeFalse();
        plain.SoftDelete.ShouldBeFalse();
        plain.LastModified.ShouldBeTrue();

        var beacon = (await DescriptorFor<Beacon>()).Metadata.ShouldNotBeNull();
        beacon.CorrelationId.ShouldBeTrue();
        beacon.CausationId.ShouldBeFalse();
        beacon.LastModifiedBy.ShouldBeTrue();
        beacon.Version.ShouldBeTrue();
        beacon.TenantId.ShouldBeTrue();
        beacon.SoftDelete.ShouldBeFalse();

        var chart = (await DescriptorFor<NauticalChart>()).Metadata.ShouldNotBeNull();
        chart.CorrelationId.ShouldBeFalse();
        chart.CausationId.ShouldBeTrue();
        chart.LastModifiedBy.ShouldBeFalse();
        chart.Version.ShouldBeTrue();
        chart.SoftDelete.ShouldBeTrue();
    }

    /// <summary>
    ///     The store-wide block stays the "any type captures it" roll-up, so it can say true where a
    ///     particular type's says false — which is why the per-type block exists.
    /// </summary>
    [Fact]
    public async Task the_store_wide_block_is_still_the_roll_up_of_the_per_type_ones()
    {
        var usage = await ((IDocumentStoreUsageSource)_store).TryCreateUsage(Token);

        usage!.DocumentMetadata!.CorrelationId.ShouldBeTrue();
        usage.DocumentMetadata.CausationId.ShouldBeTrue();
        usage.Documents.Single(x => x.DocumentType.Name == nameof(MooringBuoy)).Metadata!.CorrelationId.ShouldBeFalse();
    }
}

public class MooringBuoy
{
    public Guid Id { get; set; }
    public string Colour { get; set; } = string.Empty;
}

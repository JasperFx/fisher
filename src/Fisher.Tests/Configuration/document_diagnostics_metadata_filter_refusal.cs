using JasperFx;
using JasperFx.Documents;

namespace Fisher.Tests.Configuration;

/// <summary>
///     jasperfx#1077: a <c>CorrelationId</c> / <c>CausationId</c> / <c>LastModifiedBy</c> filter on a type
///     that does not capture the column is refused, where it used to be skipped.
/// </summary>
/// <remarks>
///     <para>
///         <b>Skipping returned the unfiltered page and its unfiltered total</b>, which a console renders
///         as a filter that matched everything. <c>DiagnosticFilter.RequestedTerms</c> gated each term on
///         <c>column.Enabled</c> because a term on a column the table lacks is <c>no such column</c>; the
///         gate was right about the SQL and wrong about the answer. The refusal is the shared
///         <c>DocumentQueryOptions.AssertMetadataFiltersAreCaptured</c>, so the wording is every store's.
///     </para>
///     <para>
///         The capture flags are the ROOT mapping's, because a sub-class has no table of its own: a
///         sub-class of a root that captures correlation ids is filtered, not refused.
///     </para>
/// </remarks>
public class document_diagnostics_metadata_filter_refusal : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("diag-metadata-refusal");
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;
    private IDocumentStoreDiagnostics Diagnostics => _store;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // Captures nothing optional.
            options.Schema.For<Lighthouse>();

            // Captures correlation ids only.
            options.Schema.For<Beacon>().Metadata(m => m.CorrelationId.Enabled = true);

            // A hierarchy whose root captures causation ids.
            options.Schema.For<Lightship>().AddSubClass<RelieverLightship>()
                .Metadata(m => m.CausationId.Enabled = true);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.CorrelationId = "c-1";
        session.CausationId = "k-1";
        session.Store(new Lighthouse { Id = Guid.NewGuid(), Name = "Fastnet" });
        session.Store(new Lighthouse { Id = Guid.NewGuid(), Name = "Eddystone" });
        session.Store(new Beacon { Id = Guid.NewGuid(), Light = "red" });
        session.Store(new RelieverLightship { Id = Guid.NewGuid(), Name = "Planet", Nets = 2 });
        await session.SaveChangesAsync(Token);

        await using var other = _store.LightweightSession();
        other.CorrelationId = "c-2";
        other.CausationId = "k-2";
        other.Store(new Beacon { Id = Guid.NewGuid(), Light = "green" });
        other.Store(new RelieverLightship { Id = Guid.NewGuid(), Name = "Seven Stones", Nets = 3 });
        await other.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static readonly string LighthouseType = typeof(Lighthouse).FullName!;

    [Theory]
    [InlineData(nameof(DocumentQueryOptions.CorrelationId))]
    [InlineData(nameof(DocumentQueryOptions.CausationId))]
    [InlineData(nameof(DocumentQueryOptions.LastModifiedBy))]
    public async Task a_filter_on_a_column_the_type_does_not_capture_is_refused(string criterion)
    {
        var options = OptionsFiltering(criterion, "anything");

        var ex = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(LighthouseType, options, Token));

        ex.Criterion.ShouldBe(criterion);
        ex.Message.ShouldContain(nameof(Lighthouse));
    }

    /// <summary>The reported defect as it presented: an unfiltered page reading as a filtered one.</summary>
    [Fact]
    public async Task the_refusal_replaces_an_unfiltered_page_that_read_as_a_match()
    {
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { CorrelationId = "no-such-correlation" }, Token));
    }

    [Fact]
    public async Task a_filter_on_a_captured_column_still_filters()
    {
        var page = await Diagnostics.QueryDocumentsAsync(typeof(Beacon).FullName!,
            new DocumentQueryOptions(1, 10) { CorrelationId = "c-2" }, Token);

        page.TotalCount.ShouldBe(1);
        page.Documents.ShouldHaveSingleItem().Json.ShouldContain("green");
    }

    /// <summary>One captured column does not license another on the same type.</summary>
    [Fact]
    public async Task capturing_one_column_does_not_admit_a_filter_on_another()
    {
        var ex = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(typeof(Beacon).FullName!,
                new DocumentQueryOptions(1, 10) { CorrelationId = "c-2", CausationId = "k-2" }, Token));

        ex.Criterion.ShouldBe(nameof(DocumentQueryOptions.CausationId));
    }

    [Fact]
    public async Task a_sub_class_is_judged_by_its_roots_table()
    {
        var page = await Diagnostics.QueryDocumentsAsync(typeof(RelieverLightship).FullName!,
            new DocumentQueryOptions(1, 10) { CausationId = "k-1" }, Token);

        page.Documents.ShouldHaveSingleItem().Json.ShouldContain("Planet");

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(typeof(RelieverLightship).FullName!,
                new DocumentQueryOptions(1, 10) { CorrelationId = "c-1" }, Token));
    }

    /// <summary>
    ///     Every path the page read can take refuses: with criteria, across every tenant, and on a type
    ///     whose table does not exist yet — whether a column can be filtered is a fact about the type.
    /// </summary>
    [Fact]
    public async Task every_read_path_refuses_alike()
    {
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { LastModifiedBy = "ann", Where = "Name != null" }, Token));

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            Diagnostics.QueryDocumentsAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { LastModifiedBy = "ann", AllTenants = true }, Token));

        using var fresh = TemporaryDatabase.Create("diag-metadata-refusal-fresh");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = fresh.ConnectionString;
            options.Schema.For<Lighthouse>();
        });

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            ((IDocumentStoreDiagnostics)store).QueryDocumentsAsync(LighthouseType,
                new DocumentQueryOptions(1, 10) { CorrelationId = "c-1" }, Token));
    }

    private static DocumentQueryOptions OptionsFiltering(string criterion, string value)
        => criterion switch
        {
            nameof(DocumentQueryOptions.CorrelationId) => new DocumentQueryOptions(1, 10) { CorrelationId = value },
            nameof(DocumentQueryOptions.CausationId) => new DocumentQueryOptions(1, 10) { CausationId = value },
            _ => new DocumentQueryOptions(1, 10) { LastModifiedBy = value }
        };
}

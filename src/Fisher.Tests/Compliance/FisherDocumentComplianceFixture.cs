using System.Reflection;
using JasperFx;
using JasperFx.Documents;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Compliance;

/// <summary>
///     Fisher's implementation of the cross-store <b>document</b> compliance seam (fisher#68 /
///     jasperfx#647) — the slice <c>JasperFx.Events</c> did not cover before 2.47.0.
/// </summary>
/// <remarks>
///     <para>
///         Three members wide, and deliberately <em>not</em> generic over Fisher's session pair the way
///         <see cref="FisherComplianceFixture" /> is. Everything the document suites do runs through
///         <see cref="IDocumentSessionFactory" /> and the three shared session contracts, so there is
///         nothing for a generic to carry — and typing <see cref="Sessions" /> as the bare contract is
///         what makes it a compile error for a suite to reach past it onto a Fisher type.
///     </para>
///     <para>
///         <b><see cref="IDocumentStore" /> is handed back directly — there is no adapter</b>, which is
///         the whole point of fisher#68's first half: Fisher's own session and store types
///         <em>are</em> the shared contracts.
///     </para>
///     <para>
///         <b>Document types are registered up front rather than left to be created on demand, and on
///         SQLite that is required rather than tidy.</b> Fisher creates a document table at the first
///         write of that type, and SQLite resolves a table name when it <em>prepares</em> a statement —
///         so <c>query_over_an_empty_document_type_returns_an_empty_list</c> and the <c>AnyAsync</c>
///         over an untouched <c>ComplianceGadget</c> would fail with <c>no such table</c> rather than
///         answering empty. <see cref="DocumentComplianceConfig.DocumentTypes" /> exists for exactly
///         this, and <c>DocumentSchema.MappingFor</c> is the non-generic registration that puts each
///         type into the migration. Same lesson rebuild teardown, <c>CleanAsync&lt;T&gt;</c> and the
///         document diagnostics each had to learn.
///     </para>
///     <para>
///         <b>Isolation is per fixture instance and xUnit builds one per test, so each test gets its own
///         throwaway SQLite file.</b> Marten and Polecat both had to put their four document suites in
///         one xUnit collection, because all four pin <c>SchemaName</c> to
///         <c>compliance_documents</c> and the base suite wipes document data before every test — run in
///         parallel against one server, one class's wipe lands in the middle of another's test. Fisher
///         needs no collection: a file per test means the wipe has nothing else to reach. The schema
///         name still flows through, where it folds into the table prefix rather than naming a real
///         schema.
///     </para>
/// </remarks>
public class FisherDocumentComplianceFixture : DocumentStorageComplianceFixture
{
    private TemporaryDatabase? _database;
    private DocumentStore? _store;

    protected override async Task BuildStoreAsync(DocumentComplianceConfig config)
    {
        await DisposeStoreAsync().ConfigureAwait(false);

        var schemaName = (config.SchemaName ?? "compliance_documents").ToLowerInvariant();

        _database = TemporaryDatabase.Create(schemaName);

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = schemaName;

            // Before the mappings: a document keyed by a wrapper has to have that wrapper resolvable
            // as an identity type by the time its mapping is built, or the mapping has no identity
            // member to find. Fisher discovers wrappers by itself, so this is an assertion rather than
            // a prerequisite — but a configuration that would break under a store that needs it is not
            // worth writing.
            foreach (var valueType in config.ValueTypes)
            {
                options.RegisterValueType(valueType);
            }

            foreach (var documentType in config.DocumentTypes)
            {
                options.Schema.MappingFor(documentType);
            }

            // jasperfx#898. Conjoined DOCUMENT tenancy, replayed onto the same non-generic mapping the
            // loop above just resolved — Schema.For<T>().MultiTenanted() sets exactly this property.
            //
            // ⚠️ Before the migration rather than after, and that is not merely ordering hygiene:
            // TenancyStyle decides whether the table carries a tenant_id column AND whether the primary
            // key is (tenant_id, id) or id alone, so a type conjoined after the schema was applied has a
            // table that cannot hold two tenants' copies of one id. Which is the exact shape
            // DocumentConjoinedTenancyCompliance exists to catch, so it would fail as a wiring error
            // dressed as a product bug.
            //
            // Per type rather than through Policies.AllDocumentsAreMultiTenanted(), because the suite
            // needs a single-tenanted type to remain single-tenanted where it declares one.
            foreach (var documentType in config.ConjoinedDocuments)
            {
                options.Schema.MappingFor(documentType).TenancyStyle =
                    JasperFx.MultiTenancy.TenancyStyle.Conjoined;
            }

            // jasperfx#870 (fisher#364). Soft deletes and hierarchies, replayed onto the same
            // non-generic mapping as everything else — Schema.For<T>().SoftDeleted() and
            // .AddSubClass<TSub>() call exactly these two members. Both change the table the migration
            // builds (is_deleted/deleted_at, doc_type), so they go in before it, and a dropped replay
            // fails every gated fact rather than skipping it: a hard-deleted ticket is simply gone, and
            // an unregistered sub-class gets a table of its own.
            foreach (var documentType in config.SoftDeletedDocuments)
            {
                options.Schema.MappingFor(documentType).SoftDeleted();
            }

            foreach (var declaration in config.SubClasses)
            {
                options.Schema.MappingFor(declaration.Root).AddSubClass(declaration.SubClass);
            }

            // jasperfx#842. The vector indexes DocumentSearchCompliance declares, replayed onto the
            // same non-generic mapping as everything else above.
            //
            // ⚠️ This is not optional and it does not degrade gracefully. A vector search reads a
            // DECLARED index — Fisher refuses a search over an undeclared member by name — so a
            // fixture that flipped SupportsVectorSearch and dropped this loop fails every fact in
            // the suite rather than skipping them, with an error about configuration rather than
            // about the search. The metric travels with the declaration for the same reason: the
            // index's distance is what a search uses when the caller names none, and the suite's
            // facts are stated in terms of "nearest" under the declared metric.
            foreach (var declaration in config.VectorIndexes)
            {
                options.Schema
                    .MappingFor(declaration.DocumentType)
                    .AddVectorIndex(
                        MemberChainFor(declaration.DocumentType, declaration.MemberName),
                        declaration.Dimensions,
                        declaration.Distance);
            }

            // The full-text half of a hybrid search. One index per document type on Fisher — a
            // search operator names no index, so a second would have nothing to tell it apart —
            // which is why every declared member for a type goes into a single call rather than one
            // call per member.
            foreach (var group in config.FullTextIndexes.GroupBy(x => x.DocumentType))
            {
                var chains = group
                    .SelectMany(x => x.MemberNames)
                    .Select(name => MemberChainFor(group.Key, name))
                    .ToArray();

                options.Schema
                    .MappingFor(group.Key)
                    .AddFullTextIndex(chains, Fisher.Storage.FullText.FullTextTokenizer.Porter);
            }

            // jasperfx#819. Replayed onto the SAME non-generic mapping the loop above just resolved,
            // which is the shipped registration route rather than a test-only one —
            // Schema.For<T>().UseOptimisticConcurrency(true) and .UseNumericRevisions() set exactly
            // these two properties.
            //
            // ⚠️ The optimistic replay is load-bearing rather than belt-and-braces. ComplianceShipment
            // implements IVersioned *and* the config declares the type, because the stores disagree
            // about whether the marker is itself the opt-in or merely supplies the member to guard on;
            // a suite declaring only the marker would be testing that disagreement. On Fisher the
            // marker IS the opt-in (DocumentMetadata's conventions turn it on, as Marten's
            // VersionedPolicy does), so dropping this loop leaves every fact passing and nothing
            // announcing that the config was ignored — which is why the config member says so at
            // length rather than leaving it to each fixture.
            foreach (var type in config.OptimisticConcurrencyTypes)
            {
                options.Schema.MappingFor(type).UseOptimisticConcurrency = true;
            }

            // jasperfx#943. The mapped route to the same guard: a member the configuration names through
            // Metadata(m => m.Version.MapTo(...)), resolved here by name onto the same MetadataColumn the
            // DSL reaches. Fisher has carried that route since fisher#245; this is the replay
            // SupportsMappedConcurrencyMember requires, and the flag below is what turns the facts on.
            foreach (var declaration in config.MappedVersionMembers)
            {
                var member = declaration.DocumentType.GetProperty(declaration.MemberName)
                    ?? throw new InvalidOperationException(
                        $"{declaration.DocumentType.Name} has no property named {declaration.MemberName}.");

                options.Schema.MappingFor(declaration.DocumentType).Metadata.Version.MapTo(member);
            }

            // No suite populates this one — jasperfx#819 §2 was written, run against Fisher, and
            // withdrawn, because the declared route has no document member to name a revision on or
            // read one back off. Replayed anyway: it costs nothing, and it is the half of the
            // declaration a Type alone can carry.
            foreach (var type in config.NumericRevisionTypes)
            {
                options.Schema.MappingFor(type).UseNumericRevisions = true;
            }

            // jasperfx#672. The suite states the stream identity it needs and the fixture replays it,
            // exactly as it replays the value types above. This used to be an *inference* made here —
            // string identity whenever the config declared event types — which was right only because
            // DocumentSessionEventsCompliance was the only suite populating EventTypes, and would have
            // silently mis-configured the first Guid-keyed event suite to arrive. Null means "leave the
            // store on its own default", so every document-only suite is untouched.
            if (config.StreamIdentity.HasValue)
            {
                options.Events.StreamIdentity = config.StreamIdentity.Value;
            }

            // jasperfx#669. Only DocumentSessionEventsCompliance populates this, and only because that
            // suite reaches the event store through a document session. Registering up front is the
            // same discipline the document types above need and for a related reason: the event tables
            // have to be in the migration this fixture applies, and on SQLite a table that is not there
            // when a statement is prepared is a `no such table`, not an empty result.
            foreach (var eventType in config.EventTypes)
            {
                options.Events.AddEventType(eventType);
            }

            // jasperfx#679. The one config member that carries instances rather than Types, because
            // the suite has to hold the very listener it registered in order to read back what the
            // store handed it. Adapted onto Fisher's own listener type and added to the same
            // Listeners collection every other listener uses — Fisher deliberately grew no second
            // list for the shared contract, so this is the shipped registration route rather than a
            // test-only one.
            foreach (var listener in config.CommitListeners)
            {
                options.Listeners.Add(listener.AsSessionListener());
            }
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Cancellation).ConfigureAwait(false);
    }

    public override IDocumentSessionFactory Sessions => _store
        ?? throw new InvalidOperationException("The compliance store has not been configured yet.");

    /// <summary>
    ///     Numeric revisions — the INTEGER <c>revision</c> column, <c>Store(doc, revision)</c>,
    ///     <c>UpdateRevision</c> and <c>TryUpdateRevision</c> (fisher#18).
    /// </summary>
    public override bool SupportsNumericRevisions => true;

    /// <summary>
    ///     <see cref="Guid" /> optimistic concurrency — the <c>guid_version</c> column and the guard
    ///     behind it (fisher#245).
    /// </summary>
    /// <remarks>
    ///     The two are alternatives on any one type — <c>AssertConcurrencyIsCoherent</c> refuses the
    ///     pair at configuration time — but the store supports both, and each suite names its own type.
    /// </remarks>
    public override bool SupportsOptimisticConcurrency => true;

    /// <summary>
    ///     The version guard reached through a member named by <c>Metadata(m =&gt; m.Version.MapTo(...))</c>
    ///     rather than through <c>IVersioned</c> (fisher#245, jasperfx#943).
    /// </summary>
    /// <remarks>
    ///     Both routes land on the same <c>MetadataColumn</c>, and <c>MappedVersionFor</c> reads one member
    ///     for both, which is why fisher#245 asserted every cross-session fact for both routes. Requires the
    ///     <c>MappedVersionMembers</c> replay above.
    /// </remarks>
    public override bool SupportsMappedConcurrencyMember => true;

    /// <summary>
    ///     Vector search — <c>IDocumentSearchOperations.VectorSearchWithScoresAsync</c>, reached
    ///     through <c>IDocumentReadOperations.Search</c> (fisher#241, fisher#291).
    /// </summary>
    /// <remarks>
    ///     Fisher's search is an EXACT scan through a registered SQLite distance function rather than
    ///     an approximate index, so the filter facts jasperfx#842 warns approximate stores about cost
    ///     it nothing: there is no candidate bound for a predicate to fall outside of.
    /// </remarks>
    public override bool SupportsVectorSearch => true;

    /// <summary>
    ///     Hybrid search — the FTS5 leg and the vector leg fused by reciprocal rank fusion
    ///     (fisher#243).
    /// </summary>
    public override bool SupportsHybridSearch => true;

    /// <summary>
    ///     Conjoined document tenancy — <c>Schema.For&lt;T&gt;().MultiTenanted()</c>, a <c>tenant_id</c>
    ///     column leading the primary key, and every read scoped to the session's tenant (fisher#51).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Flipping this is what makes <c>document_conjoined_tenancy_compliance</c> run, and the
    ///         thing it was gated behind on Fisher was not the tenancy — that has worked since fisher#51
    ///         made the filter a statement-level pass — but the two tenant-scoped session overloads on
    ///         <see cref="IDocumentSessionFactory" />. Those are additive members with throwing
    ///         defaults, and Fisher's own <c>LightweightSession(string? tenantId = null)</c> does not
    ///         satisfy either of them, so <see cref="IDocumentStore" /> forwards all four explicitly.
    ///     </para>
    ///     <para>
    ///         ⚠️ That is the near-miss the contract's own remarks warn about and it is worth restating
    ///         where a reader will meet it: the forwarders are not decoration. Delete them and this
    ///         fixture still compiles, the store is still perfectly correct about tenancy, and every
    ///         fact in the suite fails with <c>NotSupportedException</c> from a default implementation.
    ///     </para>
    /// </remarks>
    public override bool SupportsConjoinedDocuments => true;

    /// <summary>
    ///     <c>IDocumentStoreDiagnostics</c> and its write sibling, grown to jasperfx#870's contract
    ///     (fisher#364). Both are the store itself, implemented explicitly, which is what the suite's
    ///     "subject pairs with the usage source" fact relies on.
    /// </summary>
    public override bool SupportsDocumentDiagnostics => true;

    /// <inheritdoc cref="SupportsDocumentDiagnostics" />
    public override IDocumentStoreDiagnostics DocumentDiagnostics => (IDocumentStoreDiagnostics)Sessions;

    /// <inheritdoc cref="SupportsDocumentDiagnostics" />
    public override bool SupportsDocumentDiagnosticWrites => true;

    /// <inheritdoc cref="SupportsDocumentDiagnostics" />
    public override IDocumentStoreDiagnosticsWriter DocumentDiagnosticsWriter
        => (IDocumentStoreDiagnosticsWriter)Sessions;

    /// <summary>The fixture replays <c>DocumentComplianceConfig.SoftDeletedDocuments</c>.</summary>
    public override bool SupportsSoftDeletedDocuments => true;

    /// <summary>The fixture replays <c>DocumentComplianceConfig.SubClasses</c>.</summary>
    public override bool SupportsDocumentHierarchies => true;

    /// <summary>
    ///     <c>DocumentQueryOptions.AllTenants</c> (jasperfx#928, fisher#368): no tenant predicate under
    ///     conjoined tenancy, a fan-out under database-per-tenant.
    /// </summary>
    public override bool SupportsDocumentDiagnosticAllTenants => true;

    // SupportsDocumentDiagnosticCriteria stays false, deliberately: Where / OrderBy are Dynamic LINQ
    // text for the store's own IQueryable<T>, and the translation is jasperfx#869, still open. Left
    // false, the suite does not skip the criteria facts — it asserts they are REFUSED with
    // DocumentCriteriaNotSupportedException, which is the contract for a store that cannot apply them.

    /// <summary>
    ///     The two deliberate escapes from tenant scoping — <c>AnyTenant()</c> and
    ///     <c>TenantIsOneOf(...)</c> (fisher#26).
    /// </summary>
    /// <remarks>
    ///     Both are queryable operators on Fisher rather than element predicates inside a
    ///     <c>Where</c>, which is the shape the seam's remarks describe for Polecat and Fisher against
    ///     Marten's. They are only expressible because the tenant filter is its own statement-level
    ///     pass: an operator that <em>replaces</em> the term cannot be written while the term is welded
    ///     to each caller predicate, which is what fisher#51 changed.
    /// </remarks>
    public override bool SupportsCrossTenantQueries => true;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<T>> QueryAllTenantsAsync<T>(
        IDocumentReadOperations session, CancellationToken token)
        => await session.Query<T>().AnyTenant().ToListAsync(token).ConfigureAwait(false);

    /// <inheritdoc />
    public override async Task<IReadOnlyList<T>> QueryTenantsAsync<T>(
        IDocumentReadOperations session, string[] tenantIds, CancellationToken token)
        => await session.Query<T>().TenantIsOneOf(tenantIds).ToListAsync(token).ConfigureAwait(false);

    public override async Task CleanDocumentDataAsync()
    {
        if (_store is null)
        {
            return;
        }

        await _store.Advanced.Clean.DeleteAllDocumentsAsync(Cancellation).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeStoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeStoreAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }

        _database?.Dispose();
        _database = null;
    }

    /// <summary>
    ///     Turn a declared member NAME into the member chain Fisher's mapping wants.
    /// </summary>
    /// <remarks>
    ///     The shared declaration carries a name rather than an expression because it has no type
    ///     parameter to write one against — <see cref="VectorIndexDeclaration" /> is a record holding
    ///     a <see cref="Type" />. Fisher's public DSL takes a lambda and immediately reduces it to
    ///     exactly this chain, so resolving the name here lands in the same place without a generic
    ///     dance through reflection.
    /// </remarks>
    private static MemberInfo[] MemberChainFor(Type documentType, string memberName)
    {
        var member = documentType
            .GetMember(memberName, BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(x => x is PropertyInfo or FieldInfo);

        return member is null
            ? throw new InvalidOperationException(
                $"The compliance configuration declared an index on '{documentType.FullName}.{memberName}', "
                + "and no public instance property or field of that name exists.")
            : [member];
    }
}

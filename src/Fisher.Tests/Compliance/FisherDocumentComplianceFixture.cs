using JasperFx;
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

            // jasperfx#669. Only DocumentSessionEventsCompliance populates this, and only because that
            // suite reaches the event store through a document session. Registering up front is the
            // same discipline the document types above need and for a related reason: the event tables
            // have to be in the migration this fixture applies, and on SQLite a table that is not there
            // when a statement is prepared is a `no such table`, not an empty result.
            if (config.EventTypes.Count != 0)
            {
                // The suite appends by string stream key throughout and the config carries no knob for
                // it, so the stream identity is the fixture's to supply. Guid — Fisher's default and
                // every sibling's — would refuse every append in the suite by name.
                options.Events.StreamIdentity = StreamIdentity.AsString;

                foreach (var eventType in config.EventTypes)
                {
                    options.Events.AddEventType(eventType);
                }
            }
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Cancellation).ConfigureAwait(false);
    }

    public override IDocumentSessionFactory Sessions => _store
        ?? throw new InvalidOperationException("The compliance store has not been configured yet.");

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
}

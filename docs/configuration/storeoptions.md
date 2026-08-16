# Configuring Document Storage

`StoreOptions` is the configuration root, reached from `AddFisher(options => …)` or
`DocumentStore.For(opts => …)`.

## Core options

```cs
var store = DocumentStore.For(opts =>
{
    opts.Connection("Data Source=app.db");

    // Folds into the table prefix rather than being a real schema — see below.
    opts.DatabaseSchemaName = "main";

    // Names this store in traces and in monitoring tools.
    opts.StoreName = "Main";

    // How aggressively Fisher creates and migrates tables.
    opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

    // Command timeout in seconds. See the note below — this is not what you may think.
    opts.CommandTimeout = 30;

    // Pool ceiling per database file.
    opts.MaxPoolSize = 8;
});
```

## DatabaseSchemaName, and why it is a prefix

**SQLite has no schemas.** Rather than pretend otherwise, Fisher folds the logical schema name into
the table *prefix*:

| `DatabaseSchemaName` | Event table | Document table for `Order` |
| :--- | :--- | :--- |
| `main` (default) | `fi_events` | `fi_doc_order` |
| `reporting` | `reporting_fi_events` | `reporting_fi_doc_order` |

Every `DbObjectName` uses the SQLite schema `main`, so nothing ever renders as qualified SQL. That is
what gives two logical stores real isolation inside one database file with no `ATTACH` lifecycle to
re-establish on every pooled connection.

::: warning
Two stores registered over one file with the same `DatabaseSchemaName` are
[refused](/configuration/multiple-stores) — they would share every table, each reading, writing and
cleaning the other's rows, silently.
:::

## AutoCreateSchemaObjects

| Value | Behaviour |
| :--- | :--- |
| `CreateOrUpdate` | Create missing objects and migrate existing ones. The default. |
| `CreateOnly` | Create missing objects; never alter one that exists. |
| `All` | Drop and recreate. |
| `None` | Never touch the schema. Everything must exist already. |

`AutoCreate.None` is honoured everywhere for free, because all DDL goes through Weasel's migrations
rather than being issued ad hoc at call sites.

::: warning
Fisher normally creates a document type's table **on demand**, the first time something reads or
writes one — a snapshot type is registered by projection configuration, which can run after the
schema was last applied.

Under `AutoCreate.None` that path checks instead of creating, and throws naming the document type if
its table is missing. So a store configured this way must apply its schema out of band — with
`ApplyAllConfiguredChangesToDatabaseAsync`, the generated DDL, or
`ApplyAllDatabaseChangesOnStartup()` — and must **re-apply it after registering a new projection**,
since that registration is what maps the snapshot's document type.
:::

## Event store options

```cs
opts.Events.StreamIdentity = StreamIdentity.AsGuid;   // or AsString
opts.Events.TenancyStyle = TenancyStyle.Conjoined;    // or Single

opts.Events.EnableCorrelationId = true;
opts.Events.EnableCausationId = true;
opts.Events.EnableUserName = true;
opts.Events.EnableHeaders = true;

opts.Events.DatabaseSchemaName = "events";            // events in their own prefix

opts.Events.AddEventType<OrderPlaced>();
```

The four `Enable*` flags each add a column to `fi_events`, so they are schema decisions — set them
before the tables are created. They also gate the matching filters on
[event queries](/events/querying): a filter on a column that does not exist would be
`no such column` rather than an empty result, so an ungated one is ignored.

Other event options worth knowing:

| Option | Purpose |
| :--- | :--- |
| `Events.BinarySerializer` | Enables [binary event bodies](/events/storage#binary-event-bodies). |
| `Events.HighWaterLivenessInterval` | How often an idle daemon re-stamps its liveness mark. |
| `Events.MessageOutbox` | The [side-effect](/events/projections/side-effects) sink. |
| `Events.AppendObserver` | A callback with every appended event, after commit. |
| `Events.EnableSideEffectsOnInlineProjections` | Lets inline projections publish messages. |
| `Events.RegisterTagType<T>()` | Registers a [DCB tag](/events/dcb) type and its table. |

## Document schema DSL

`opts.Schema.For<T>()` returns a `DocumentMappingExpression<T>`:

<!-- snippet: sample_documents_schema_dsl -->
<a id='snippet-sample_documents_schema_dsl'></a>
```cs
opts.Schema.For<Catch>()
    .DocumentAlias("catches")
    .SoftDeleted()
    .UseOptimisticConcurrency()
    .MultiTenanted()
    .Duplicate(x => x.Species)
    .Index(x => x.Landed)
    .UniqueIndex(x => x.Tag)
    .ForeignKey<Angler>(x => x.AnglerId);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L134-L144' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_schema_dsl' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each is documented in its own page: [soft delete](/documents/deletes),
[concurrency](/documents/concurrency), [multi-tenancy](/documents/multi-tenancy),
[duplicated fields](/documents/indexing/duplicated-fields), [indexes](/documents/indexing/indexes),
[foreign keys](/documents/indexing/foreign-keys), [hierarchies](/documents/hierarchies),
[metadata](/documents/metadata).

## Configuration layers, and their order

There are four ways to configure a document type, and the order they apply in is worth remembering.
Each overrides the one before:

1. **Store policies** (`opts.Policies`) — written without knowing about the type they land on.
2. **JasperFx metadata interfaces** (`ISoftDeleted`, `IVersioned`, `IRevisioned`) — intrinsic to the
   type, but saying nothing about this store.
3. **Schema attributes** (`[Index]`, `[UniqueIndex]`, `[DuplicateField]`, `[HiloSequence]`,
   `[SoftDeleted]`) — on the type, and about storage.
4. **`opts.Schema.For<T>()`** — naming the type in this store's own configuration.

Weakest first, and the reason reads off the layer. The first three run when the mapping is created;
the DSL runs afterwards.

### Store policies

<!-- snippet: sample_documents_store_policies -->
<a id='snippet-sample_documents_store_policies'></a>
```cs
opts.Policies.AllDocumentsAreMultiTenanted();
opts.Policies.AllDocumentsSoftDeleted();
opts.Policies.AllDocumentsUseOptimisticConcurrency();

// A policy configures the DocumentMapping directly rather than through the Schema.For<T>()
// expression, so it sets properties rather than calling the DSL methods.
opts.Policies.ForAllDocuments(m => m.UseOptimisticConcurrency = true);

// ForDocument<T> does *not* create the mapping — a type nothing ever stores stays
// unmapped and gets no table. It means "if you store one of these, store it like so".
opts.Policies.ForDocument<Catch>(m => m.TenancyStyle = TenancyStyle.Conjoined);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L149-L161' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_store_policies' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`ForDocument<T>` is **not** `Schema.For<T>()`: it does not create the mapping. A type nothing ever
stores stays unmapped and gets no table. It means "if you store one of these, store it like so".
:::

::: warning
**Table partitioning is out of scope permanently, not pending.** SQLite has no partition functions,
no partition schemes and no per-partition storage. The nearest equivalent is separate tables behind a
`UNION ALL` view, which carries none of the operational properties — partition switching, dropping an
aged partition — that make the feature worth having.
:::

## Serialization

See [JSON Serialization](/configuration/json).

## Resilience

See [Resiliency Policies](/configuration/retries) for `ConfigurePolly` and `ExtendPolly`.

## Session listeners

```cs
opts.Listeners.Add(new AuditListener());
```

Store-wide listeners run for every session. See [Session Listeners](/documents/listeners).

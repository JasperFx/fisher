# Fisher as Document DB

Fisher lets you use SQLite as a document database. Documents are stored as JSON text, with each
document type getting its own table (prefixed `fi_doc_`).

## Key Concepts

- **Documents** are plain .NET objects serialized to JSON
- **Sessions** provide a unit of work for batching changes
- **LINQ queries** translate to SQLite `json_extract` expressions
- **Automatic schema management** creates and migrates tables as needed

## Document Tables

Each document type gets a table. The core columns:

| Column | Type | Description |
| :--- | :--- | :--- |
| `id` | Varies | Primary key — Guid, string, int or long |
| `data` | TEXT | The serialized document, exactly as the serializer wrote it |
| `doc_type` | TEXT | Sub-class discriminator, for a [hierarchy](/documents/hierarchies) |
| `dotnet_type` | TEXT | Assembly-qualified .NET type name |
| `last_modified` | TEXT | ISO-8601 UTC, written by SQLite |
| `tenant_id` | TEXT | Present under [conjoined tenancy](/documents/multi-tenancy) |

Further columns appear when a feature asks for them: `guid_version` or `revision`
([concurrency](/documents/concurrency)), `is_deleted` / `deleted_at`
([soft delete](/documents/deletes)), the [opt-in metadata columns](/documents/metadata), and a
generated column per [duplicated field](/documents/indexing/duplicated-fields).

::: tip
A document table is created **on demand at first write**, as well as by the migration. A document
type can be stored without ever being registered, and a snapshot type is registered by projection
configuration — either way the first write may be the first time the table is needed.

The exception is an [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction),
where on-demand creation would deadlock against the caller's own write lock. There a missing table
throws by name.
:::

## Quick Example

```cs
// Define a document
public class User
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
}

// Store a document
await using var session = store.LightweightSession();
var user = new User { FirstName = "Jane", LastName = "Doe", Email = "jane@example.com" };
session.Store(user);
await session.SaveChangesAsync();

// Load by id
var loaded = await session.LoadAsync<User>(user.Id);

// Query with LINQ
var users = await session.Query<User>()
    .Where(x => x.LastName == "Doe")
    .ToListAsync();
```

## What is here

| Topic | |
| :--- | :--- |
| [Document Identity](/documents/identity) | Guid, string, int, long and strong-typed wrappers |
| [Opening Sessions](/documents/sessions) | Tracking modes, enlistment, `SessionOptions` |
| [Storing Documents](/documents/storing) | `Store`, `Insert`, `Update`, the unit of work |
| [Deleting Documents](/documents/deletes) | Hard and soft deletes, delete by criteria |
| [Querying](/documents/querying/) | LINQ, joins, grouping, paging, raw SQL, JSON reads |
| [Indexing](/documents/indexing/) | Duplicated fields, declared indexes, foreign keys |
| [Hierarchies](/documents/hierarchies) | A base type and its sub-classes in one table |
| [Concurrency](/documents/concurrency) | Guid versions or numeric revisions |
| [Patching](/documents/partial-updates-patching) | Changing part of a document without loading it |
| [Bulk Insert](/documents/bulk-insert) | Loading a lot of documents at once |
| [Metadata](/documents/metadata) | What Fisher records, and mapping it back onto members |
| [Multi-Tenancy](/documents/multi-tenancy) | Conjoined tenancy, and writing across tenants |

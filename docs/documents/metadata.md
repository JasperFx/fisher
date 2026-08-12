# Fisher Metadata

Fisher records metadata alongside every document. Some columns are always there; five are opt-in. All
of them can be projected back onto members of the document.

## The columns

| Column | Always? | What it holds |
| :--- | :--- | :--- |
| `last_modified` | yes | When the row was last written, in UTC |
| `dotnet_type` | yes | Assembly-qualified .NET type name |
| `doc_type` | hierarchy | The sub-class discriminator alias |
| `guid_version` | concurrency | The Guid version |
| `revision` | concurrency | The numeric revision |
| `is_deleted`, `deleted_at` | soft delete | |
| `created_at` | opt-in | When the row was first written |
| `correlation_id` | opt-in | The session's correlation id |
| `causation_id` | opt-in | The session's causation id |
| `last_modified_by` | opt-in | The session's `CurrentUserName` |
| `headers` | opt-in | The session's headers, as JSON |
| `tenant_id` | tenancy | |

## Enabling the opt-in columns

```cs
opts.Schema.For<Order>().Metadata(m =>
{
    m.CreatedAt.Enabled = true;
    m.CorrelationId.Enabled = true;
    m.CausationId.Enabled = true;
    m.LastModifiedBy.Enabled = true;
    m.Headers.Enabled = true;
});
```

They are filled from the session, so the same request that wrote an event and a document can be
identified from either:

```cs
session.CorrelationId = activity.RootId;
session.CausationId = activity.ParentId;
session.CurrentUserName = "jane";
session.SetHeader("region", "eu-west");
```

::: tip
`created_at` is filled by a column **DEFAULT**, not by a write binder. The upsert's `do update set`
clause assigns every column in the write list from `excluded.*`, so a `created_at` contributed by a
write binder would move forward on every save. Putting it in the DEFAULT means it is in no INSERT
column list and no set clause, and nothing has to remember why.
:::

::: tip
`tenant_id` is read-only for a different reason: it is part of the primary key and is bound inline
ahead of the binder loop, so a write binder would be a second writer of a value that already has one.
Enabling its metadata column creates nothing — `MultiTenanted()` does that — so it decides only
whether the value is projected back onto a member.
:::

::: warning
Only the opt-in columns have an `Enabled` flag, and enabling any of the others throws. Whether
`guid_version`, `revision`, `is_deleted` and `deleted_at` exist is already decided by
`UseOptimisticConcurrency()`, `UseNumericRevisions()` and `SoftDeleted()`, and `last_modified` is
always there — so a second flag over any of them would be a knob that silently did nothing.

Turning an enabled column back **off** also throws. A column is created by the migration, and
dropping one that may hold data is a migration rather than a configuration flag.
:::

## Mapping metadata onto document members

Every column is written either way; mapping decides whether the value comes back out. There are three
ways to say it, each overriding the one before.

### 1. The JasperFx metadata interfaces

```cs
public class User : ISoftDeleted
{
    public Guid Id { get; set; }
    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class Order : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
}
```

::: tip
`IVersioned` **turns optimistic concurrency on**, as on both siblings. That is not a liberty: with it
off the `guid_version` column is neither written nor read, so mapping a member onto it would mean
nothing. The converse does not hold — `UseOptimisticConcurrency()` alone maps nothing, because there
is no member named.
:::

The interfaces are resolved through the interface map, not by name, so an explicitly implemented
`ISoftDeleted.Deleted` is found — and a document is free to have a public `Deleted` of its own
meaning something else.

### 2. Attributes

```cs
public class Order
{
    public Guid Id { get; set; }

    [VersionMetadata] public Guid Version { get; set; }
    [LastModifiedMetadata] public DateTimeOffset UpdatedAt { get; set; }
    [CreatedAtMetadata] public DateTimeOffset CreatedAt { get; set; }
    [CorrelationIdMetadata] public string? CorrelationId { get; set; }
    [CausationIdMetadata] public string? CausationId { get; set; }
    [LastModifiedByMetadata] public string? UpdatedBy { get; set; }
    [HeadersMetadata] public Dictionary<string, object>? Headers { get; set; }
    [TenantIdMetadata] public string? TenantId { get; set; }
    [IsSoftDeletedMetadata] public bool Deleted { get; set; }
    [DeletedAtMetadata] public DateTimeOffset? DeletedAt { get; set; }
}
```

### 3. The DSL

```cs
opts.Schema.For<Order>().Metadata(m =>
{
    m.Version.MapTo(x => x.Version);
    m.LastModified.MapTo(x => x.UpdatedAt);
    m.CreatedAt.MapTo(x => x.CreatedAt);
});
```

::: tip
**Mapping an optional column enables it.** A mapping onto a column that would not exist is
configuration that silently does nothing.
:::

Two more things:

- **Adding a mapping widens the SELECT.** A binder joins the read list only when its column is
  mapped, because an unmapped one would cost a column per row to accomplish nothing.
- **A bad mapping is refused at configuration time**, with the column named — rather than throwing
  when the document's storage is first built, a long way from the line that caused it and in a
  message about expression trees.

::: warning
`dotnet_type` cannot be mapped. Weasel's binder for it takes no member where every other binder does,
so offering a mapping would silently do nothing. That is an upstream gap rather than a Fisher
decision.
:::

## Reading metadata without mapping it

```cs
var meta = await session.MetadataForAsync(document);
// or by id
var meta = await session.MetadataForAsync<User>(id);
```

`StoredDocumentMetadata` carries every column the type has. Every optional value is **nullable**,
where Polecat's equivalent requires `CreatedAt` — here null means "the column is not on this table",
and a default `DateTimeOffset` would be indistinguishable from a real one.

::: tip
`MetadataForAsync` deliberately **ignores the soft-delete filter** (the tenant term stays). A
soft-deleted row's metadata — including when it was deleted — is exactly what a caller asking about it
wants, and no ordinary load can answer it. It is one of only two places in Fisher where going around
the implicit filters is correct; the other is bulk insert's duplicate probe.
:::

::: tip
The returned type is `Fisher.Metadata.StoredDocumentMetadata`, not `DocumentMetadata` as on both
siblings, because Fisher already has a `DocumentMetadata` one namespace away doing the opposite job —
which columns are mapped onto which members. Two same-named types with opposite jobs is a collision
only noticed by whoever imports the wrong one.
:::

## Event metadata

Events carry their own. See [Event Metadata](/events/metadata).

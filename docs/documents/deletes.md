# Deleting Documents

## Deleting by document or id

```cs
session.Delete(document);
session.Delete<User>(id);        // Guid, string, int or long

await session.SaveChangesAsync();
```

## Deleting by criteria

```cs
session.DeleteWhere<User>(x => x.LastLogin < cutoff);
await session.SaveChangesAsync();
```

The predicate goes through the same parser as `Query<T>()`, so everything
[LINQ supports](/documents/querying/linq/operators) is available. The caller's predicate is applied
*last*, after the tenant scope and any guard, because a compound predicate is parenthesized and so
cannot swallow them.

## Soft deletes

A type is soft-deleted when it is marked `[SoftDeleted]`, implements `ISoftDeleted`, or is registered
for it:

```cs
opts.Schema.For<User>().SoftDeleted();
opts.Policies.AllDocumentsSoftDeleted();
```

```cs
[SoftDeleted]
public class User { /* … */ }

public class User : ISoftDeleted
{
    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```

The table gains `is_deleted` (INTEGER 0/1) and a nullable `deleted_at`, and `Delete` becomes an
`update … set is_deleted = 1`.

```cs
session.Delete(user);            // soft
session.HardDelete(user);        // really gone
session.HardDelete<User>(id);

session.DeleteWhere<User>(x => x.Inactive);       // soft
session.HardDeleteWhere<User>(x => x.Inactive);   // hard
session.UndoDeleteWhere<User>(x => x.Id == id);   // undelete
```

### Reading soft-deleted documents

Every ordinary read filters them out — `LoadAsync`, `LoadManyAsync`, `Query<T>()`, the JSON reads,
`CheckExistsAsync`, a patch. To see them, say so:

```cs
session.Query<User>().Where(x => x.MaybeDeleted());              // both
session.Query<User>().Where(x => x.IsDeleted());                 // only deleted
session.Query<User>().Where(x => x.DeletedSince(cutoff));
session.Query<User>().Where(x => x.DeletedBefore(cutoff));
```

::: tip
**The load SQL carries the filter, not the caller.** `LoadAsync` and `LoadManyAsync` are reached from
the session, the projection loader and the daemon alike, so a filter added by callers would present
as a deleted document coming back to life on whichever path forgot it. The LINQ side gets the same
filter from the same place — one source, so the query path cannot drift from the load path.
:::

### Undeleting

**Storing a soft-deleted document undeletes it**, and that falls out of the upsert rather than being
arranged: the live values are bound on every write and the `do update set` clause assigns every
column from `excluded.*`, so the insert branch and the update branch agree without either being
written twice.

### Guards on the deletion time

A soft delete guards on `is_deleted = 0`; an undelete guards on `is_deleted = 1`. Deleting an
already-deleted document must not push `deleted_at` forward, or `DeletedSince` answers about the most
recent call rather than about the deletion.

::: tip
Polecat has this guard on its by-id delete but not on `DeleteWhere`. Fisher has it on both.
:::

### Comparing deletion times

`DeletedSince` and `DeletedBefore` compare `deleted_at` **as text**, with none of the `strftime`
normalisation a document's own `DateTimeOffset` member needs. The column is Fisher's fixed-width UTC
format, chosen so that a string comparison *is* an instant comparison. A document member is whatever
System.Text.Json wrote, which is why that one needs the wrapper and this one does not.

::: warning
A soft-delete operator against a type that is **not** soft-deleted throws, in the query layer and in
`UndoDeleteWhere` alike. There is no column to answer from, so `IsDeleted()` would come back empty and
`MaybeDeleted()` complete — both of which look like real answers.
:::

## Metadata on a deleted row

`MetadataForAsync` is the one read that deliberately ignores the soft-delete filter: a soft-deleted
row's metadata — including *when* it was deleted — is exactly what a caller asking about it wants,
and no ordinary load can answer. See [Fisher Metadata](/documents/metadata).

## Deletes and cleaning

`Advanced.Clean.TruncateDocumentStorageAsync` stays a real `delete from` even for a soft-deleted
type. A rebuild's teardown clears rows rather than flagging them, or the replay would write onto rows
it cannot see. See [Tearing Down Document Storage](/schema/cleaning).

# Session Listeners

`IDocumentSessionListener` brackets the unit of work.

```cs
public class AuditListener : IDocumentSessionListener
{
    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        // Work queued here joins this transaction — documents *and* appended events
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        foreach (var inserted in commit.Inserted) { … }
        return Task.CompletedTask;
    }
}
```

Register store-wide or per session:

```cs
opts.Listeners.Add(new AuditListener());

var options = new SessionOptions();
options.Listeners.Add(new AuditListener());
```

## Only two methods are required

The two synchronous members — `DocumentLoaded` and `DocumentAddedForStorage` — are
**default-implemented**. That is both why a commit-only listener needs two methods, and why a listener
written against Polecat's two-member interface compiles here unaltered.

```cs
public class TrackingListener : IDocumentSessionListener
{
    public void DocumentLoaded(object id, object document) { … }
    public void DocumentAddedForStorage(object id, object document) { … }
    // …
}
```

::: tip
`DocumentLoaded` runs **per row**, so the composed listener list is built once and cached per session.
Keep the body cheap.
:::

## When the hooks fire, and what the database can see

The same seam the [outbox](/events/projections/side-effects) uses, with the same guarantees:

| Hook | Position | Visible to another connection? |
| :--- | :--- | :--- |
| `BeforeSaveChangesAsync` | Before the transaction opens, once per save | **no** |
| `AfterCommitAsync` | After the commit, outside the resilience pipeline | **yes** |

Hook *order* is not the invariant — both would fire in order even if both ran before the commit. What
is pinned is what the rest of the database can see when each runs.

::: warning
`AfterCommitAsync` runs **outside** the resilience pipeline, deliberately. A retried `SQLITE_BUSY`
re-executes the whole write delegate, so a hook invoked inside it would fire twice for a transaction
that had already committed.
:::

## The rules

- **An empty unit of work fires nothing**, as on Marten. Without that, every no-op `SaveChangesAsync`
  would run every store-wide listener.
- **An [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction) fires
  the before hook and not the after one.** "Everyone can see this now" is a claim only the caller's
  commit can make, and Fisher is not told when that happens.
- **Pending streams are collected *after* the before hook**, where Marten collects them before. It
  costs nothing, and it makes "work queued in the hook joins this transaction" true of appended events
  as well as of documents. A Marten listener that starts a stream is appending to the *next* unit of
  work.
- **The async daemon's projection batch does not fire session listeners.** A projection batch is the
  daemon's unit of work, not the application's; firing user listeners for it would run your
  `AfterCommitAsync` on the daemon's threads for every batch of every shard. JasperFx's
  `IDaemonChangeListener` is the hook for that side, and Fisher supports it.

## The store-agnostic contract

`JasperFx.Events.Documents.IDocumentCommitListener` is the shared spelling of the after-commit hook,
so a post-commit side effect can be written once and registered with Marten, Polecat or Fisher.
Fisher's `IDocumentSessionListener` **derives from it**, which makes both directions work:

```cs
// Outbound: a Fisher listener already IS a contract listener. Nothing to do.
IDocumentCommitListener asContract = new MyFisherListener();

// Inbound: a listener that implements only the shared contract, registered with AsSessionListener().
options.Listeners.Add(myCommitListener.AsSessionListener());
```

`ChangeSet` implements `IDocumentChangeSet` alongside `IChangeSet`, and `IDocumentDeletion` derives
from the contract's identically-named type — so nothing is copied or converted on the way through.

::: tip
There is **no second `CommitListeners` collection**. A contract listener is adapted onto
`IDocumentSessionListener` and joins the same `Listeners` list, so it runs in the same order, is
cached per session the same way, and obeys every rule below without exception.
:::

::: warning
`IDocumentChangeSet` declares `Inserted`, `Updated` and `Deleted` as `IReadOnlyList<>` where Fisher's
own `IChangeSet` says `IEnumerable<>`. That is the contract insisting on a *materialised* snapshot,
because on Marten the change set is the live unit of work and a lazy sequence taken out of one is
wrong by the time a listener reads it again. Fisher's is a snapshot either way.
:::

::: warning
**The rules below are Fisher's, and two of them are not shared.** An empty unit of work and an
enlisted session both fire nothing here; Marten fires unconditionally for the latter. The shared
contract permits both answers and deliberately asserts neither, so a listener that must hear about a
commit made under your own transaction has to check its store.
:::

## Joining a session you did not open: `AddSaveListener`

`Listeners` on `StoreOptions` and `SessionOptions` are read when the session is opened. A message bus
often joins a session the *application* opened — Wolverine's `Enroll` does — so it needs to register a
hook on a session that already exists. That is the contract's `AddSaveListener`, which takes JasperFx's
`IDocumentSaveListener` and runs it on every save of that session for as long as it lives:

<!-- snippet: sample_shared_session_hooks -->
<a id='snippet-sample_shared_session_hooks'></a>
```cs
await using var session = store.LightweightSession();

// Held as the contract, the way a message bus joins a session the application opened
IDocumentSessionOperations contract = session;

contract.AddSaveListener(new EnvelopeOutboxListener());

// Documents of mixed types, each stored as its own runtime type
contract.StoreObjects(documents);

await contract.SaveChangesAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/shared_session_hooks_samples.cs#L51-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shared_session_hooks' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A listener written against the contract, enlisting a
[shared participant](/documents/transaction-participants#the-store-agnostic-participant) from its before
hook, which is the shape an outbox has:

<!-- snippet: sample_shared_save_listener -->
<a id='snippet-sample_shared_save_listener'></a>
```cs
// Written once against JasperFx's contract, so the same class serves Marten, Polecat and Fisher
public class EnvelopeOutboxListener : IDocumentSaveListener
{
    public Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
    {
        // Outside the transaction, once per save. A participant enlisted here joins this commit.
        session.AddTransactionParticipant(new EnvelopeParticipant());
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
        CancellationToken token)
    {
        // After the commit, once, outside the resilience pipeline: the envelopes are durable now
        return Task.CompletedTask;
    }
}

public class EnvelopeParticipant : IDbTransactionParticipant
{
    public async Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken token)
    {
        // Write on the connection and transaction you are handed, never on a second connection
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into envelopes (body) values ('…')";
        await command.ExecuteNonQueryAsync(token);
    }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/shared_session_hooks_samples.cs#L14-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shared_save_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It follows every rule above: the before hook runs once per save and outside the transaction, the after
hook after the commit and outside the resilience pipeline, an empty unit of work fires neither, and an
enlisted session fires only the before hook. Two more:

- **It runs alongside the store-wide and `SessionOptions` listeners**, not instead of them: Fisher's own
  listeners' before hooks first, then the shared ones. After the commit the shared hooks run first, in
  the place the outbox's after hook always ran, so one of your own listeners throwing cannot stop an
  outbox from flushing. Each shared hook runs whatever an earlier one threw, and none sees your
  cancellation token, because the commit has already happened.
- **A listener added through a [tenant scope](/documents/multi-tenancy#writing-across-tenants) lands
  on the parent**, whose saves are the scope's.

::: warning
**A session taken from the async daemon's projection batch refuses `AddSaveListener`** with
`NotSupportedException`. The daemon commits that batch itself and never runs a session's save hooks, so
a listener accepted there would never fire, and an outbox that never flushes is message loss with a clean
build. The contract asks a store to refuse here for exactly that reason.
:::

## Reading what is about to be appended

`IChangeSet` describes a commit that has happened. Before one has, the streams a session has enlisted
are readable from the session itself — which is what a `BeforeSaveChangesAsync` hook deciding
something from the events it is bracketing needs:

```cs
foreach (var action in session.Events.PendingStreams)
{
    // action.Id / action.Key, action.ActionType, action.Events, action.TenantId
}
```

The same collection is on the shared `JasperFx.Events.Documents.IDocumentSessionOperations`, so a
listener written against the store-agnostic contract rather than against Fisher reads it without
naming Fisher:

```cs
IReadOnlyList<StreamAction> pending = ((IDocumentSessionOperations)session).PendingStreams;
```

The two answers are the same actions, with two differences worth knowing:

- **The contract's is a snapshot; `Events.PendingStreams` is a live view** of the session's tracking
  dictionary. A hook that appends while holding the latter sees it change underneath it.
- **The contract's includes every [tenant scope](/documents/multi-tenancy#writing-across-tenants)
  of the session**, because those commit in this same transaction — the question it answers is about
  the unit of work rather than about one tenant. Each action carries its own `TenantId`. Read from a
  scope, it reports that tenant alone.

::: tip
A stream is pending only until the commit clears it, and a session is reusable afterwards — so a
collection that still held committed actions would double-count them on the next read. Fisher's is
empty after `SaveChangesAsync`.
:::

## IChangeSet

```cs
public interface IChangeSet
{
    IEnumerable<object> Inserted { get; }
    IEnumerable<object> Updated { get; }
    IEnumerable<IDocumentDeletion> Deleted { get; }
    IEnumerable<IEvent> GetEvents();
    IEnumerable<StreamAction> GetStreams();
    IChangeSet Clone();
}
```

::: tip
`Deleted` is `IEnumerable<IDocumentDeletion>`, **not** `IEnumerable<IDeletion>`. `Weasel.Storage`'s
`IDeletion` is already in scope and is the storage *operation* that deletes, so a second `IDeletion`
one namespace away would be a collision only noticed by whoever imported the wrong one. The members
are unchanged, so a listener body ports; only a declaration naming the type has to be edited.
:::

::: tip
`Clone()` returns `this`. On Marten the change set *is* the live unit of work, which is reset after
every commit — so retaining one without cloning watches it empty out. Fisher builds it from the
operations snapshot the transaction wrote from, so it is immutable by construction. The member is
carried so a listener that clones out of habit still compiles.
:::

### Classification

A deletion is classified by testing `IDeletion` **before** its role, and that ordering is
load-bearing: every deletion carries the deletion role, *including the soft form whose statement is an
`UPDATE`* — so a role-first switch would route by-id deletions through the predicate branch and report
every one of them with a null id.

A predicate delete really does report a null id, because it named no row.

::: tip
A [patch](/documents/partial-updates-patching), a raw
[`QueueSqlCommand`](/documents/querying/raw-sql) and an `UndoDeleteWhere` appear in **no bucket**: none
of them carries a document, and inventing one would be worse than the omission. Marten is the same.
:::

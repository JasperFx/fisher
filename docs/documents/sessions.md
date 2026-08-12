# Opening Sessions

A session is a connection plus a unit of work. `IDocumentSession` reads and writes;
`IQuerySession` reads.

## The session factories

```cs
await using var session = store.LightweightSession();          // no tracking — the default
await using var session = store.IdentitySession();             // identity map
await using var session = store.DirtyTrackedSession();         // identity map + change detection
await using var session = store.OpenSession(sessionOptions);   // everything else

await using var query = store.QuerySession();
```

Each takes an optional tenant id:

```cs
await using var session = store.LightweightSession("acme");
```

::: tip
**`QuerySession()`'s narrowing is a convention, not a guarantee.** It is the same session narrowed to
the read interface, so a cast gets a write handle back. A genuinely query-only type would cost a
connection per scope to express a distinction the store does not make — so this is said out loud
rather than implied.
:::

::: tip
There is no `OpenSessionAsync` and no `LightweightSession(SessionOptions)`. Tracking is a property of
the options rather than a choice of constructor, so `OpenSession` already *is* the lightweight one;
and a session opens its connection lazily, so there is nothing to await.
:::

## Tracking modes

`SessionOptions.Tracking` decides what a session remembers.

| Mode | Behaviour |
| :--- | :--- |
| `None` | Nothing is remembered. The default. |
| `IdentityOnly` | An identity map: a document read twice comes back as the same instance. |
| `DirtyTracking` | The identity map, plus changed documents are written at commit without `Store`. |

```cs
await using var session = store.OpenSession(new SessionOptions
{
    Tracking = DocumentTracking.DirtyTracking
});
```

Things worth knowing about the map:

- **It covers `Query<T>()`, not just `LoadAsync`.** The LINQ provider resolves storage through the
  session, so a query under a tracking session builds a tracking selector without knowing it did.
  This matches Marten, whose documentation says the map applies to documents loaded "by Id **or Linq
  queries**".
- **Raw SQL bypasses it.** `AdvancedSql` resolves the query-only flavor directly, which is Marten's
  behaviour too and for a real reason: a raw query names its own columns and may select no identity
  at all.
- **Storing a second *instance* under a mapped id throws.** That is the safety property the map
  exists for: two instances of one document, both stored, is a last-write-wins outcome
  indistinguishable from a lost update. A type declaring `IEquatable<T>` is taken at its word and
  exempted. A lightweight session keeps no map and does not check.
- **`LoadManyAsync` preselects out of the map** and asks only for the ids it does not hold.

::: warning
`SessionOptions.Tracking` defaults to `None`, where Marten's `OpenSession(SessionOptions)` defaults
to its identity map. Marten's default predates its own `LightweightSession()`; following it would
silently give every existing `OpenSession` caller a map they did not ask for — *and* that throw.
:::

## Ejecting

```cs
session.Eject(document);         // by reference
session.EjectAllOfType(typeof(User));
session.EjectAllPendingChanges();
```

`Eject` matches **by reference**, so ejecting one instance leaves a queued write made with a
different instance alone — the distinction the map exists to make.

`EjectAllPendingChanges` keeps the identity map and clears the change trackers, which looks
inconsistent and is not: a tracker is a queued write that has not been asked for yet. Pending
[DCB boundaries](/events/dcb) go too, since a boundary guards appends that are being dropped.

::: warning
The identity map and the tracker list are **unguarded**. That is safe because a tracking mode is only
chosen by whoever opens the session, and the one caller that drives a session from several threads —
the async daemon — opens lightweight sessions everywhere. Do not hand a tracked session to several
threads.
:::

## SessionOptions

```cs
var options = new SessionOptions
{
    TenantId = "acme",
    Tracking = DocumentTracking.IdentityOnly,
    Timeout = 5,                                 // seconds; see below
    IsolationLevel = IsolationLevel.Serializable
};

options.Listeners.Add(new AuditListener());

await using var session = store.OpenSession(options);
```

There are also three static factories:

```cs
SessionOptions.ForTenant("acme");
SessionOptions.ForConnection(myConnection);
SessionOptions.ForTransaction(myTransaction);
```

### IsolationLevel

Carried for parity, and it refuses exactly one value. `Unspecified`, `ReadCommitted`,
`RepeatableRead` and `Serializable` all produce the same `BEGIN IMMEDIATE`; `Chaos` and `Snapshot`
are refused by the provider.

::: danger
**`ReadUncommitted` is refused by Fisher.** It is the one value that begins a *deferred* transaction,
and nothing would signal the loss because the transaction still describes itself as `Serializable`.
:::

### Timeout

`SessionOptions.Timeout` means something different here than on either sibling, in two ways. It
bounds how long a **statement** waits for the write lock before `SQLITE_BUSY`; it does not interrupt
a query that is genuinely slow. And it does **not** bound the wait at `BEGIN IMMEDIATE` — that comes
from the connection string's `Default Timeout`, 30 seconds by default. See
[Timeouts, and which knob does what](/configuration/sqlite#timeouts-and-which-knob-does-what).

## Enlisting in your own connection or transaction

**This is worth more on Fisher than the same feature is on either sibling.** An application using
Fisher keeps its own tables in the *same file*, and SQLite permits one writer per file — so without a
way to hand Fisher an open transaction, "my rows and Fisher's, or neither" means taking the write
lock twice and contending with yourself.

There are three modes, with one rule each:

| You supply | Fisher's rule |
| :--- | :--- |
| Nothing | The ordinary session. Fisher opens and commits its own transaction, and disposes its own connection. |
| `Connection` | Fisher opens and commits its own transaction on it, and **never disposes a connection it did not open**. |
| `Transaction` | Fisher **neither commits nor rolls back**. |

```cs
await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync();
await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

await using (var session = store.OpenSession(SessionOptions.ForTransaction(tx)))
{
    session.Store(new User { /* … */ });
    await session.SaveChangesAsync();   // flushes into *your* transaction
}

await myOwnCommand.ExecuteNonQueryAsync();   // your rows, same transaction
await tx.CommitAsync();                      // both, or neither
```

::: tip
Marten's `OwnsConnectionLifecycle` / `OwnsTransactionLifecycle` pair is deliberately absent — four
combinations of which two are traps, in place of two rules that are always true.
:::

### What changes in an enlisted session

Five things, each of which would be silently wrong the other way:

- **No resilience pipeline.** An ordinary commit can be retried after `SQLITE_BUSY` because the
  failed attempt's transaction rolled back with it. An enlisted one did not — it is yours and still
  open — so a retry would write everything the first attempt wrote a second time. The busy surfaces
  to you instead.
- **No post-commit step.** An outbox's `AfterCommitAsync` and the append observer both claim
  "everyone can see this now", and Fisher is not told when you commit. Neither fires;
  `BeforeCommitAsync` does, as the last thing the session writes.
- **Document tables are not created on demand.** That path runs a migration on its own connection,
  which would block against the write lock your transaction is holding — a session deadlocking
  against itself, presenting after thirty seconds as `database is locked`. A missing table throws by
  name instead. The existence check runs on *your* connection, so a table created inside the same
  transaction counts.
- **No `ITransactionParticipant.AfterCommitAsync`**, for the same reason.
- **A deferred caller transaction weakens the append guard**, and Fisher cannot warn about it. SQLite
  still refuses the second writer, so there is no lost update; what changes is that the loser gets
  `SQLITE_BUSY` at first write rather than a clean concurrency failure. The provider reports
  `Serializable` for a deferred transaction and an immediate one alike, so the two are
  indistinguishable from outside — this documentation is the only instrument available. Begin your
  transaction with `IsolationLevel.Serializable`.

### The other direction

If what you have is a "save" method rather than a connection to borrow —
`DbContext.SaveChangesAsync()`, say — enlist it in *Fisher's* transaction instead. See
[Transaction Participants](/documents/transaction-participants).

## Session metadata

```cs
session.CorrelationId = "…";
session.CausationId = "…";
session.CurrentUserName = "jane";
session.SetHeader("tenant-region", "eu-west");
```

These reach appended events (each gated on its `Enable*` option) and, if you enable the matching
columns, stored documents. The session seeds correlation and causation from `Activity.Current` at
construction, so tracing context reaches events with no application code; an explicit assignment
afterwards wins.

## Disposal

```cs
await using var session = store.LightweightSession();
```

Sessions implement both `IDisposable` and `IAsyncDisposable`. Prefer `await using`, but the
synchronous form works — which matters, because a `ServiceProvider` disposed synchronously refuses
outright to dispose a service offering only the async form.

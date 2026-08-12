# SQLite and PRAGMA Settings

This page collects the SQLite-level behaviour worth understanding before you go to production — the
things that have no analogue on Marten or Polecat.

## Connections come from the data source

Fisher opens every connection through Weasel's `SqliteDataSource`, reached from
`FisherDatabase.OpenConnectionAsync`. Production code never does `new SqliteConnection(...)`, and
neither should yours if you are working inside the store: the data source is what applies the PRAGMA
settings, and — for an in-memory database — what holds the database alive.

## PRAGMA settings

`StoreOptions.PragmaSettings` is Weasel's `SqlitePragmaSettings`. It defaults to
`SqlitePragmaSettings.Default` — WAL, `synchronous = NORMAL`, foreign keys on, a 5 second busy
timeout, a 64 MB cache and a 256 MB memory map.

```cs
// A named profile
opts.PragmaSettings = SqlitePragmaSettings.HighSafety;   // or .HighPerformance, or .Default

// Or piece by piece
opts.PragmaSettings = new SqlitePragmaSettings
{
    JournalMode = JournalMode.WAL,
    Synchronous = SynchronousMode.FULL,
    ForeignKeys = true,
    BusyTimeout = 10_000,        // milliseconds
    CacheSize = -64_000          // negative is KiB, so this is 64 MB
};
```

| Profile | Trade |
| :--- | :--- |
| `Default` | WAL + `synchronous = NORMAL`. The general-purpose choice. |
| `HighPerformance` | `synchronous = OFF` — faster, with a corruption risk on power loss. |
| `HighSafety` | `synchronous = FULL`, secure delete, a longer busy timeout. |

They are applied per connection, which is what makes them work per database file under
[database-per-tenant](/configuration/multitenancy#database-per-tenant) with no extra machinery.

### WAL

[Write-ahead logging](https://sqlite.org/wal.html) is on by default and it is load-bearing: **WAL is
what lets the async daemon read while a session writes.**

If you turn it off, Fisher logs a warning at daemon startup rather than refusing to run. A non-WAL
store projects correctly; it just serialises the daemon and every writer against each other, which
presents as a slow projection rather than as a misconfiguration. The
[health check](/documents/aspnetcore#health-check) is how an operator finds out the warning mattered.

### Foreign keys

Enforcement is per-connection in SQLite and **off by default in the library** — but on for every
connection Fisher opens, because Weasel's default profile sets it. That is why
[document foreign keys](/documents/indexing/foreign-keys) bite the moment they are declared, and why
the order of deletes matters when clearing event data.

## One writer per file

SQLite permits one writer per database file. Everything below follows from that.

### The append lock

Fisher's append path takes the write lock with `BEGIN IMMEDIATE` (`IsolationLevel.Serializable`),
where Marten takes an advisory lock and Polecat an `UPDLOCK, HOLDLOCK` row lock.

The consequence is visible in one place. `AppendExclusive`, `FetchForExclusiveWriting` and
`WriteExclusivelyToAggregate` are the *optimistic* methods here. On the siblings a competing session
**waits** its turn; on Fisher there is no row lock to wait on, so the loser **fails** with
`EventStreamUnexpectedMaxEventIdException`. The safety property is unchanged — the version guard
still runs inside the write transaction, so there is no lost update.

Matching the siblings would mean holding a `BEGIN IMMEDIATE` open from the fetch until
`SaveChangesAsync`, blocking every other writer in the process for as long as the caller holds the
session. That is a worse trade for an embedded database.

### Busy retries

A contended write surfaces as `SQLITE_BUSY` or `SQLITE_LOCKED`, and Fisher retries it through a real
Polly pipeline. See [Resiliency Policies](/configuration/retries) — including the paths that
deliberately run *outside* the pipeline, because retrying them would double-write.

### Timeouts, and which knob does what

This trips people up, so it is worth being precise. Three different things bound three different
waits:

| Setting | What it bounds |
| :--- | :--- |
| `SessionOptions.Timeout` / `opts.CommandTimeout` | How long a **statement** waits for the write lock. |
| `PRAGMA busy_timeout` | The engine's own busy handler for statements. |
| Connection string `Default Timeout` | The wait at `BEGIN IMMEDIATE`, and connection open. |

`SessionOptions.Timeout` does **not** bound the wait at `BEGIN IMMEDIATE`, because the transaction is
begun on the connection rather than through a command — that wait comes from the connection string's
`Default Timeout`, 30 seconds by default. And `Default Timeout=0` means *no limit*, not "do not
wait".

Neither of them interrupts a query that is genuinely slow. They bound lock contention, not work.

### Isolation levels

`SessionOptions.IsolationLevel` is carried for parity and refuses exactly one value. Verified against
Microsoft.Data.Sqlite: `Unspecified`, `ReadCommitted`, `RepeatableRead` and `Serializable` all
produce the same `BEGIN IMMEDIATE` and all report `Serializable` back; `Chaos` and `Snapshot` are
refused by the provider.

::: danger
**`ReadUncommitted` is refused by Fisher.** It is the one value that begins a *deferred* transaction,
which weakens the append guard — and nothing would signal the loss, because the transaction still
describes itself as `Serializable`.
:::

So Polecat code setting `ReadCommitted` (its default) ports across and behaves identically.

## Releasing pooled connections

Microsoft.Data.Sqlite keeps a pooled connection per connection string, worth three file handles
(`.db`, `-wal`, `-shm`), in a **process-wide** registry. Disposing a Fisher store now releases its
own pool.

```cs
await store.DisposeAsync();          // releases this store's pooled connections
await tenancy.ForgetTenantAsync(id); // releases one tenant's
```

::: danger
**Never call `SqliteConnection.ClearAllPools()`.** It disposes every pooled connection in the
process, so one store's cleanup takes out another's — and in a parallel test suite that presents as
an intermittent `ObjectDisposedException`. Fisher only ever uses the targeted
`ClearPool(connection)` form, which names one connection string.
:::

A connection currently checked out is unharmed: it goes on reading and writing after its pool is
cleared, and is discarded rather than re-pooled when it closes. That is what makes forgetting a
tenant safe while a session is mid-request.

::: tip
There is no idle eviction, deliberately. A timer cannot tell a tenant that is finished from one that
is merely quiet, re-resolving one is nearly free, and a resolved-but-unused tenant costs no
measurable memory and no file handles at all. `ForgetTenantAsync` leaves the judgement with the
caller who actually knows.
:::

## Guids are lowercase canonical TEXT

SQLite has no native Guid type. Fisher stores one as **lowercase canonical text**, and SQLite's
default collation is case-sensitive — so a Guid bound the wrong way matches nothing, silently.

Every write path Fisher owns converts explicitly. The place you can meet this yourself is
[raw SQL](/documents/querying/raw-sql), where a caller's value reaches a parameter with no conversion
in between; Fisher converts there too, along with `DateTimeOffset` and `decimal`, all three of which
Microsoft.Data.Sqlite binds to something Fisher never wrote.

## Timestamps are fixed-width UTC TEXT

`fi_events.timestamp`, `last_modified`, `deleted_at` and friends hold ISO-8601 text in a fixed-width
UTC form, chosen precisely so that a **string comparison is an instant comparison**. That is why the
soft-delete and metadata range operators compare as text with no `strftime` wrapper.

A `DateTimeOffset` *inside a document* is different: it is whatever System.Text.Json wrote, with
trimmed fractional zeros and the original offset, which is not order-preserving. That one is compared
through SQLite's date parser instead — see [Supported LINQ Operators](/documents/querying/linq/operators#timestamps).

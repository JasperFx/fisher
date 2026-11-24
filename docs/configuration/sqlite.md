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
`WriteExclusivelyToAggregate` take that lock before they read and hold it until the session commits, so a competing
exclusive writer waits its turn, as on the siblings. But there is no row lock to take, so it is the **file's** lock:
while one session holds it, every other writer on the file waits, whatever stream it writes to. Readers carry on under
WAL. See [exclusive writes](/events/appending#exclusive-writes).

### The busy handler

`PRAGMA busy_timeout` installs SQLite's default busy handler, which sleeps 1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50 and
then 100 ms between tries for the write lock. A waiting writer can sleep long after the lock was
released, and with several writers on one file that sleep was nearly all of the cost
([fisher#547](https://github.com/JasperFx/fisher/issues/547)).

So Fisher 2.0 replaces it on every connection it opens with a handler that sleeps a fixed 1 ms
between tries. The deadline is still `BusyTimeout`, and the write still fails with `SQLITE_BUSY` once
it passes, so nothing changes for callers. At 8 to 16 concurrent writers it measured 2 to 3 times the
throughput of the default handler. A `BusyTimeout` of zero or less is left alone, and so is a
connection you pass to a session yourself.

### Busy retries

A contended write surfaces as `SQLITE_BUSY` or `SQLITE_LOCKED`, and Fisher retries it through a real
Polly pipeline. See [Resiliency Policies](/configuration/retries) — including the paths that
deliberately run *outside* the pipeline, because retrying them would double-write.

### Timeouts, and which knob does what

This trips people up, so it is worth being precise. A writer that finds the lock held waits in two
layers, one after the other:

1. **SQLite's busy handler** waits first, for every statement, `BEGIN IMMEDIATE` included. Its deadline
   is `PRAGMA busy_timeout` (Weasel's `BusyTimeout`, 5 seconds by default). This is where nearly all
   contended waits happen and end.
2. **Microsoft.Data.Sqlite's own retry loop** only starts once the busy handler gives up. It retries the
   statement synchronously, about every 150 ms, until the command's timeout passes.

| Setting | What it bounds |
| :--- | :--- |
| `PRAGMA busy_timeout` | The busy handler: the first wait, for every statement and for `BEGIN IMMEDIATE`. |
| `SessionOptions.Timeout` / `opts.CommandTimeout` | Microsoft.Data.Sqlite's retry for a **statement**, after the busy handler gives up. |
| Connection string `Default Timeout` | Microsoft.Data.Sqlite's retry for `BEGIN IMMEDIATE`, after the busy handler gives up, and connection open. |

`SessionOptions.Timeout` does **not** bound the retry at `BEGIN IMMEDIATE`, because the transaction is
begun on the connection rather than through a command. That retry is bounded by the connection string's
`Default Timeout`, 30 seconds by default. And `Default Timeout=0` means *no limit*, not "do not
wait".

None of them interrupts a query that is genuinely slow. They bound lock contention, not work.

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

## `decimal` is a JSON number, and it compares as one

SQLite has no decimal type. A `decimal` member is stored as an ordinary JSON number, and
`json_extract` hands it back as REAL, which holds about fifteen significant digits. A duplicated
`decimal` field is declared REAL for the same reason.

Fisher does not let that REAL decide anything a decimal would decide differently. A `Select`, a
`Where` (`==`, the ranges, `IsOneOf`, `Contains`), an `OrderBy`, a keyset cursor, `Distinct`,
`DistinctBy`, a `GroupBy` key, a join key and an event-body query over a `decimal` member all read the
number as the text System.Text.Json wrote and compare it as a `decimal`, through functions and a
collation Fisher registers on every connection. So `0.1000000000000000001m` and
`0.1000000000000000002m` are two values, as they are in C#. A comparison still uses the REAL as a coarse
range first, so an index on the member — a duplicated field or `Index(x => x.Amount)` — still serves
`==`, the ranges and `IsOneOf`. An `OrderBy` over a `decimal` sorts by an exact key instead, so it is no
longer served by such an index.

The aggregates are exact too. `Sum`, `Average`, `Min` and `Max` over a `decimal`
member — `SumAsync` and its siblings, a grouped `Select`, a `HAVING` and a grouped `OrderBy` — read the
number as the text System.Text.Json wrote and compute in `decimal`. So
`GroupBy(x => x.Category).Where(g => g.Sum(x => x.Amount) == 0.3m)` finds the
group holding `0.1m` and `0.2m`, which SQLite's own `sum()` would put at `0.30000000000000004`.
`AverageAsync` still returns `double`; it is the exact decimal mean, rounded once.

You do not have to do anything about this. Fisher normalises the comparison value wherever a query
binds one, so `Where(x => x.Total > 100m)`, `IsOneOf`, `Contains`, `HAVING` and arithmetic on a
`decimal` member all compare numerically.

Arithmetic is the exception to "a floating-point comparison is close enough": SQLite computes
`0.3 - 0.1` as `0.19999999999999998`, so the subtraction itself would change which rows match. Fisher
therefore folds a `decimal` member plus or minus constants into one constant computed in `decimal`
(`x.Total - 10m > 90m` is sent as `total > 100`), and refuses every other `decimal` or `float`
arithmetic shape, and any non-integral `%`, by name.

::: warning
Do not reach around it. Microsoft.Data.Sqlite binds a raw `decimal` parameter as **TEXT**, and SQLite
orders every numeric value below every TEXT one — so a hand-written comparison against
`json_extract(data, '$.total')` with an unconverted `decimal` matches **nothing** for `>` and `=`,
and **everything** for `<`, with no error either way. That is what
[raw SQL](/documents/querying/raw-sql) converts for you, and it is worth knowing if you ever build a
statement outside Fisher.

A *declared column* is rescued by SQLite's affinity rules, which is why a duplicated `decimal` field
behaves correctly even when bound raw. There is no affinity inside `json_extract`, so an undeclared
member is not.
:::

## The native SQLite build

Fisher runs on the `SQLitePCLRaw.bundle_e_sqlite3` 3.0.x bundle that `Microsoft.Data.Sqlite` brings. It does
not swap in SQLite3MC (an encrypted build) and does not cap `Microsoft.Data.Sqlite`; an application that
wants encryption at rest can reference `SQLite3MC.PCLRaw.provider` itself, since it is built on the same
SQLitePCLRaw 3.0.x line. Fisher needs SQLite 3.44 or newer for the json1 functions it relies on, and its
test suite asserts that floor against the bundle it runs on.

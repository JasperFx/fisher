# Why Fisher?

Fisher is a document database and event store on SQLite. The reasonable first reaction is that
SQLite is the *small* option — a fallback for when you cannot have a real database. This page argues
the opposite: for a large class of .NET applications SQLite is the *right* option, and several
things that are compromises on PostgreSQL or SQL Server are advantages here.

## There is no server

A Fisher store is a file. That single fact removes a category of work:

- **Nothing to provision.** No container, no connection pooling to a remote host, no credentials, no
  network partition to reason about.
- **Nothing to keep running.** The database's lifetime is the process's.
- **Backup is `cp`.** Restore is `cp` the other way. A tenant's entire data set is one file you can
  hand to somebody.
- **Tests need no fixture.** Fisher's own test suite creates throwaway SQLite files; there is no
  Docker Compose file in this repository for a database, because there is no database to compose.

For a desktop application, a CLI, an edge or on-premises deployment, a single-node service, or an
integration test suite, that is not a lesser story than a database server. It is a much better one.

## The round trip you are not paying for

A document database's design is shaped by the cost of talking to a server. Marten and Polecat both
spend real effort collapsing round trips: batched queries exist to send several reads at once,
`SelectMany` and child-collection querying exist partly so you do not fetch documents you will
discard, and the JSON-returning reads exist to skip a serializer pass on data that already crossed a
wire.

In Fisher the database *is* the caller's process. That changes what those features are worth:

- **[JSON-returning reads](/documents/querying/query-json) are worth more, not less.** On a server
  store, skipping deserialize-then-reserialize saves a fraction of the total cost. Here the round
  trip *is* the cost. An endpoint that reads a document and writes it to a response goes from "parse
  JSON, build an object, serialize an object" to "copy bytes" — and the bytes are byte-exact, because
  `data` is TEXT holding exactly what the serializer wrote. Neither `jsonb` nor `nvarchar` can
  promise that.
- **[Joins](/documents/querying/linq/joins) are worth more, not less.** The usual argument against
  joins in a document store is that a second round trip is cheap next to a join's cost. There is no
  round trip to be cheap. And on SQLite a join between two document tables is the plainest SQL in
  this codebase — no `OPENJSON`, no lateral join, and an [expression index](/documents/indexing/indexes)
  usable on either side.
- **[Batched queries](/documents/querying/batched-queries) are worth *less*, and Fisher says so.**
  They exist for API parity, so DCB and document code ports between the stores unchanged. The one
  property that survives is ordering. They are not presented as a performance feature.

## One writer per file

This is SQLite's central constraint and the honest counterweight to everything above. One database
file permits one writer at a time. Fisher does not hide that; it builds on it.

**What it costs.** The unit of work is strictly sequential. Two processes writing one file contend.
A long transaction blocks every other writer for its duration, which is why
[bulk insert](/documents/bulk-insert) treats its batch size as a ceiling on lock hold time rather
than as a throughput knob.

**What it buys.** Committed event sequence numbers are contiguous, because a transaction's sequences
commit before the next writer allocates any — so the async daemon's high-water mark is simply
`max(seq_id)` with no gap-skipping to reason about. And
[cross-tenant writes](/documents/multi-tenancy#writing-across-tenants) fall out for free: one
`SaveChangesAsync` writes several tenants' rows in one transaction, where a database-per-tenant store
would need a distributed transaction to match it.

**What it makes essential.** Two features that are conveniences elsewhere are load-bearing here,
because an application using Fisher keeps its own tables in the same file:

- [`ITransactionParticipant`](/documents/transaction-participants) lets something else — EF Core,
  Dapper, hand-written ADO.NET — write on *Fisher's* connection inside *Fisher's* transaction.
- [`QueueSqlCommand`](/documents/querying/raw-sql) and
  [session enlistment](/documents/sessions#enlisting-in-your-own-connection-or-transaction) answer
  the same problem from the other direction.

Without them, "my rows and Fisher's, or neither" means taking the write lock twice and contending
with yourself.

**And the way out.** If you need concurrent writers, you split across files — which is exactly what
[database-per-tenant](/configuration/multitenancy#database-per-tenant) and
[multiple stores](/configuration/multiple-stores) do. On PostgreSQL, database-per-tenant is a
heavyweight provisioning decision; here a tenant is a file, so it is cheap enough to do on first use,
and N tenants write concurrently instead of queueing behind one lock. That makes it a performance
feature, which is not true on either sibling.

## What SQLite turns out to be good at

Several features were expected to be the hard ones and were not:

- **[Patching](/documents/partial-updates-patching)** is the strongest case in the whole library.
  json1 is built in — no server function to install, unlike Marten — and it composes, so a chain of
  operations is one statement. And because Fisher's
  [duplicated fields](/documents/indexing/duplicated-fields) are `VIRTUAL` generated columns, a patch
  has nothing to refresh; both siblings must update theirs inside the patch SQL.
- **Duplicated fields cost index space and nothing else.** They are computed from `data` on read, so
  they cannot drift, they need no backfill when added to a table that already has rows, and the write
  path is untouched.
- **[Declared indexes](/documents/indexing/indexes) add no column at all.** SQLite has indexed
  expressions, so a member is indexed where it lives. Marten needs a computed index and Polecat a
  `JSON_VALUE` index; both materialise something first.
- **[Foreign keys between documents](/documents/indexing/foreign-keys) are real and enforced**,
  cascade included, over a generated column — which was the one thing genuinely uncertain before it
  was probed.

## Where Fisher fits

Reach for Fisher when the database being a separate machine is a cost rather than a benefit:
single-node services, desktop and CLI applications, edge and on-premises deployments, embedded
reporting, and — very commonly — integration test suites for code that will run on Marten or Polecat
in production, since the API is the same one.

Reach for [Marten](https://martendb.io) or [Polecat](https://polecat.jasperfx.net) when you need many
concurrent writers against one data set, a database that outlives any process, or the operational
tooling that comes with a real server.

The API being shared means that is a deployment decision more than a rewrite.

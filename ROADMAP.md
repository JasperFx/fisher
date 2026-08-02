# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of `42834ba`. 34 tests green on net9.0 and net10.0.

## The destination

**First round of JasperFx compliance tests passing.** `JasperFx.Events.ComplianceTests` is the
shared cross-store suite that Marten and Polecat both enroll in; passing it is what makes Fisher a
real Critter Stack event store rather than a lookalike. Everything below is ordered by what that
needs.

## Done

| Milestone | Notes |
|---|---|
| Solution + build infrastructure | net9.0/net10.0, CPM, xUnit v3 on MTP, CI |
| `fi_` schema | `fi_streams`, `fi_events`, `fi_event_progression` via Weasel.Sqlite |
| SQLite dialects over Weasel.Storage | `SqliteStorageDialect<TId>`, `SqliteEventStoreDialect` |
| Sessions + append | `DocumentStore`, `FisherSession` UoW, `EventOperations`, `AppendPlanner` |
| Event store reads | `FetchStreamAsync`, `FetchStreamStateAsync`, `LoadAsync`, archive/un-archive |

## Next, in order

### 1. Live aggregation — `AggregateStreamAsync`

**Start here.** It is the only projection-shaped feature that does *not* need document storage,
because it folds events in memory and returns the aggregate rather than persisting it. That makes
it the cheapest way to get the JasperFx aggregation machinery wired up and proven.

Needs `EventGraph` to implement `IAggregationSourceFactory<IQuerySession>` (Polecat's is the
template — see its `EventGraph.Build<TDoc>()`), which in turn wants `SingleStreamProjection<TDoc,
TId>` and therefore an id-type resolution story. Polecat resolves it through `DocumentMapping`;
Fisher has no `DocumentMapping` yet, so either a minimal id-resolver lands here or this waits on
step 2. **Resolving that is the first decision to make.**

### 2. Document storage

`IStorageSession.StorageFor`, `IStorageDatabase.Providers` and `FisherDatabase.SequenceFor` all
throw `NotImplementedException` today. Needs `DocumentMapping`, a `DocumentProviderRegistry` behind
`IProviderGraph`, the closed-shape document storages, `fi_doc_*` tables, and Store/Insert/Load/
Delete.

Prefer Weasel.Storage's closed-shape storages over hand-written SQL — the whole point of the
dialect layer is that this should mostly be configuration. Polecat's
`SqlServerDocumentStorageDescriptorBuilder` is the shape to mirror, minus the SQL Server type
mapping.

### 3. Projections

`ProjectionGraph<IProjection, IDocumentSession, IQuerySession>` — needs `IProjection`,
`StoreOptions.Projections`, the projection storage seam, and inline snapshot application during
`SaveChangesAsync`.

**Steps 2 and 3 are entangled, not sequential.** `Projections.Snapshot<T>` needs somewhere to write
the snapshot, which is document storage. Expect to interleave them rather than finishing one first.

### 4. Async daemon

`FisherDatabase` must implement `IEventDatabase`. Needs high-water detection over `fi_events`,
event loading/paging, and `BuildProjectionDaemonAsync`.

Two SQLite-specific things to think about up front:
- The high-water mark assumes `seq_id` only moves forward. `AUTOINCREMENT` is what guarantees that
  (see CLAUDE.md) — do not weaken it.
- WAL journaling is what lets the daemon read while a session writes. It is on by default via
  `SqlitePragmaSettings.Default`, but a consumer overriding `StoreOptions.PragmaSettings` could turn
  it off and quietly serialize the daemon behind every write.

### 5. Enroll in the compliance suites

Flip `$(EnableComplianceTests)` in `Fisher.Tests.csproj` and add `Compliance/`.

Three global aliases the source-only suites bind against:

```
ComplianceQuerySession    -> Fisher.IQuerySession                (exists)
ComplianceOperations      -> Fisher.IDocumentSession             (exists)
ComplianceEventProjection -> Fisher's EventProjection base type  (step 3)
```

`EventStoreComplianceFixture<TOperations, TQuerySession>` members, against current state:

| Member | Blocked on |
|---|---|
| `OpenSession`, `SaveChangesAsync`, `EventsFor`, `Registry` | — ready |
| `BuildStoreAsync` | — ready (apply schema explicitly, as Polecat does) |
| `LoadDocumentAsync`, `StoreDocument` | document storage (2) |
| `EventStore`, `AllAggregateTypes` | projections (3) |
| `CleanEventDataAsync` | needs `Advanced.Clean` |
| `StartDaemonAsync`, `WaitForNonStaleProjectionDataAsync` | daemon (4) |
| `CreateBatch` | batched queries + DCB tags |

Suites are independently enrollable, so they can go green one at a time rather than all at once.
`AutoDiscoveredAggregateCompliance` and `SelfAggregatingEvolveCompliance` are the likely first two;
`DcbTagQueryAndConsistencyCompliance` (727 lines, needs tag tables) is the last.

## Open items not on the critical path

- **Delete `FisherCommandBuilder`** once a Weasel.Sqlite release carries
  [weasel#424](https://github.com/JasperFx/weasel/pull/424) and switch back to
  `Weasel.Sqlite.CommandBuilder`. Check the PR's status before assuming the shim is still needed.
- **Concurrency regression test.** The append path's safety rests on `BEGIN IMMEDIATE` being what
  `IsolationLevel.Serializable` produces — verified empirically against Microsoft.Data.Sqlite 10.0.9,
  but it is library behaviour Fisher does not own. A test appending from two concurrent sessions and
  asserting one fails cleanly would catch a regression under a provider bump.
- **`TombstoneStreamOperation` is unreachable.** Written into the dialect, no caller. Archive/
  un-archive got wired up and tested; tombstone still needs a session-facing API.
- **Not started at all:** DCB tags, multi-tenancy beyond a tenant id column, subscriptions, DI
  registration (`AddFisher`), LINQ, bulk insert, `FetchForWriting`.

## Things not to rediscover the hard way

All in CLAUDE.md, repeated here because each one cost real time:

- A non-literal column `DEFAULT` must be parenthesized.
- `AUTOINCREMENT` on `seq_id` is load-bearing, not decorative.
- Constraint-violation mapping needs the *extended* SQLite result code.
- Guids and timestamps convert explicitly in **both** directions — never rely on provider coercion.
- `dotnet test` cannot emit TRX under MTP; CI runs the test executable directly.

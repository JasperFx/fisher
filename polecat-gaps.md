# Polecat Features Not Yet in Fisher

> ⚠️ **This is a Polecat comparison, not a Marten parity statement, and it is dated.** Everything
> below was established against Polecat on 2026-08-08, on JasperFx 2.45.0. **A feature Marten has and
> Polecat does not is invisible to it** — `Include()`, full-text search, compiled queries and
> `IMartenLogger` are all absent from Fisher *and* absent from this file, because Polecat does not have
> them either. Do not read a clean row here as parity with Marten. The Marten-facing list lives in
> [the migration guide](docs/migration-guide.md#marten-features-fisher-does-not-have) and is the one to
> keep current; the README points at it.

Everything [Polecat](https://github.com/JasperFx/polecat) (SQL Server) has that Fisher (SQLite) does
not, as of 2026-08-08, on JasperFx 2.45.0. Polecat is the comparison rather than Marten because
Fisher mirrors Polecat's internals by design — CLAUDE.md's rule is "mirror Marten's public API surface
where it costs nothing; mirror Polecat's internals where the concern is not dialect-specific" — so a
divergence from Polecat is either a deliberate SQLite decision or a gap.

**This file is an index, not the tracking.** Every row below is a filed issue; see ROADMAP.md's rule
that a note in a document is context rather than tracking. Nothing here is a to-do that exists only
here.

The scoreboard: 29 issues, [#22](https://github.com/JasperFx/fisher/issues/22) through
[#50](https://github.com/JasperFx/fisher/issues/50), filed together after a file-by-file comparison of
both source trees. Closed so far — [#45](https://github.com/JasperFx/fisher/issues/45),
[#34](https://github.com/JasperFx/fisher/issues/34),
[#22](https://github.com/JasperFx/fisher/issues/22),
[#23](https://github.com/JasperFx/fisher/issues/23) and
[#24](https://github.com/JasperFx/fisher/issues/24).

---

## LINQ

The single largest area. Fisher's `LinqQueryParser` handles `Where`, the four ordering operators,
`Take` and `Skip`, and refuses everything else by name; Polecat's handles twenty-odd operators.

| Feature | Issue |
|---|---|
| ~~`SumAsync` / `MinAsync` / `MaxAsync` / `AverageAsync`, `LastAsync`, predicate overloads of `CountAsync`/`AnyAsync`~~ | [#22](https://github.com/JasperFx/fisher/issues/22) **done** |
| ~~`Select` projections (scalar, anonymous, constructor), `Distinct`, `DistinctBy`~~ | [#23](https://github.com/JasperFx/fisher/issues/23) **done** |
| ~~`GroupBy`, with `Where`-after-group as `HAVING`~~ | [#24](https://github.com/JasperFx/fisher/issues/24) **done** |
| ~~`Join`, `GroupJoin(...).SelectMany(...)`~~ | [#25](https://github.com/JasperFx/fisher/issues/25) **done** — plus the plain `Join` the issue did not ask for |
| ~~`AnyTenant` / `TenantIsOneOf`, `ModifiedSince` / `ModifiedBefore`, `QueryForNonStaleData`, `IsOneOf` / `In` / `IsEmpty` / `object.Equals`~~ (`CreatedSince`/`CreatedBefore` wait on [#29](https://github.com/JasperFx/fisher/issues/29)) | [#26](https://github.com/JasperFx/fisher/issues/26) **done** |
| ~~`IPagedList` / `ToPagedListAsync`, and keyset (cursor) pagination~~ | [#27](https://github.com/JasperFx/fisher/issues/27) **done** |
| ~~`LoadJsonAsync`, `ToJsonArrayAsync`, `ToJsonFirstWithVersionAsync`, streaming~~ | [#28](https://github.com/JasperFx/fisher/issues/28) **done** |
| ~~Batched document queries, `IQueryPlan`, `CheckExistsAsync`, `ToSql`~~ | [#37](https://github.com/JasperFx/fisher/issues/37) **done** |

Two of these were *cheaper* on SQLite than on either sibling and both paid out. `GroupJoin`
([#25](https://github.com/JasperFx/fisher/issues/25)) is a plain join between two tables with no
`OPENJSON` and no round trip to amortise — and the usual "a round trip is cheaper than a join"
argument inverts for an in-process store. It also needed no statement type of its own, where Polecat
carries a parallel `JoinStatement`. Keyset pagination
([#27](https://github.com/JasperFx/fisher/issues/27)) can use SQLite's native row-value comparison,
where T-SQL needs the expanded OR-of-ANDs form the planner cannot index.

## Sessions and the unit of work

| Feature | Issue |
|---|---|
| ~~`QuerySession()` on the store, and `SessionOptions` (tenant, isolation, timeout, **connection/transaction enlistment**)~~ | [#30](https://github.com/JasperFx/fisher/issues/30) **done** — the listeners half stays with #32 |
| `IdentitySession()`, `DocumentTracking`, dirty tracking, `Eject` / `EjectAllOfType` / `EjectAllPendingChanges` | [#31](https://github.com/JasperFx/fisher/issues/31) |
| `IDocumentSessionListener` and `IChangeSet` | [#32](https://github.com/JasperFx/fisher/issues/32) |
| `ForTenant(...)` / `ITenantOperations` — writing for several tenants in one unit of work | [#33](https://github.com/JasperFx/fisher/issues/33) |
| ~~`QueueSqlCommand` and `IAdvancedSql`~~ | [#34](https://github.com/JasperFx/fisher/issues/34) **done** |
| `ITransactionParticipant` | [#50](https://github.com/JasperFx/fisher/issues/50) |

`SessionOptions`' enlistment half and `ITransactionParticipant` are the two answers to the same
problem from opposite ownership directions, and it is a **sharper problem here than on either
sibling**: one writer per file means an application that writes its own tables and Fisher's in the
same file cannot do both atomically, and contends with itself trying. **Two of the three answers are
now built.** [#34](https://github.com/JasperFx/fisher/issues/34) is the cheapest —
`QueueSqlCommand` enrols the application's own statements in Fisher's transaction, so the common case
needs nothing else. Building it turned up the piece with no sibling to port: raw SQL is the only path
where a caller's value reaches a parameter unconverted, and a Guid, a timestamp and a decimal each
bind to something Fisher never wrote. [#30](https://github.com/JasperFx/fisher/issues/30) is the other
direction — `SessionOptions.ForTransaction(tx)` puts Fisher's writes inside a transaction the caller
owns and commits. `ITransactionParticipant` ([#50](https://github.com/JasperFx/fisher/issues/50)) is
the remaining one, and it is now the EF Core interop story rather than an atomicity gap.

## Document storage

| Feature | Issue |
|---|---|
| Metadata columns `created_at`, `correlation_id`, `causation_id`, `last_modified_by`, `headers`; `MetadataForAsync` | [#29](https://github.com/JasperFx/fisher/issues/29) |
| ~~Patching — `Set` / `Increment` / `Append` / `Remove` / `Rename` / `Delete` / `Duplicate`, by id or predicate~~ (`Insert` at an index is [#52](https://github.com/JasperFx/fisher/issues/52)) | [#35](https://github.com/JasperFx/fisher/issues/35) **done** |
| ~~Bulk insert, with `InsertsOnly` / `OverwriteExisting`~~ (`IgnoreDuplicates` is [#53](https://github.com/JasperFx/fisher/issues/53)) | [#36](https://github.com/JasperFx/fisher/issues/36) **done** |
| Document foreign keys | [#38](https://github.com/JasperFx/fisher/issues/38) |
| `[Index]` / `[UniqueIndex]` / `[HiloSequence]` attributes, `AddSubClassHierarchy()`, `StorePolicies`, `IInitialData` | [#39](https://github.com/JasperFx/fisher/issues/39) |

Patching ([#35](https://github.com/JasperFx/fisher/issues/35)) is the strongest single case in this
list. Every operation maps to one json1 function in one `update` statement — no server function to
install, unlike Marten's PL/pgSQL patch function — and a Fisher duplicated field, being a `VIRTUAL`
generated column, follows a patch with nothing to refresh, where both siblings must update their
duplicated columns inside the patch SQL. That is fisher#2's generated-column decision paying off.

Bulk insert ([#36](https://github.com/JasperFx/fisher/issues/36)) has no `SqlBulkCopy` analogue and
does not need one: on SQLite the transaction dominates the cost, so a prepared statement rebound per
row inside one transaction is already the fast path.

## Event store

| Feature | Issue |
|---|---|
| Natural keys — and with them the last partial member on `IEventStoreOperations` | [#40](https://github.com/JasperFx/fisher/issues/40) |
| ~~`QueryRawEventDataOnly<T>()` — LINQ over the event **body**~~ | [#41](https://github.com/JasperFx/fisher/issues/41) **done** |
| ~~`FetchEventStoreStatistics`, `ToDatabaseScript` / `WriteCreationScriptToFileAsync`, `CleanAsync<T>`, `EventProjectionScenario`~~ | [#42](https://github.com/JasperFx/fisher/issues/42) **done** |
| Binary event serialization (`[BinaryEvent]`, `IEventBinarySerializer`) | [#43](https://github.com/JasperFx/fisher/issues/43) |
| `IDocumentStoreDiagnostics`, `IDocumentStoreUsageSource`, projection replay | [#44](https://github.com/JasperFx/fisher/issues/44) |
| ~~`CompositeProjection`~~ | [#19](https://github.com/JasperFx/fisher/issues/19) **done** |

[#40](https://github.com/JasperFx/fisher/issues/40) is the one that closes a stated partial:
`FetchForWriting<T, TId>` and `FetchLatest<T, TId>` accept only an id that is already the stream
identity type, because in the siblings that overload is the natural-key **and** strong-typed-id entry
point. fisher#14 closed the strong-typed half; this is the other.

[#43](https://github.com/JasperFx/fisher/issues/43) matters more here than it looks: SQLite has no
`jsonb`, so Fisher stores the literal JSON text of every event forever, and the store's disk footprint
is the application's.

## Store and infrastructure

| Feature | Issue |
|---|---|
| ~~`IDocumentStore` — the store is a concrete class with no interface~~ | [#45](https://github.com/JasperFx/fisher/issues/45) **done** |
| `AddFisherStore<T>`, `IConfigureFisher` — several stores in one application | [#46](https://github.com/JasperFx/fisher/issues/46) |
| Database-per-tenant / `ITenancy` / master-table tenancy | [#47](https://github.com/JasperFx/fisher/issues/47) |
| OpenTelemetry spans for session work | [#48](https://github.com/JasperFx/fisher/issues/48) |
| `Fisher.AspNetCore` — streaming JSON results, ETags, daemon health check | [#49](https://github.com/JasperFx/fisher/issues/49) |
| `Fisher.EntityFrameworkCore` — transaction participation and EF-backed projections | [#50](https://github.com/JasperFx/fisher/issues/50) |

[#45](https://github.com/JasperFx/fisher/issues/45) is **done** — eight public members extracted, with
the tooling surfaces (`IEventStore` and friends) deliberately left as explicit implementations so a
monitoring-only API does not land on the store's own, and the boundary pinned by reflection in both
directions. [#46](https://github.com/JasperFx/fisher/issues/46) and
[#49](https://github.com/JasperFx/fisher/issues/49) are unblocked by it.

**[#47](https://github.com/JasperFx/fisher/issues/47) is the one where SQLite's constraint becomes the
feature.** A tenant is a file: creating one is a `File.Create` plus a migration, deleting one is
deleting a file, and — the part that matters — tenants get **separate write locks**, which is the only
way a multi-tenant Fisher application scales writes at all. It is a performance feature here and an
isolation feature on both siblings. It is also what would finally make the `IEventDatabase` parameters
that `DocumentStore.Daemon.cs` ignores throughout start carrying an answer.

---

## Not gaps — SQLite has no equivalent and never will

Recorded so they are not rediscovered as omissions. None of these has an issue.

| Polecat feature | Why not |
|---|---|
| `DocumentPartitioning`, `RollingPartitions`, `AllDocumentsAreMultiTenantedWithPartitioning()`, `AddPolecatManagedTenantsAsync`, `TablePartitionStatus` | SQLite has no table partitioning at all — no partition functions, no schemes, no per-partition storage. Separate tables under a `UNION ALL` view is a different feature and carries none of the operational properties (partition switching, aged-partition drop) that make Polecat's worth having. |
| `DaemonMode.HotCold`, `Events/Daemon/Coordination/` | Hot-cold failover means several nodes competing for a leadership lease through the database, and SQLite does not make a file safe to share across nodes. `AddFisher` refuses the mode rather than silently running Solo — see the DI notes in CLAUDE.md. |
| `JsonIndex` | Polecat needs a `JSON_VALUE` computed index to index into JSON. SQLite indexes the expression directly, which is what fisher#16 built — cheaper, and already done. |
| `TenantEventSequenceRegistry`, `TenantPartitionOrdinalRegistry` | Mechanics of SQL Server sequences and partition ordinals. Fisher's sequence is `INTEGER PRIMARY KEY AUTOINCREMENT`. |
| `SqlBulkCopy` | No wire protocol to bulk-load over — but see [#36](https://github.com/JasperFx/fisher/issues/36) for what replaces it, which is not slower. |
| Row-locking `FetchForExclusiveWriting` semantics | Already documented in CLAUDE.md's divergence table: SQLite has no row lock, so the loser of an exclusive append **fails** rather than **waits**. The safety property is unchanged. |

## Also not gaps — deliberate, already decided

| Feature | Decision |
|---|---|
| A message bus / durable outbox | [fisher#8](https://github.com/JasperFx/fisher/issues/8), closed wontfix. `NulloMessageOutbox` is the intended end state; delivery is a bus integration's job here as on both siblings. |
| `IChangeListener` (Polecat's local spelling) | Fisher uses JasperFx's lifted `IDaemonChangeListener`. Polecat's is the older spelling of the same thing; new code should not copy it. |
| An inline equivalent of subscriptions | "Inline" would be code in the caller's own unit of work. A subscription needs the daemon. |
| Compiled queries | Declined on a measurement — [fisher#195](https://github.com/JasperFx/fisher/issues/195). **This row used to read "Polecat declines them too", which was not a reason and inherited a wrong one**: Polecat's own note says SQL Server's query plan caching handles it natively, and that conflates two different costs. A plan cache saves the *server* re-planning SQL it has seen; a compiled query saves the *client* walking an expression tree and rendering SQL, which the database never sees. Measured on Fisher, that client-side half is 4–11% of an ordinary query, 22.5% of the cheapest one, and 35–78% of a query's allocations — real, but ~2 µs, and a filter-shape plan cache collects nearly all of it with no public API. |

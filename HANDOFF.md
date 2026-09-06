# Handoff

State of Fisher after the **Polecat comparison** — a file-by-file sweep of both source trees that filed
[#22–#50](https://github.com/JasperFx/fisher/issues/22) and has been working through them since.
Written for whoever picks this up next.

**Nothing is half-built.** Every milestone on disk builds, is tested, and was committed complete.
[ROADMAP.md](ROADMAP.md) says what comes next and why in that order;
[polecat-gaps.md](polecat-gaps.md) indexes the comparison backlog and records what SQLite has no
equivalent for and never will.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps. This document is the compliance
scoreboard and the things that are true right now but not obvious from either.

**1805 tests green on net9.0 and net10.0** — 1750 in
`Fisher.Tests`, 36 in `Fisher.AspNetCore.Tests` and 19 in `Fisher.EntityFrameworkCore.Tests`. 516 of
them are shared cross-store compliance tests — 447 event sourcing and 69 document. On JasperFx **2.66.0** / Weasel **9.29.0**.

### Cleared by JasperFx 2.66.0 — the five that were red

**All five were upstream, and the 2.66.0 bump is what closed them.** Nothing in `src/Fisher` was
implicated in any of them, and nothing in `src/Fisher` changed to make them pass — the pin bump is
the whole diff. Two were `stream_archiving_compliance`
(`capturing_an_archived_event_archives_a_string_identified_stream` and
`capturing_an_archived_event_through_an_async_snapshot_archives_the_stream`) and three were
`upcasting_compliance` (`a_raw_json_transformation_upcasts_without_the_old_clr_type`,
`upcasting_applies_in_live_aggregation` and `upcasting_applies_in_async_daemon_projections`).
Kept here rather than deleted, because both diagnoses are worth having the next time an
archiving or upcasting fact goes red.

**The first two were jasperfx#778, a real product bug in `JasperFx.Events` that Fisher found by being
the store that ran the suite.** An `Archived` event the aggregate applies nothing for did not archive
its stream, on any store. `Archived` carries no state, so an aggregate has no reason to declare an
`Apply` for it — which left it out of the projection's `AllEventTypes`, so `AppliesTo` answered
false, the inline path screened the whole stream out *before reading anything*, and the async shard's
own filter never delivered the event into a slice at all. Marten's four local archiving tests all
append a handled event beside the marker, which is why it survived there. jasperfx#780 closed the
innermost of three gates — and on Fisher changed nothing, which is what sent jasperfx#784 after the
two outside it.

**The last three were jasperfx#787, a suite bug**, and the first two of them are one cause:
`UpcastingCompliance`'s raw-JSON fact reached the stored body with a case-sensitive
`GetProperty("CartId")`. Property casing is a store-serializer decision — Marten's default
`PropertyNamingPolicy` is null, Polecat's and Fisher's is camelCase — so the fact passed on Marten
and threw `KeyNotFoundException` on the other two. The live-aggregation and daemon facts append a
coupon event and go down with it; the daemon one surfaces sixty seconds later as a shard that
recorded no progress. Fixed upstream in jasperfx#788.

jasperfx#779, the other suite bug this wave's enrollment turned up, was already in 2.65.0.
**jasperfx#784 and jasperfx#788 are the two that shipped in 2.66.0**, and between them they are the
whole of what turned this section from a red list into a cleared one.

Note that 440 is not quite *every* event suite the shared library has, and three of the shortfall are
enrolled-and-gated rather than absent:

- `SingleTenantedEventSlicingCompliance` (jasperfx#724, 2.59.0) is **not enrolled**, because its
  mixed-tenancy precondition cannot be constructed on Fisher at all — see jasperfx#727 — and its own
  guard would make every fact skip itself.
- `UpcastingCompliance` (jasperfx#752, 2.64.0) is **not enrolled** because Fisher has no upcasting
  yet; enrolling it today would be a suite that skips itself wholesale.
- `DcbHasTagLinqCompliance`, `AggregateToLinqOperatorCompliance` and `AggregateToManyCompliance`
  (2.64.0) **are enrolled and gated off**. All three terminate a cross-stream `QueryAllRawEvents()`
  returning `IQueryable<IEvent>`, which Fisher does not have and would need a second LINQ provider to
  grow: Fisher's is built over *document* storage, which is why `EventOperations.QueryEventsAsync`
  takes a predicate rather than returning a queryable. Nothing behavioural is declined — DCB tag
  querying is green through `DcbTagQueryAndConsistencyCompliance` and `AssignTagWhereCompliance`, and
  cross-stream aggregation is `AggregateByTagsAsync`.

`CompositeProjectionCompliance` (jasperfx#725) *is* enrolled since fisher#154 closed the
`AddCompositeProjection` seam on the fixture.

## Closed since the comparison

`IDocumentStore` (#45) · raw SQL, `QueueSqlCommand` and `IAdvancedSql` (#34) · LINQ aggregates and
`Last` (#22) · `Select` projections, `Distinct`, `DistinctBy` (#23) · `GroupBy` and `HAVING` (#24) ·
**a cross-tenant read (#51)** · the marker operators (#26) · offset and keyset paging (#27) · batching,
query plans, `CheckExistsAsync`, `ToSql` (#37) · JSON-returning reads (#28) · `Advanced` parity (#42) ·
patching (#35) · bulk insert (#36) · composite projections (#19) · event body queries (#41) ·
sessions, `SessionOptions` and enlistment (#30) · **LINQ joins (#25)** · aggregates and `Last` over a
join (#54) · `IgnoreDuplicates` (#53) · patch `Insert` at an index (#52) · **document metadata and
`MetadataForAsync` (#29)** · **natural keys (#40)**.

## Natural keys — and the end of the last partial member

**#40 closes the one place `IEventStoreOperations` was still described as partial.** `FetchForWriting<T,
TId>` and `FetchLatest<T, TId>` are the natural-key and strong-typed-id entry point in both siblings;
fisher#14 closed the second half and this closes the first, so the qualifier comes out of CLAUDE.md.

As with the daemon, **the definition and the discovery are JasperFx's** — `NaturalKeyDefinition`,
`[NaturalKey]`, `[NaturalKeySource]` and `JasperFxAggregationProjectionBase` all ship in 2.45.0 — and
Fisher supplies the storage seam. Four divergences from Polecat, each verified:

- **No `is_archived` column on the lookup table.** Polecat copies the flag from `pc_streams` and keeps
  it in sync from a projection watching for the `Archived` event, which is then why it needs a second
  rebuild-time entry point: a daemon rebuild replays events without appending streams, so the table
  would be left empty after teardown. Fisher archives with a direct operation rather than an event, and
  the lookup joins `fi_streams` anyway — reading the flag off the join makes the streams table the only
  place that knows, and removes the sync step, the projection and the rebuild path together.
- **The rows are written from the session rather than from an inline projection**, next to
  `EventTagWriter` and inside the append's transaction. A key registered outside it leaves either a
  stream no key resolves to or a key naming a stream that does not exist.
- **A second stream claiming an existing key is refused.** Polecat's `MERGE` repoints, so the newcomer
  silently takes the key and the original becomes unreachable by the identifier it was created with.
  The conflict clause is guarded and the statement returns the row it settled on; "no row" is the
  failure, which is exactly how the optimistic document upsert reads its version guard.
- **No foreign key to `fi_streams`, uniformly** — Polecat has one for single-tenant stores and drops it
  under conjoined tenancy, so its two tenancy styles differ there.

The Guid trap showed up for the third time and behaved identically: binding the uppercase form makes
every lookup return nothing. Documents, tag rows, and now this.

One thing worth knowing that no test would have told you: **the lookup rows have to be cleared by
`DeleteAllEventDataAsync`.** Left behind, the duplicate guard fires on data that no longer exists — and
since the compliance fixture cleans before every test, that presents as an unexplained failure two
tests later rather than where the cause is.

## Document metadata — the wiring that was already there

**#29 asked for five columns Polecat has and Fisher did not, and Weasel.Storage already had a binder
for every one of them.** `DocumentCreatedAtBinder`, `DocumentCorrelationIdBinder`,
`DocumentCausationIdBinder`, `DocumentLastModifiedByBinder`, `DocumentHeadersBinder` and
`DocumentTenantIdBinder` were all sitting unused. The issue's step 3 said to check upstream first and
that if the binders existed this would be wiring; they did, and it was.

**The one thing the issue predicted wrongly is worth reading before touching the descriptor.** It
expected `created_at` to need a deliberate exception to the "assign every column from `excluded.*`"
rule — "that is exactly the kind of thing a later cleanup would simplify away". It needs none. The
upsert's set clause is built from the *write list*, so a column no write binder contributes is not in
it: `created_at` is filled by its own parenthesized DEFAULT and its binder goes into `readBinders`
alone. Making it a write binder is what breaks the test, verified by doing it. The rule stands
unqualified, which is a better outcome than a documented carve-out.

`tenant_id` is read-only too, for a different reason — it is part of the primary key and the storage
operations bind it inline ahead of the binder loop, so a write binder would be a second writer.

Three decisions that are about not shipping dead knobs:

- **Only the opt-in columns have an `Enabled` flag.** Whether `guid_version`, `revision`, `is_deleted`
  and `deleted_at` exist is decided by `UseOptimisticConcurrency()`, `UseNumericRevisions()` and
  `SoftDeleted()`; `last_modified` is always there. Marten puts `Enabled` on all of them, which here
  would be a knob doing nothing four times over — so `OptionalMetadataColumnExpression` is a separate
  type and `MetadataColumn.Enable()` throws on the rest.
- **Mapping an optional column enables it**, for the same reason in the other direction: a mapping
  onto a column that would not exist is configuration that silently does nothing.
- **Turning one back off throws.** Creating a column is a migration and so is dropping one, and the
  second has data in it.

`MetadataForAsync` is the other half, and like bulk insert's duplicate probe it is **deliberately not
routed through the LINQ path** — that path applies the soft-delete filter, and "when was this deleted"
is one of the questions this method exists to answer. It is the second place in Fisher where going
around an implicit filter is correct; both are now written down, because the default is the opposite.

Its return type is `StoredDocumentMetadata`, not `DocumentMetadata` as on both siblings. Fisher already
has a `DocumentMetadata` one namespace away meaning the configuration rather than the values, and two
same-named types with opposite jobs is a collision only noticed by whoever imports the wrong one.

One gap this closed on the way past: **`IDocumentSession` did not declare `CorrelationId`,
`CausationId`, `CurrentUserName` or `Headers` at all.** They were public on `FisherSession` and on no
interface, so Fisher's own tests set them by casting to an internal type. Tolerable while only events
read them; wrong the moment a document does.

## Sessions and enlistment — the one the ordering called a hard stop

**#30 was the only open issue ROADMAP described as a hard stop rather than an inconvenience**, and the
reason is structural. SQLite permits one writer per file and an application using Fisher keeps its own
tables in that file, so an application that wanted its rows and Fisher's in one atomic unit had no way
to get it: `QueueSqlCommand` (#34) covers "my SQL inside Fisher's transaction", and there was nothing
for "Fisher's writes inside mine". `SessionOptions.ForTransaction(tx)` is that half.

`SessionOptions` has **three modes with one rule each** — no connection and no transaction is the
ordinary session; a connection alone means Fisher opens and commits its own transaction on it and never
disposes it; a transaction means Fisher neither commits nor rolls back. Marten's
`OwnsConnectionLifecycle` / `OwnsTransactionLifecycle` pair is deliberately not carried, because four
combinations of which two are traps is a worse surface than two rules that are always true.

**The finding worth carrying forward is the command/transaction one, because the probe that was meant
to establish it got the wrong answer.** A scratch program said a command with `Transaction` unset
executes happily on a connection with an open transaction — so the design treated setting it as
tidiness. It is not: Microsoft.Data.Sqlite throws *"Execute requires the command to have a transaction
object when the connection assigned to the command is in a pending local transaction"*. The probe was
wrong because it built its command with `connection.CreateCommand()`, **which inherits the connection's
transaction**, while every Fisher statement is a detached command out of Weasel's builder. Six tests
fail with that exact message if `ConfigureCommandAsync` stops setting it. The lesson generalises: a
provider probe has to construct its command the way the production path does.

Four other things about the enlisted path, each of which would have been silently wrong the other way:

- **No resilience pipeline.** A retried `SQLITE_BUSY` re-executes the whole batch, and the failed
  attempt's writes are still sitting in the caller's transaction rather than having rolled back with
  it — so the retry would write everything twice. Same property as fisher#4 and fisher#12; third
  time this shape has come up.
- **No post-commit step.** `AfterCommitAsync` and the append observer both claim "everyone can see
  this now", and Fisher is not told when the caller commits. `BeforeCommitAsync` still fires.
- **No on-demand table creation.** That path runs a migration on its own connection, which would block
  against the write lock the caller's transaction holds — a session deadlocking against itself,
  presenting after thirty seconds as `database is locked`. It throws by name instead, and the
  existence check runs on the caller's connection so a table created inside the same transaction
  counts.
- **A deferred caller transaction weakens the append guard and Fisher cannot warn about it.** Safety
  holds (SQLite still refuses the second writer, so no lost update); what changes is the loser gets
  `SQLITE_BUSY` rather than a clean concurrency failure. The provider reports `Serializable` for a
  deferred transaction and an immediate one alike, so there is nothing to detect — documentation is
  the only instrument.

`IsolationLevel` is carried for parity and refuses exactly one value. Verified against
Microsoft.Data.Sqlite 10.0.9: `Unspecified`, `ReadCommitted`, `RepeatableRead` and `Serializable` all
produce the same `BEGIN IMMEDIATE` and all report `Serializable` back, so Polecat code setting its
`ReadCommitted` default ports across unchanged. **`ReadUncommitted` is refused**, being the one value
that begins a deferred transaction — and nothing would signal the loss, since the transaction still
describes itself as `Serializable`.

**Two of the issue's five listed `SessionOptions` members were deliberately not built.** `Tracking`
belongs to [#31](https://github.com/JasperFx/fisher/issues/31) (no identity map, no dirty tracking) and
`Listeners` to [#32](https://github.com/JasperFx/fisher/issues/32); shipping either now would be a knob
that silently does nothing, which is the one thing this codebase refuses. `LightweightSession(SessionOptions)`
and `OpenSessionAsync` are absent for the same class of reason — Fisher opens one kind of session and
opens its connection lazily, so one is a second name for `OpenSession` and the other has nothing to
await.

The read-only question the issue refused to leave implied is decided: **`QuerySession()`'s narrowing is
a convention, not a guarantee**, said on `IQuerySession` itself and pinned by
`a_query_session_is_the_same_session_type_narrowed`, so making it real is a deliberate change rather
than a discovery.

**#51 is the one to read.** It was a genuine cross-tenant data leak — a conjoined `Query<T>()` with no
`Where` returned every tenant's rows, because the tenant filter was applied by wrapping each caller
predicate and a query with no predicates got none. It is the same mistake `ApplyHierarchyFilter`
already documents, which fisher#17 fixed for `doc_type` and nobody revisited for tenancy. All three
implicit filters are now one statement-level pass each. Nothing caught it because the conjoined
compliance suite covers the *event* store and Fisher's document tests were single-tenant.

Two follow-ups were filed rather than shipped half-done: `Insert`-at-an-array-index
([#52](https://github.com/JasperFx/fisher/issues/52), because `json_insert` is a silent no-op at an
occupied path) and `BulkInsertMode.IgnoreDuplicates`
([#53](https://github.com/JasperFx/fisher/issues/53), because `insert or ignore` is a fifth statement
in the descriptor rather than a flag). **Both are now closed.**

### #52 — and the two bugs the probe found in `Remove`

The insert itself went as the issue predicted: a `Remove`-style rebuild, with the ordering taken from
an explicit ordinal rather than from `json_each`'s row order, which is not a documented guarantee.
Doubling the ordinals is what lets the new element sit strictly between two neighbours — an existing
element keeps `2k` below the insertion point and takes `2k+2` at or above it, and the new one is
`2*index+1`. Past the end sorts above everything and therefore appends, which is why that case needs
no length to check and no round trip to learn one.

**Probing it found two defects in the `Remove` that shipped with fisher#35**, both silent and both
invisible to an array of one type:

- **A JSON `true` came back as `1`.** SQLite has no boolean, so `json_each` hands one over as the
  integer 1 and `json_quote` writes it back as a number. Any rebuild — so any `Remove` — flattened
  every boolean in the array.
- **Every removal dropped every `null` in the array.** A JSON null element reads back as SQL NULL, and
  `where value <> ?` is NULL rather than true for it, so the filter excluded it along with the element
  actually named. `Remove(member, null)` also removed nothing.

Both are fixed here, because they live in the expression `Insert` had to generalise anyway. The
element expression is keyed on `json_each.type` and the comparison is `is not`. Each half was verified
by reverting it.

One more thing worth knowing, because nothing in `Remove` could have taught it: **json1's JSON subtype
does not survive a subquery.** `Insert` projects its elements through one, so
`json_group_array(v)` writes every element as a quoted string and the aggregate has to re-parse with
`json_group_array(json(v))`.

### #53 — `IgnoreDuplicates`, and the one place going around the filters is right

Not with the fifth statement the issue expected, but by
filtering: each batch reads which of its ids are already stored and queues only the rest. The read is
the interesting part, because it is the one place where going *around* the implicit filters is
correct. `LoadManyAsync` and `Query<T>()` both answer "which of these can I read" and apply the
soft-delete and hierarchy terms to do it; the question here is "which of these would collide", and a
soft-deleted row still holds the primary key. Adding the soft-delete term makes the batch fail with
`UNIQUE constraint failed`, which was verified. The tenant term stays, because a conjoined table keys
on `(tenant_id, id)`.

Two more things in it, both verified by reverting them. **Ids compare as invariant strings**, because
the reader hands an INTEGER column back as `long` while an `int` identity's raw value is an `int` and
boxed those never compare equal — an int-keyed type would find nothing and fail on the very
constraint the mode exists to avoid. And **the probe is outside the write transaction on purpose**:
closing that window means holding `BEGIN IMMEDIATE` across it through an enlisted session, which
forfeits the `SQLITE_BUSY` retry for the operation most likely to contend for the write lock. The
window is not silent — a concurrent writer makes the insert fail loudly rather than being skipped. `GroupJoin` ([#25](https://github.com/JasperFx/fisher/issues/25)) was left for last in that tier for
the right reason — every locator and all three implicit filters qualified for two tables — and is now
done; see below.

## Joins — the piece where the port stopped being the answer

**#25 is the one issue so far where following Polecat's shape would have made Fisher worse**, and it is
worth reading the two side by side before touching `Linq/Joins/`. Three divergences, each verified by
reverting it:

- **The join is a `JoinClause` on the ordinary `Statement`, not a parallel `JoinStatement`.** Polecat's
  join path re-implements the select list, the wheres, the ordering, the paging and the count; Fisher's
  `Count`, `Any`, `ToPagedListAsync` and `ToSql` serve a join without knowing it is one. The single
  place that had to learn about joins is `WrapAsSubquery` — a count over a *paged* join has to carry the
  join into the subquery or it counts the outer table. The test that catches that has to page to a size
  between the two counts, which the first version of it did not.
- **The table alias is threaded through `MemberFactory`, not rewritten into finished SQL.** Polecat has
  an `AliasingCommandBuilder` and a string-replacing `AliasLocator` because it applies aliases after
  rendering; that produces valid SQL reading the wrong table whenever a pattern matches something it
  should not. Removing the qualifier fails 17 of the 27 join tests, most of them silently wrong rather
  than erroring.
- **Both documents are materialized by their own storages' selectors**, the inner one through an
  offsetting `DbDataReader` view, because a closed-shape selector reads from fixed positions. Polecat
  deserializes the `data` column directly, which loses `doc_type` resolution — a joined sub-class comes
  back as its base — and every metadata binder with it.

**Polecat also drops the inner query's `Where` clauses silently**, collecting only its tenant and
soft-delete filters. Fisher parses the inner source with the same parser and the inner alias, so
`GroupJoin(session.Query<Catch>().Where(...))` means what it says.

The placement rule worth remembering: **everything about the inner side goes in the `ON` clause, and a
post-join `where` goes in the `WHERE`** — the first says which rows may match, the second which joined
rows survive, and on a left join the difference is visible in the answer. Moving the inner-side filters
to the `WHERE` fails five tests, all of them cases where a left join quietly became an inner one.

Two follow-ups were filed rather than shipped half-done, and **both are now closed** — #54 below, and
more than one join per query ([#55](https://github.com/JasperFx/fisher/issues/55)). #55's premise was
right: `Statement.Joins` was already a list and everything above it was written for exactly two sides.
What it cost in the end was one type. A second join is written against the *shape* the first produced,
so its outer key names no document until that shape is resolved back to one; `JoinShape` composes that
rung by rung, and the outer/inner pair everywhere else became a list of `JoinSide` without any new
idea. Two things only appear past the second join: a shape has to be carried whole as well as member by
member (a `GroupJoin`'s own selector names the entire previous rung), and a *third* join's shape holds
the second's shape, so a member access on it has to be folded or it lands on an anonymous-type
construction that evaluates in memory and translates to nothing.

### #54 — the aggregates and `Last` over a join, and the defect underneath them

The issue read as a missing feature and half of it was a **bug on the unjoined path**. Both terminals
reached `Build<T>`, which asks the schema for a mapping of the query's *element* type — and a join's
element type is the caller's result shape, a projection's is whatever it produced, and neither is a
document. So `Select(...).SumAsync(...)` did not refuse: it threw `InvalidOperationException` about
identity members, naming neither the operator nor the reason. Building from `SourceTypeFor` instead —
which every other terminal already used — answers the join and refuses the projection by name.

Three things worth carrying:

- **The seam is one closure, not a second resolver.** `JoinPlan.Member` holds the post-join member
  mapping the `Where` and `OrderBy` already go through, so an aggregate selector cannot disagree with
  the clauses on the same statement about which side a member belongs to. Re-deriving the projection
  and the two member factories would have been three chances to.
- **The two aggregate guards needed nothing.** They ask about the *resolved member* — does it order,
  is it a number — so they were already right for a joined selector. That is the dividend of fisher#22
  having made `AggregateFunction` an enum with two distinct guards rather than a SQL string.
- **A paged `Last` over a join cannot reuse `ReverseOverPage`**, and this is the one genuinely new
  piece. That wrapper works unjoined because `json_extract(data, …)` resolves against the subquery's
  own `data` column; a join's locator is `json_extract(outer_t.data, …)` and the alias does not survive
  into the enclosing scope — `no such column: outer_t.id`, verified by trying it. The keys are aliased
  into the page's select list and the outer statement orders by the alias, which is what keyset paging
  already does to sort on a locator no member of the result exposes. Reverting to the in-place reversal
  answers about the whole join rather than the page; both directions were run.

The aggregate's paged subquery carries `Joins` and `FromAlias` for the reason `WrapAsSubquery`
documents. Dropping them is `no such column: inner_t.data` rather than a wrong number — which is worth
saying because it is the *only* place qualification is a mercy: everywhere else in this feature an
unqualified locator produces valid SQL that reads the wrong table.

Both LINQ spellings are supported, which is more than the issue asked for: query syntax's plain `join`
clause emits `Queryable.Join` rather than `GroupJoin`, and a `where` or `orderby` after it names the
transparent identifier rather than the projected result. One rewriter collapses all of it onto
`(outer, inner) => …`; which shape a later clause names is decided by its parameter's *type*, and which
side a member belongs to by parameter *reference* — a self-join makes the type ambiguous.

## The 2.45.0 bump, and the first suite to check something nothing else did

`ConjoinedEventTenancyCompliance` (8 tests) and `SubscriptionCompliance` (6) were enrolled and were
green on the first run. **No production change** — the cost was two seam members and one small partial:

- `ComplianceStoreConfig.ConjoinedEventTenancy` → `options.Events.TenancyStyle = Conjoined`, which has
  to be set before `ApplyAllConfiguredChangesToDatabaseAsync` because it is a *schema* decision:
  `StreamsTable` and `EventsTable` read it when they build their columns and their primary key.
- `IComplianceStoreRegistrar.Subscribe(ComplianceSubscription)` → `Projections.Subscribe(...)`, with
  the name pinned to `ComplianceSubscription.SubscriptionName`. Fisher's `SubscriptionWrapper` happens
  to derive exactly that string from the type name, but progression is keyed on it and a store should
  not leave that resting on a naming convention it could reasonably change.
- `ComplianceSubscription.Fisher.cs`, the per-consumer partial — the same shape as
  `ComplianceFlatTableProjection` and for the same reason. Fisher's is the closest of the three stores
  to being writable once, because fisher#21 took JasperFx's lifted `IDaemonChangeListener` rather than
  copying Polecat's older product-local `IChangeListener`.

**The tenancy suite is the first to check a Fisher feature that had never had a cross-store test.**
Every other suite so far has either confirmed a port or demanded a new feature. Conjoined event tenancy
was built with the original schema work — `fi_streams` and `fi_events` have keyed on `(tenant_id, id)`
since then — and the only thing holding it was Fisher's own `event_store_schema_creation`, a schema
test. A schema test cannot see the property that actually matters: *isolation*, whose failure mode is
silent and asymmetric, since a store that leaks across tenants still answers correctly for the tenant
that owns the data and misbehaves only for the other one. The suite checks both directions on every
test, over a stream id deliberately reused across two tenants.

**Verified load-bearing rather than assumed.** A suite that passes on the bump is exactly where a
seam member that quietly does nothing would hide, so the flag was removed and the suite re-run: it
fails with `ExistingStreamIdCollisionException` from `AppendPlanner`, which is precisely the "collide
on append" outcome the suite's own design notes predict for a store keying on id alone. The flag is
doing the work.

Like 2.43.0 and 2.44.0 this is a compliance-tests-only release — the core assemblies are unchanged and
the whole diff is the two suite files, three seam additions and the version. **With it the upstream ES
compliance backlog is empty**, so twenty-eight was where the *event* half sat until a new one was
filed. 2.47.0 opened a second half rather than a twenty-ninth event suite — see the document contract
below. Two have been filed since, both from widened contracts rather than from a reopened backlog:
`BinaryEventSerializationCompliance` in 2.50.0 (the twenty-ninth) and `AggregateWriteCacheCompliance`
in 2.51.0, putting the event half at thirty.

## The 2.44.0 bump cost nothing whatever

The first bump to add suites and need **no Fisher change at all** — not a seam member, not a line of
production code. `RebuildAndCatchUpCompliance` (11 tests) and `DeadLetterCompliance` (6) were enrolled
and were green on the first run.

Both were filed upstream as needing a seam addition and neither did, which is worth recording because
the reasoning generalises. The whole rebuild surface — seven `RebuildProjectionAsync` overloads, both
`CatchUpAsync` forms, `PrepareForRebuildsAsync` — is declared on the shared `IProjectionDaemon`, which
the fixture's `StartDaemonAsync` already returns. The dead letter path is
`IEventStore<TOperations, TQuerySession>.ContinuousErrors` for the policy and
`IEventStore.AllDatabases()` → `IEventDatabase.QueryDeadLetterEventsAsync` for the rows; Fisher
implements all three because the daemon needs them regardless of any suite.

The trick the dead letter suite uses is worth knowing: it casts the fixture's **non-generic**
`IEventStore` to the closed generic to reach `ContinuousErrors`. That is safe because the suite is
generic over the same session pair the store closes over, and it reaches the entire generic store
surface without any seam member at all.

**The rebuild teardown test is the one with teeth.** A rebuild that replays onto surviving rows looks
correct for every stream whose events still exist, and is wrong only for rows the replay can no longer
produce — so it passes any assertion about a live aggregate and fails only on stale state. The suite
plants a document with no backing events and requires the rebuild to remove it. Fisher already had
this right; it is the same divergence `TeardownExistingProjectionStateAsync` had to learn for flat
tables via `IPublishesTables`, now checked for document projections too.

Like 2.43.0, this is a compliance-tests-only release — 2.44.0 is one commit past 2.43.0 and that
commit touched only the two new suite files and the version.

**One disclosure carried over from upstream.** The 2.44.0 commit message records two unreproduced
failures of `a_rebuild_reproduces_the_projected_state` on Polecat early in that suite's development,
with no error text and no reproduction, passing ever since. Not seen on Fisher: 15 consecutive runs of
the rebuild suite clean, plus two full 216-test compliance runs. Recorded so a first sighting here is
recognised rather than investigated cold.

## The 2.43.0 bump cost nothing but the seam

Both new suites went green **on the bump alone** — 21 tests, no production change. That is the point
of them: fisher#9 (masking) and fisher#10 (compacting) were ported from Polecat's shape before a
shared suite existed to check the shape was right, and this is what turns "ported faithfully" from a
claim into a fact.

`StreamCompactingCompliance` needed nothing at all. `CompactStreamAsync<T>` was lifted onto
`IEventStoreOperations` as a default-implemented throw back in 2.41.0, so the suite reaches it through
the shared operations surface — a store without compacting surfaces as a `NotSupportedException` from
the DIM rather than a compile error, which is exactly the shape that lets a suite ship ahead of an
implementation.

`EventDataMaskingCompliance` cost **three seam members and nothing else**. `IEventDataMasking` became
shared in the same 2.41.0 lift, but the entry point that hands one out did not — every store spells it
on its own `Advanced` surface and those share no interface. So:

| Seam member | Fisher's |
|---|---|
| `FisherComplianceFixture.ApplyEventDataMaskingAsync` | `Store.Advanced.ApplyEventDataMaskingAsync` — signatures already matched one for one |
| `FisherComplianceRegistrar.AddMaskingRule<T>(Action<T>)` | `EventGraph.AddMaskingRuleForProtectedInformation<T>(Action<T>)` |
| `FisherComplianceRegistrar.AddMaskingRule<T>(Func<T,T>)` | `EventGraph.AddMaskingRuleForProtectedInformation<T>(Func<T,T>)` |

The `Action` / `Func` split the seam demands is the split Fisher already had, and for the same reason
the suite gives: only the mutating form reaches contravariantly, because `IEvent<out T>` is covariant
while assigning a replacement back needs the closed `Event<T>`'s setter. See CLAUDE.md, "Event data
masking".

The core packages did not move — `JasperFx` and `JasperFx.Events` 2.43.0 are byte-identical in
documented public API to 2.42.2. This was a compliance-tests release.

**fisher#13's intermittent rebuild failure is gone**, fixed at `e3c9912` — the session's operation
queue was not thread-safe. Earlier handoffs described it as the one known flake; it no longer is.

## All seven filed issues closed

The tracker was empty when this pass started, while ROADMAP named five unbuilt features — against
Fisher's own convention that a deferred gap lives in the tracker rather than in a doc. Seven were
filed; all seven are done — #19, composite projections, was the last and shipped, so this list is
closed rather than outstanding.

Three of the seven turned up a real defect or a wrong premise, which is the useful part:

- **#18** — the difficulty was the *positional slot contract*, not the SQL. The shared upsert binds
  four trailing slots unconditionally, overwrite two; Fisher's `guarded` flag is false under Numeric
  because it means "Optimistic" to its caller, so reading it produced a two-slot statement for a
  four-slot binder and an `IndexOutOfRangeException` from inside Weasel naming nothing of Fisher's.
- **#20** — everything a container disposes was `IAsyncDisposable` only, and a `ServiceProvider`
  disposed synchronously *refuses* such a service. Since sessions register scoped, that made a scoped
  session unusable rather than merely less efficient.
- **#17** — this issue's own premise was wrong. `dotnet_type` cannot be the discriminator: it holds an
  assembly-qualified name and its binder takes no alias resolver. A separate `doc_type` column was
  needed after all.

| Issue | Why it is worth knowing |
|---|---|
| ~~[#15](https://github.com/JasperFx/fisher/issues/15)~~ `OpenReadOnlyEventStore` | **The one real finding.** Its doc comment blamed a missing paged event query layer; fisher#9 built one. `EventQuery` is flat exact-match filters plus paging, not an expression, and every column it names is on `fi_events`. Small, with a Polecat template. |
| ~~[#16](https://github.com/JasperFx/fisher/issues/16)~~ indexes | `Duplicate` is currently the only way to get one. SQLite indexes expressions directly, so this is *cheaper* here than on the siblings — but the indexed expression must be built from `MemberFactory`'s `TypedLocator`, or it is created, never used, and reports nothing. |
| ~~[#17](https://github.com/JasperFx/fisher/issues/17)~~ hierarchies | `dotnet_type` is already the discriminator, so no schema change. Decide whether it holds a short alias before anything writes rows. |
| ~~[#18](https://github.com/JasperFx/fisher/issues/18)~~ numeric revisions | The one item on this list with no SQLite-specific answer needed, which is unusual enough to say. |
| ~~[#19](https://github.com/JasperFx/fisher/issues/19)~~ composite projections | Nearly as free as it looked — `FisherCompositeProjection` closes JasperFx's base and `CompositeIProjectionSource` presents a bare `IProjection` as a stage. What it cost was member teardown (fisher#63): a wrapper holding a member must be asked what it publishes, or a rebuild replays onto rows the previous run left. |
| ~~[#20](https://github.com/JasperFx/fisher/issues/20)~~ `AddFisher` | The largest gap between "works" and "usable without boilerplate". |
| ~~[#21](https://github.com/JasperFx/fisher/issues/21)~~ subscriptions | `ISubscriptionRunner` is resolved by a soft `as` cast, so not implementing it fails at runtime rather than at compile time — which is why it reads as absent rather than broken. |

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.66.0 ships 52 suites; Fisher enrolls **50 of them, 516 tests**.
Fisher passes **516 of them, across all
50 suites**. Every suite compiles; every one is also subclassed and running. The five that did not
pass on the 2.65.0 pin were the upstream ones described at the top of this file, and 2.66.0 closed
all five.

**What that does and does not claim, because the difference is load-bearing** (fisher#124). The suite
pins **API portability, not behavioural equivalence**: code written against one store compiles and
runs against another. It does not pin that the three *behave* the same, and the migration guide's
"Behaviour that differs" list is exactly what it does not pin — the exclusive methods failing rather
than waiting, Marten's strictly-greater revision guard rather than Polecat's equality one, a stricter
`QueryForNonStaleData`, ordinal string comparison, applied inner-side join predicates. Read as
equivalence, "passes all 40 suites" invites using Fisher as a test double for a Marten or Polecat
application, which the divergence list argues against.

**The bump crossed three releases and the whole compliance delta is one test.** 2.54.0 and 2.55.0
changed no suite file; 2.56.0 added `usage_describes_the_registered_projections` to
`EventStoreExplorerCompliance`, taking it from 6 to 7 and the event half from 250 to 251. No suite
was added, no seam member was needed — `ComplianceStoreConfig` and `IComplianceStoreRegistrar` are
byte-identical across the three, and the suite registers its `VoyageSnapshot` through the
`config.Snapshot<T>(SnapshotLifecycle.Inline)` that was already there.

**That one test is fisher#120's regression guard, and it is worth knowing why it is shared rather
than Fisher's** (jasperfx#700). `TryCreateUsage` had exactly one shared test and it asserted on
`usage.Events` alone, so a store could fill that list, leave every other slot on `EventStoreUsage`
empty, and pass the whole suite — which is what Fisher did for several releases, describing none of
its twenty registered projections to `projections list`, `projections rebuild` or CritterWatch.
Nothing about the omission was dialect-specific, so there was no store-level decision anywhere to
prompt somebody to look. It is registered **Inline** on purpose: an implementation describing the
daemon's shards rather than the registrations would look correct and still answer nothing for an
inline-only store, which is exactly the shape that went unreported.

**2.49.0 added no suite file and still required production work**, which is the first bump of that
shape: `DocumentLoadAndStoreCompliance` gained three tests for `LoadAsync<T>(object)` (jasperfx#665 /
fisher#89) and `DocumentComplianceConfig` gained `ValueTypes`. Diffing the suite *list* would have
reported a clean bump — diff the contents.

The library is now two halves. The **event sourcing** half is 43 enrolled suites and 447 tests, and the
upstream backlog it emptied in 2.45.0 refilled in 2.64.0 — see "Wave 13" below. The
**document** half arrived in 2.47.0 (jasperfx#647) and is now seven suites, 69 tests, over the
store-agnostic document contract Fisher implements for fisher#68. Every suite added since 2.49.0 has
landed in that half rather than the event one. It exists because the document side had no shared
definition at all — Fisher's document parity with Polecat was established by the one-time
hand-comparison that filed #22–#50, and enrolling replaces it with something standing.

The ten suites added since 2.39.5 divided cleanly into "already true" and "had to be built", and the
ratio is worth noticing — eight of the ten cost nothing, because they arrived after Fisher had already
been built to the sibling's shape:

| New suite | Arrived | What it cost |
|---|---|---|
| `StringStreamIdentityCompliance` | 2.40.0 | Nothing. 19 tests green on the bump alone — `StreamIdentity.AsString` was already built to the shape the suite expects. |
| `SnapshotLifecycleCompliance` | 2.40.0 | Nothing. 6 tests green on the bump alone; inline and async snapshots already agreed. |
| `MultiStreamProjectionCompliance` | 2.40.0 | One file. `MultiStreamProjection<TDoc, TId>` closes JasperFx's shared base over Fisher's session pair, and all 10 tests passed first run — slicing, `Identities`, `FanOut`, inline and async. The document-storage work that let a projection key on a string paid for this. |
| `FlatTableProjectionCompliance` | 2.41.0 | A real feature: `Projections/Flattened/`, ~700 lines. See CLAUDE.md. |
| `StrongTypedIdentityCompliance` | 2.42.0 | A real feature, fisher#14: `Storage/StrongTypedId.cs` and `StrongTypedIdentification`. No new seam was needed — `IIdentification<TDoc,TId>` had already reserved the three members for it. |
| `EventDataMaskingCompliance` | 2.43.0 | Three seam members, no production change. 10 tests green on the bump. |
| `StreamCompactingCompliance` | 2.43.0 | Nothing at all. 11 tests green on the bump; the member was already on the shared operations surface. |
| `RebuildAndCatchUpCompliance` | 2.44.0 | Nothing at all. 11 tests green on the bump; the whole rebuild surface is on `IProjectionDaemon`. |
| `DeadLetterCompliance` | 2.44.0 | Nothing at all. 6 tests green on the bump; `ContinuousErrors` and `QueryDeadLetterEventsAsync` were already there for the daemon. |
| `ConjoinedEventTenancyCompliance` | 2.45.0 | One seam member, no production change. 8 tests green on the bump — and the first suite to test a Fisher feature nothing cross-store had covered. |
| `SubscriptionCompliance` | 2.45.0 | One registrar member and a small partial, no production change. 6 tests green on the bump; fisher#21 had already built it to the shape. |

| `DocumentQueryCompliance` | 2.47.0 | Nothing beyond the contract binding below. 17 tests green on the first run. **Deliberately not a LINQ conformance suite** — it pins the minimum translatable set `Query<T>()` promises, and upstream's position that LINQ is out of shared-compliance scope permanently is unchanged. |
| `DocumentDeleteCompliance` | 2.47.0 | Nothing. 10 tests, first run. |
| `DocumentLoadAndStoreCompliance` | 2.47.0 | Nothing. 8 tests, first run. |
| `DocumentSessionCompliance` | 2.47.0 | Nothing. 7 tests, first run. |

The four document suites shared one cost between them rather than each paying: four interface
declarations, one partial class on the query provider, and one constraint widening — Fisher's
by-identity document read surface was `where T : class` where the contract is `where T : notnull`.
Widening it removed an inconsistency rather than creating one, since `Store`, `Delete`, `DeleteWhere`
and `Query<T>` were already `notnull`. See "The store-agnostic document contract" in CLAUDE.md.

### Wave 13 — the first suites to run anywhere (fisher#184)

**2.64.0 added eight event sourcing suites, deepened `SubscriptionCompliance`, and widened four more —
and not one of them had ever been executed against a real event store.** The JasperFx repository
enrols only the document suites, so the whole wave arrived compile-checked and design-reasoned.
Fisher enrolling them is first-contact runtime validation, and that changes what a failure means: a
red fact here is as likely to be an over-tight assertion as a store bug, so every one was classified
rather than assumed. Nineteen were red on the first run, in six groups.

**Five were genuine Fisher bugs, all fixed here.** Each had the same shape — a correct-looking
implementation nothing local had reason to question:

- **`AlwaysEnforceConsistency` did nothing** (jasperfx#762). `AppendPlanner.CollectActionableStreams`
  kept only streams with at least one event, so a stream fetched for writing, flagged, and then left
  alone was dropped from the unit of work along with its version guard. The flag is on the shared
  `IEventStream`, Fisher forwarded it to the `StreamAction` faithfully, and nothing else ever read
  it. Invisible to any ordinary append test, because appending one event brings the ordinary guard
  back — the empty case is the whole subject of the flag.
- **Appending to an archived stream landed** rather than being rejected. Every one of Fisher's own
  archiving tests checks the flag and the reads; none of them tried to write afterwards.
  `Exceptions.ArchivedStreamException` is the refusal, raised from the planner — and deliberately
  *not* for a `StartStream`, where an archived id is still an id in use and the collision is the more
  useful answer.
- **`FisherProjectionStorage.ArchiveStream` was an empty method**, with a comment reasoning about the
  wrong question: it said archiving leaves the snapshot alone and a projection wanting its document
  removed says so with `ShouldDelete` — both true, and neither what the seam asks for. It means
  *archive the stream*, and both siblings queue their archive operation there.
- **`FetchLatest` by natural key threw on a miss** where the contract is null, which made the
  key-shaped spelling the one member of the family that could not answer "does this aggregate
  exist?". `FetchForWriting` still throws, and jasperfx#764 deliberately leaves that miss out of
  shared scope because the three stores genuinely disagree about it.
- **Renaming a natural key left the old row behind**, so the superseded identifier resolved forever
  and its slot in the lookup's primary key could never be claimed by another stream. Both siblings
  reframed the same behaviour as a defect (polecat#435 / marten#5041); Fisher was the third.

**Two were upstream bugs**, filed and fixed in `jasperfx` — see "Red" at the top of this file for
what they are and why they are still red here.

**Three suites are enrolled and gated off** for a LINQ surface Fisher does not have, listed with the
header above. **One is not enrolled**: `UpcastingCompliance`, which is the next node's work.

**The rest were green on the bump**, which is the part worth stating plainly, since it is the larger
number: `ProjectionScenarioCompliance` (20) and `ProjectionCoordinatorCompliance` (6) both passed
first run. The second is the one to notice — it exists *because of* fisher#138, where Fisher
registered only an `IHostedService` over a class implementing nothing else and both documented routes
to the running daemon failed while all 37 suites passed. Its pause/resume fact targets exactly the
`StartAsync` bug that had, and it is green, which is the shared confirmation fisher#138's local tests
could not be.

Two features were built to enrol rather than to gate: `Fisher.Batching.FetchStreamStatePlan` and
`FetchStreamPlan` (parity with polecat#370), two small classes that make `StreamQueryPlanCompliance`'s
13 facts real; and the registrar's `UseMessageOutbox` plus the `RecordingMessageOutbox` partial, which
is what lets `ProjectionSideEffectCompliance` assert a *nonzero* publish count rather than facts that
are all vacuously true of a store that dropped every message.

**Four local test files gave facts up to the shared suites**, each noted at the site rather than
deleted silently: `projection_scenarios.cs` entirely (4 facts), four of `projection_coordinator.cs`'s
seven, two of `natural_keys.cs`'s twelve and two of `subscriptions.cs`'s eight. What stayed in each
case is what is Fisher's alone — the `ResetAllDataAsync` daemon handling, `FetchForWritingByNaturalKey`
and `UnknownNaturalKeyException` (which jasperfx#764 excludes on purpose), the subscription wrapper's
naming.

**Green on all fifty is not the same as feature-complete.** The suites cover what is portable
across stores; "Deliberate gaps" below is still the honest list of what Fisher does not do.

### Green — 50 suites, 516 tests

Event sourcing — 43 suites, 447 tests:

| Suite | Tests |
|---|---|
| `EventQueryCompliance` | 41 |
| `DcbTagQueryAndConsistencyCompliance` | 28 |
| `ProjectionScenarioCompliance` | 20 |
| `StringStreamIdentityCompliance` | 19 |
| `NaturalKeyCompliance` | 17 |
| `StreamArchivingCompliance` | 16 |
| `StreamStateQueryCompliance` | 15 |
| `AggregateWriteCacheCompliance` | 14 |
| `EventStoreExplorerCompliance` | 14 |
| `StrongTypedIdentityCompliance` | 14 |
| `FetchForWritingCompliance` | 13 |
| `StreamQueryPlanCompliance` | 13 |
| `FetchLatestCompliance` | 12 |
| `AlwaysEnforceConsistencyCompliance` | 11 |
| `ConjoinedEventTenancyCompliance` | 11 |
| `EventDataMaskingCompliance` | 11 |
| `RebuildAndCatchUpCompliance` | 11 |
| `StreamCompactingCompliance` | 11 |
| `StreamReadCompliance` | 11 |
| `SubscriptionCompliance` | 11 |
| `FlatTableProjectionCompliance` | 10 |
| `MultiStreamProjectionCompliance` | 10 |
| `EventMetadataCompliance` | 9 |
| `ProjectionSideEffectCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `LiveAggregationCompliance` | 7 |
| `UpcastingCompliance` | 7 |
| `AssignTagWhereCompliance` | 6 |
| `BinaryEventSerializationCompliance` | 6 |
| `DcbHasTagLinqCompliance` | 6 |
| `DeadLetterCompliance` | 6 |
| `ProjectionCoordinatorCompliance` | 6 |
| `SnapshotLifecycleCompliance` | 6 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `AggregateToLinqOperatorCompliance` | 5 |
| `AggregateToManyCompliance` | 5 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `CompositeProjectionCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `EventProjectionRegistrationCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |

Documents — 7 suites, 69 tests, through `FisherDocumentComplianceFixture`:

| Suite | Tests |
|---|---|
| `DocumentQueryCompliance` | 17 |
| `DocumentLoadAndStoreCompliance` | 11 |
| `DocumentCommitListenerCompliance` | 10 |
| `DocumentDeleteCompliance` | 10 |
| `PendingStreamActionsCompliance` | 9 |
| `DocumentSessionCompliance` | 7 |
| `DocumentSessionEventsCompliance` | 5 |

### Nothing in the fixture throws any more

`FisherComplianceFixture` implements every member and none of them throws. `LoadDocumentAsync` for a
strongly typed id was the last one, and fisher#14 closed it — it now closes the two-parameter
`LoadAsync<T, TId>` over the wrapper's runtime type by reflection, because the suite hands the id over
as `object` and its runtime type is the only thing naming `TId`.

The discipline still stands for the next seam member that arrives ahead of the feature: a member
Fisher cannot honour throws naming its milestone, so enrolling prematurely fails loudly rather than
passing on a stub.

`CreateBatch` went live with DCB tags, adapting Fisher's own `IBatchedQuery`. `EventStore` hands back
the `DocumentStore`. `StartDaemonAsync` builds one daemon per fixture and keeps it, so disposal can
stop it — a second daemon over the same file would mean two writers contending for one lock.

`QueryTableAsync` arrived with 2.41.0 and is the seam's only raw data access — a table name in, every
row out, deliberately predicate-free. Fisher's does the schema fold and converts a lowercase-canonical
Guid string back to a `Guid`, because SQL Server has `uniqueidentifier` and PostgreSQL has `uuid` while
SQLite has neither, so on Fisher *something* has to convert. See CLAUDE.md for why that is the honest
answer rather than a fudge.

`EventOperations.Unsupported.cs` is **gone**, and that is the milestone the file existed to mark. It
collected every `IEventStoreOperations` member Fisher threw on, one file on purpose so that its
shrinking was a visible progress measure; fisher#9 and fisher#10 took the last two (`OverwriteEvent`,
`CompletelyReplaceEvent`) out to `EventOperations.Rewriting.cs`, and the 2.43.0 pass found the file
holding nothing but an unreferenced `const string`. **Nothing on `IEventStoreOperations` throws any
more.** If a future JasperFx release widens the interface past what Fisher implements, bring the file
back rather than scattering throws through the partials.

### The rebuild concurrency cap

`StoreOptions.MaxPoolSize` is a Fisher-specific knob and the one place this milestone diverges from
the siblings. Marten and Polecat derive the cap from a real connection-pool ceiling — Npgsql's and
SqlClient's `Max Pool Size` keyword. `Microsoft.Data.Sqlite` has no such keyword; its
`SqliteConnectionStringBuilder` exposes only a boolean `Pooling`. So the ceiling is a store option and
nothing folds it into the connection string.

Its default of 8 is chosen for the cap it produces rather than as a pooling recommendation:
`max(1, 8 / 8)` is 1, and one is the honest answer for SQLite, where writers serialize at the file
level and concurrent rebuild cells contend for the same write lock instead of parallelising. The
shared suite only ever sets the ceiling explicitly, so `Events/rebuild_concurrency_cap.cs` is what
pins the default.

### The `IEventStore` surface

`DocumentStore` implements JasperFx's `IEventStore` explicitly, in `DocumentStore.EventStore.cs`, so
none of it lands on the store's own public API. Most of the interface is default-implemented by
JasperFx and left alone. Fisher overrides the two explorer reads it can answer out of `fi_streams`
(`GetRecentStreamsAsync`, `GetStreamMetadataAsync`) and supplies `TryCreateUsage`; the required
one required member it cannot honour — `OpenReadOnlyEventStore`
([fisher#15](https://github.com/JasperFx/fisher/issues/15)) — throws naming its milestone rather
than returning an empty result a monitoring tool would render as "no data". The 2.43.0 pass found
that member's doc comment stale: it blamed a missing paged event query layer, and fisher#9 had
already built one. `EventQuery` is a flat bag of exact-match filters plus paging, so what is actually
left is the filter-to-SQL mapping, `limit`/`offset` and a `count(*)`. That is now the only
throw of its kind left anywhere in Fisher. `BuildProjectionDaemonAsync` and `CompactStreamAsync`
(fisher#10) were both on that list and are both real.

Two SQLite-specific things the shared suite does **not** cover, pinned by
`src/Fisher.Tests/Events/event_store_explorer.cs` instead:

- **`GetStreamMetadataAsync` normalises Guid casing.** `fi_streams.id` holds the lowercase canonical
  form and SQLite's default collation is case-sensitive, so an uppercase Guid string matches nothing.
  The compliance suite only ever passes `Guid.ToString()`, which is already lowercase, so it would
  pass either way. `stream_metadata_is_found_regardless_of_guid_casing` fails without the parse.
- **Recent-stream ordering is a string sort** over ISO-8601 TEXT, correct only while
  `SqliteTimestamp.Format` stays fixed-width, UTC and millisecond-precision.

## The async daemon

Landed across five increments, each built, tested and committed before the next began:

| Increment | What | Commit |
|---|---|---|
| 1 | `IEventDatabase` on `FisherDatabase` — progress reads/writes, highest sequence, timestamp floor, non-stale wait | `1a0e15e` |
| 2 | `FisherHighWaterDetector` | `f5d5cb8` |
| 3 | `FisherEventLoader` + `FisherProjectionBatch` | `dc42709` |
| 4 | `IEventStore<IDocumentSession, IQuerySession>` on `DocumentStore` + `FisherProjectionDaemon` | `Daemon steps 4-5` |
| 5 | `SnapshotLifecycle.Async`, the fixture's daemon pair, enrollment | `Daemon steps 4-5` |

### The scale is not what the test count suggests

Two compliance tests, but they demand the whole daemon: start it, catch up, persist a snapshot, and
rebuild. The machinery is JasperFx's — coordinator, subscription agents, shard tracker, throttled and
resilient loaders, roughly 10,500 lines. **What a store supplies is the storage seam**, which is the
five types above; `FisherProjectionDaemon` itself is a dozen lines closing
`JasperFxAsyncDaemon<,,>` over Fisher's session pair, exactly as Polecat's is.

### `DocumentStore.Daemon.cs` ignores the `IEventDatabase` it is handed

Every member of the generic interface takes an `IEventDatabase` and every one of them works against
`Database` instead. That is not laziness: Marten and Polecat resolve a connection string off that
parameter because they can be database-per-tenant, and a SQLite store is one file. If Fisher ever
grows separate-database tenancy, these are the methods that have to start reading it.

### Teardown's missing-table check has to be in C#

A rebuild clears the projection's documents before replaying, and the table may not exist —
Fisher creates document tables on demand. The trap is that SQLite resolves a table name when it
**prepares** the statement, so `delete from t where exists (select 1 from sqlite_master ...)` fails
before the guard ever runs. The names come back from `sqlite_master` first and the delete is skipped
in C#. `rebuilding_when_the_projection_table_does_not_exist` pins it, and was verified by removing
the check — it fails with `no such table`.

Teardown runs both halves in one transaction. Deleting the progression and then failing before
clearing the documents would leave a projection replaying from zero on top of rows it already wrote,
which is the exact double-application a rebuild exists to avoid.

### The daemon warns about WAL rather than refusing to start

WAL is what lets the daemon read while a session writes; without it SQLite blocks readers for the
duration of a write, so the daemon and every application session serialize against each other. It is
on by default through `SqlitePragmaSettings.Default`, but a consumer replacing
`StoreOptions.PragmaSettings` can turn it off, and the result presents as a slow projection rather
than a misconfiguration. `BuildProjectionDaemonAsync` logs a warning; refusing to start would be the
stronger position and is not what is there.

### Async is genuinely async, and there is a test that says so

`Snapshot<T>(SnapshotLifecycle.Async)` used to throw. If lifting that had registered the projection
as Inline instead, `AsyncDaemonCompliance` would still pass — the document would already be there
when the daemon was asked for it. `an_async_snapshot_is_not_written_by_the_commit_that_appended_the_events`
asserts its *absence* before the daemon runs, which is what tells the two apart.

### The finding that shaped increment 2

**Committed `seq_id`s are contiguous on SQLite, so there is no high-water gap problem at all.** Both
halves verified against 3.51 rather than assumed:

- **Writers are serialized.** One writer per file plus `BEGIN IMMEDIATE` means a transaction's
  sequences fully commit before the next writer allocates any — no interleaving, so no hole.
- **A rollback does not consume the sequence.** SQLite keeps the `AUTOINCREMENT` counter in
  `sqlite_sequence`, an ordinary table that rolls back with the transaction. After a rolled-back
  two-row insert, the next insert reuses the number the failed one had.

Marten and Polecat must distinguish the highest sequence *issued* from the highest safe to *read*,
because a PostgreSQL sequence or SQL Server IDENTITY hands out numbers outside the transaction — a
writer can hold 7 uncommitted while 8 commits ahead of it, so reading to 8 would skip 7 forever.
Their safe-zone polling, stale-gap skipping and `SafeStartMark` machinery exists for that.

So the mark simply **is** `max(seq_id)`, and `DetectInSafeZone` has no separate answer to give.
**Do not reintroduce gap-skipping on the assumption Fisher must need it too** — it would guard a
state that cannot occur. The class comment says so as well.

`a_rolled_back_append_leaves_no_gap_in_the_sequence` is written at raw SQL deliberately: failing a
Fisher append instead proves nothing, because if the failure came before any row was inserted no
sequence was consumed and the assertion holds either way. It inserts a row, **asserts that row took
sequence 4**, rolls back, then requires the next real append to reuse 4.

### The batch must stay atomic

`FisherProjectionBatch` commits the projection's document writes **and** the progression row in one
transaction. Splitting them lets a crash between them either replay events already applied or skip
events never applied — the projection ends up permanently wrong in one direction or the other, with
nothing to signal it.

Sessions are collected rather than merged, and each flushes its *own* operations into the shared
transaction (`FisherSession.FlushOperationsAsync`), because an operation is configured against a
session as its storage context and that is what carries tenancy. Running one session's operations
through another would quietly mis-scope them.

### The loader diverges from the stream reads on purpose

Fisher's stream reads skip an unresolvable `dotnet_type` unconditionally, so a deployment can still
read events it does not know about. **The daemon must not** — silently skipping would leave the
projection permanently wrong with no signal. It honours `SkipUnknownEvents`, and otherwise throws
Fisher's own `UnknownEventTypeException`, which implements JasperFx's `IEventFailureContext` so the
daemon can classify the shard failure and name the offending sequence without knowing Fisher's
exception types. JasperFx owns only `ApplyEventException`; read-side failures belong to the store, as
in both siblings.

### The hazard that stays live

**`AUTOINCREMENT` on `fi_events.seq_id` is load-bearing, not decorative.** A bare
`INTEGER PRIMARY KEY` aliases the rowid, which SQLite **reuses** after a delete. A reused sequence
would sit below the high-water mark and be invisible to every async projection. It is also half of
why the contiguity argument above holds.

### Known gaps in what is built

Deliberate, and each throws by name rather than failing quietly. Each is tracked as an issue rather
than only as a note here:

**Nothing in the daemon throws any more.** What is left is one open question rather than a gap:

- **A message bus** — [fisher#8](https://github.com/JasperFx/fisher/issues/8). The side-effect seam
  is built, but the default outbox drops every message and Fisher ships no delivery mechanism.
  Whether that stays Fisher's answer (as it is Marten's and Polecat's) is the question on the issue.

[fisher#3](https://github.com/JasperFx/fisher/issues/3) (event-emitting projections),
[fisher#4](https://github.com/JasperFx/fisher/issues/4) (side effects) and
[fisher#5](https://github.com/JasperFx/fisher/issues/5) (dead letters) are all **closed** — see below.

### Event-emitting async projections

JasperFx drives three synchronous members on the batch. **All three do the same thing: record the
`StreamAction` and let `ExecuteAsync` plan it inside the transaction.** They can, because the action
JasperFx hands over already carries every raised event, and the single-stream-start path passes the
same action instance to each call — reference identity dedupes them.

Marten queues three different storage operations instead. That is right for Postgres and wrong here,
for two SQLite reasons: the version has to come from a read under the write lock (the slice
pre-assigns client-side from its own event count, which only matches when the projection has seen the
whole stream), and routing through `FisherQuickAppendEventsOperation` is the only thing that supplies
the `seq_id` a tag row is keyed by. Queueing Weasel's bare per-event operations would have made
raised events silently untaggable.

**Polecat no-ops these three members rather than throwing**, so an event-raising projection there
drops its events with no signal at all. Worth reporting upstream; do not copy it.

### Projection side effects

`IMessageOutbox` vends an `IMessageBatch` per unit of work, and both commit paths bracket their
transaction with its two hooks. Type names match Polecat's exactly — messaging is not dialect-specific,
so projection code ports between the stores unchanged.

**The thing worth knowing is what the tests pin, because the obvious test does not.** Recording the
hook order proves nothing: `before` then `after` is the order even if both run before the commit. The
invariant is what the rest of the database can see when each fires, so each hook probes the committed
state over a *separate* connection — invisible at `BeforeCommit`, visible at `AfterCommit`. The
hook-order test passed with `AfterCommitAsync` deliberately moved to before the commit; the probe
does not. Both commit paths have their own.

A related trap already avoided: `AfterCommitAsync` runs *outside* the resilience pipeline in the
projection batch. A retried `SQLITE_BUSY` re-executes the whole delegate, so a post-commit publish
inside it would fire twice for a transaction that had already committed.

The same property caught the batch's *input* too, and that one was silent —
[fisher#12](https://github.com/JasperFx/fisher/issues/12). `FlushOperationsAsync` drained each
session's queue as it executed, inside the retried delegate, so an attempt that failed after the
flush left the retry with nothing to write while the progression row still committed: a projection
advanced past events whose documents were never written, with no exception, no shard failure and no
dead letter. Fixed by taking the operations once before the pipeline and executing that snapshot
inside it. `a_retried_projection_batch_still_writes_its_documents` injects the failure through the
outbox's `BeforeCommitAsync`, because the injection point has to be after the flush and inside the
delegate — a competing writer fails the `BEGIN IMMEDIATE`, which is before it.

### Dead letters

`fi_dead_letters` carries `DeadLetterEvent`'s columns one for one, so CritterWatch reads Fisher's the
same way it reads Marten's. `SkipApplyErrors` now works: a poison event is quarantined and its shard
carries on, which `a_skipped_poison_event_is_quarantined_and_the_shard_carries_on` covers end to end
rather than only at the storage layer.

Three decisions worth not undoing:

- **No foreign key to `fi_events`** — deliberately the opposite of the tag tables. A dead letter has
  to survive the event being archived, compacted or cleaned away, or a cascade erases the evidence
  somebody came looking for.
- **The write is on its own connection**, outside the failing batch's transaction, which is about to
  roll back. Writing it inside would roll the record back with the failure it records.
- **It is an upsert.** The daemon retries the write in the background against a pre-assigned id.

That "no foreign key" choice is also why the cleaner has to delete them: nothing else ever would.

### Two bugs found while building the above

**[fisher#6](https://github.com/JasperFx/fisher/issues/6)** — `DeleteAllEventDataAsync` failed with
`FOREIGN KEY constraint failed` whenever DCB tag rows existed: tag tables have a real FK to
`fi_events(seq_id)` and were not being cleared. The suite never caught it because every fixture gets
a fresh database, so the clean always ran before any tag row existed. Fixed by deleting in a fixed
order, tags first.

**[fisher#7](https://github.com/JasperFx/fisher/issues/7)** — `WaitForNonStaleProjectionDataAsync`
translated only its `Task.Delay` cancellation into a `TimeoutException`. Its two reads take the same
token, so a timeout elapsing mid-query escaped as `OperationCanceledException` — the same condition
reported as two different exception types depending on timing alone. It surfaced as a roughly
1-in-8 flake on net9.0 under the full suite's load and would not reproduce in isolation, which is
exactly what the "run the suite more than once" convention exists to catch. The regression test uses
an already-elapsed timeout so the cancellation *must* come out of a read.

Both fixes were verified by reverting them: each new test fails without its fix.

## Flat-table projections — the one new feature 2.41.0 demanded

`FlatTableProjectionCompliance` is the only one of the four new suites Fisher could not pass by being
enrolled. It needs a real flat-table projection base, and it says so plainly: the shared partial
carries the table name, the projection name and every mapping, and each consumer supplies a small
partial with the constructor and the primary key column, because no single `base(...)` call satisfies
Marten, Polecat and Fisher. Fisher's half is three lines, in
`Compliance/ComplianceFlatTableProjection.Fisher.cs`.

`src/Fisher/Projections/Flattened/` is a port of Polecat's — the mapping API and the column-map shapes
are its. Four things are not, and each is a decision rather than a translation:

1. **One `insert … on conflict … do update` where Polecat emits a `MERGE`.** SQLite has had upsert
   syntax since 3.24, so matched and not-matched are two clauses of one statement, and a parameter
   appearing in both is bound once by name. **An unqualified column on the right of the update
   assignment is the pre-update row** — that is what makes `"a" = "a" + @p1` an increment, where
   `excluded."a"` would be what the insert branch would have written. Polecat spells it `target.[a]`.
2. **The table is created by the migration, not lazily on first write.** Registering the projection
   puts a `FlatTableFeatureSchema` into the store's feature set. Polecat issues a CREATE TABLE from
   inside its first apply, which works but routes around `AutoCreateSchemaObjects` — a store set to
   `AutoCreate.None` would still get DDL. `auto_create_none_leaves_the_table_alone` pins Fisher's.
3. **The physical name folds the store's logical schema in, resolved in `DocumentStore`'s
   constructor.** SQLite has no schemas, so the prefix *is* the isolation boundary between two logical
   stores in one file, and a flat table that kept its bare name would be silently shared by both. The
   projection's constructor cannot see the store and is usually registered in the same lambda that sets
   `DatabaseSchemaName`, in either order — so the fold waits until the options are final. The `fi_`
   family prefix is deliberately *not* applied: it marks a table Fisher owns the shape of, and a flat
   table's shape is the projection's. The rename needs `FlatTable : Table`, because
   `SchemaObjectBase.Identifier` has a protected setter and Weasel's `MoveToSchema` only changes the
   qualifier.
4. **Rebuild teardown is told the table name directly**, through `IPublishesTables`. `PublishedTypes()`
   is empty — a flat table's rows are not documents — so the mapped-type sweep in
   `TeardownExistingProjectionStateAsync` cannot see it, and without this a rebuild replays onto the
   rows the previous run left. The compliance suite catches exactly that, with a row whose events it
   archives so the replay cannot recreate it.

The Guid trap shows up here too, in the one place a flat table meets it: the primary key holds a stream
id, so it goes down through the lowercase-canonical conversion. Bound any other way, the second event
on a stream inserts a second row instead of updating the first —
`the_stream_id_key_is_stored_as_lowercase_canonical_text` is what would fail.

`MultiStreamProjection<TDoc, TId>`, by contrast, is one file that closes JasperFx's shared base over
Fisher's session pair, and all ten of its compliance tests passed on the first run — grouping,
`Identities`, `FanOut`, inline and async. The reason it was that cheap is that the document-storage
work already let a projection key on something other than the stream identity.

## Soft delete

The first item of "finish document storage", and the first feature since document storage landed that
no compliance suite asks for — the shared suites are event-store suites, so this one is pinned
entirely by `src/Fisher.Tests/Documents/soft_deleted_documents.cs` (18 tests). Three of them were
verified by reverting the thing they cover; see below.

Opting in has three spellings, all read once when the mapping is created: `[SoftDeleted]`,
implementing `JasperFx.Metadata.ISoftDeleted`, and `Schema.For<T>().SoftDeleted()`. The surface that
follows is Polecat's and Marten's — `HardDelete`, `DeleteWhere`, `HardDeleteWhere`,
`UndoDeleteWhere`, and `MaybeDeleted()` / `IsDeleted()` / `DeletedSince()` / `DeletedBefore()` on a
query — so soft-delete code ports between the stores unchanged. CLAUDE.md has the design; what is
worth carrying separately:

- **The one place this diverges from Polecat on purpose** is the `is_deleted = 0` guard on
  `DeleteWhere`. Polecat guards its by-id delete and not its criteria-based one, so re-deleting via a
  predicate moves `deleted_at` forward there. Fisher guards both, which makes "when was this deleted"
  mean the first deletion rather than the most recent call.
  `deleting_an_already_deleted_document_leaves_its_deletion_time_alone` plants an old timestamp
  before the second delete, because two deletes in the same millisecond would agree with or without
  the guard.
- **Undeleting is not a separate feature.** Storing a soft-deleted document brings it back, and that
  falls out of the upsert rather than being arranged: Weasel's soft-delete binders write the live
  values, and `do update set` assigns every column from `excluded.*`.
- **`ISoftDeleted`'s own members are populated on read**, but only where a read can see a deleted row
  at all. This was written up as a gap and closed by
  [fisher#11](https://github.com/JasperFx/fisher/issues/11): the interface now maps `Deleted` /
  `DeletedAt` onto the two columns, as it does `guid_version` and `last_modified` for `IVersioned`.
  Every ordinary load filters deleted rows out, so `Deleted` is observably true only through a query
  carrying `MaybeDeleted()` or `IsDeleted()`. The hazard fisher#11 named turned out to be real and is
  now pinned rather than avoided: Weasel's `DocumentSoftDeletedAtBinder` reads its column with
  `GetFieldValue<DateTimeOffset>`, which is the one place Fisher leans on Microsoft.Data.Sqlite's
  coercion instead of converting explicitly, and `metadata_column_coercions` is what fails by name if
  a provider upgrade changes it.
- **`DeletedSince` / `DeletedBefore` need none of fisher#1's `strftime` machinery.** `deleted_at` is
  `SqliteTimestamp`'s fixed-width UTC format, so text order is instant order — the same reason
  `fi_events.timestamp` sorts as text. A document's own `DateTimeOffset` member is whatever
  System.Text.Json wrote, which is why that one is the case that needed wrapping.
- The three reverts that were run: dropping the load SQL's `and is_deleted = 0` fails
  `a_deleted_document_is_invisible_to_load_and_load_many`; dropping the query layer's default filter
  fails three query tests; dropping the delete guard fails the deletion-time test above.

## Duplicated fields — fisher#2, closed, as generated columns

`Schema.For<T>().Duplicate(x => x.Name)` gives the member a column of its own and an index over it.
**The divergence from both siblings is that the column is a SQLite `VIRTUAL` generated column rather
than a written one**, which was the open question ROADMAP raised and is the reason the feature is
small: nothing writes it, so the write path, the positional `?` contract and the unit of work are all
untouched. It also cannot drift from `data`, and it needs no backfill — `duplicating_a_member_of_a_type_that_already_has_rows_needs_no_backfill`
adds the registration to a store whose rows predate it and reads the column straight back.

The tests worth knowing about, because two of them assert something SQLite decides rather than
something Fisher writes:

- **`the_planner_uses_the_index_for_a_duplicated_member` runs `EXPLAIN QUERY PLAN` over Fisher's own
  translation of the predicate.** Asserting on SQL text would only prove Fisher emitted a column
  name; this proves the planner reaches the index. `an_unduplicated_member_still_scans` is the
  contrast that keeps it honest.
- **`a_duplicated_timestamp_holds_the_normalised_form_and_orders_by_instant` is the fisher#1
  payoff.** The generated expression is the member's own `TypedLocator`, so a duplicated timestamp
  is `strftime(…)` — the column holds fixed-width UTC, sorts as text, and the index serves a range
  query. That was the specific cost fisher#1 introduced, and it is now indexable.

### The trap that nearly made this not work

**`pragma_table_info` does not list generated columns — only `pragma_table_xinfo` does.** Weasel's
delta detection uses the former, so a duplicated column reads as missing on every migration and the
patch re-adds it. Fisher runs a migration on the first write of each document type per process, so
the second one fails outright with `duplicate column name`. This is the normal path, not a corner.

Fisher carried a `DocumentTable.ConfigureQueryCommand` override for this, querying `table_xinfo`
instead. **It is gone**: [weasel#426](https://github.com/JasperFx/weasel/issues/426) shipped in
Weasel.Sqlite **9.24.0**, and the override was removed on the 9.25.0 bump.

Removing it was overdue rather than optional, which is the part worth keeping. The override and the
reader that consumes it are one contract, and 9.25.0 added a fifth statement (triggers) to the
metadata query while Fisher's copy still emitted four. That surfaced as
`ArgumentOutOfRangeException` from `readForeignKeysAsync` — a result-set misalignment naming neither
Fisher nor generated columns. A workaround kept past its fix does not sit harmless; it drifts from the
thing it was copied from, silently.

### `Schema.For<T>()` returns an expression now

`DocumentMappingExpression<T>`, not `DocumentMapping`, because `Duplicate(x => x.Name)` cannot infer
its document type from a lambda and the receiver has to carry it — the same reason Marten has one.
`.Mapping` is the way back to the mapping, and `SoftDeleted()`, `UseOptimisticConcurrency()`,
`MultiTenanted()` and `DocumentAlias()` are on the expression so ordinary configuration does not need
it. Existing call sites took `.Mapping`; there were seven.

## The LINQ layer

Ported from Polecat, which owns `Polecat.Linq.SqlGeneration` itself rather than taking it from
`Weasel.SqlServer` — so Fisher carrying its own is the mirror, and no upstream Weasel change is
needed. `Weasel.Sqlite` has no `SqlGeneration` namespace at all.

Committed:

1. **`Fisher.Linq.SqlGeneration`** — the where-fragment set. `Statement` is the one genuinely
   dialect-specific file; see below.
2. **`Fisher.Linq.Members`** — member locators over `json_extract`.
3. **`Fisher.Linq.Parsing`** — `WhereClauseParser`, `LinqQueryParser` and the method-call parsers.
4. **The provider** — `FisherQueryable<T>`, `FisherQueryProvider`, the async terminal operators, and
   `session.Query<T>()`.

**`session.Query<T>()` works.** Supported: `Where`, `OrderBy`/`OrderByDescending`/`ThenBy`/
`ThenByDescending`, `Take`, `Skip`, and the async terminals `ToListAsync`, `FirstAsync`/
`FirstOrDefaultAsync`, `SingleAsync`/`SingleOrDefaultAsync`, `CountAsync`/`LongCountAsync`,
`AnyAsync`. Anything else throws `BadLinqExpressionException` naming the operator.

`Joins`, `CursorPaging`, `SoftDeletes`, `GroupBySelectBuilder` and `SelectProjectionAnalyzer` remain
out of scope — they serve features Fisher does not have. So do `Select` projections and `GroupBy`;
`Statement` has no DISTINCT / GROUP BY / HAVING because nothing exercises them yet.

### The provider reuses the closed-shape storage rather than hand-writing SQL

`FisherQueryProvider.Build<T>` takes the column list from `ISelectClause.SelectFields()` and the
materializer from `ISelectClause.BuildSelector()`, both off the **query-only** storage flavour — the
seam CLAUDE.md notes was left in place for exactly this. That is what stops the query path's read
layout drifting away from `LoadAsync`'s. Predicates also go through `storage.FilterDocuments`, which
is a no-op today but is where a conjoined table's tenant filter lands; going around it is how a query
path silently stops honouring tenancy the moment tenancy arrives.

Two things worth knowing:

- **The provider is cached per session and uses the session's own connection**, which is why a query
  inside a unit of work sees writes that session already committed.
- **Counting a paged query wraps it as a subquery.** `Take(2).CountAsync()` must count the page, not
  the table. The subquery is modelled as a nested `Statement` rather than pre-rendered SQL so its
  parameters reach the same command builder. `counting_a_paged_query_counts_the_page` fails without
  it — verified by removing the wrap.
- **Synchronous enumeration throws.** There is no non-blocking way to serve `IEnumerator<T>` over an
  async read, and blocking inside `GetEnumerator` is how a library deadlocks a caller. Marten and
  Polecat refuse here too.

### Why the port is smaller than its source

`json_extract` does not behave like SQL Server's `JSON_VALUE`. Verified against SQLite 3.51 before
anything was built on it: it returns a JSON number as INTEGER, a float as REAL, a string as TEXT and
`true`/`false` as INTEGER 1/0. So `json_extract(data,'$.age') > 30` compares numerically with no
cast, and Polecat's whole `CAST`/`RETURNING` apparatus — `SqlTypeMap`, `BuildTypedLocator`,
`SupportsReturning`, the native-json-type switch — has no analogue. `TypedLocator` and `RawLocator`
are the same string.

### Paging is where the dialects actually diverge

`TOP(n)` and `OFFSET n ROWS FETCH NEXT m ROWS ONLY` collapse to `limit m offset n`. T-SQL needs an
ORDER BY before OFFSET and emits `ORDER BY (SELECT NULL)` as filler; SQLite does not, and inventing
one would impose a sort the caller never asked for. An offset with no limit must say `limit -1`
first — a bare `offset 2` is a parse error, verified.

### Dates — fisher#1, closed, and it did not need duplicated fields

Earlier handoffs said lifting this needed a normalised sortable duplicated column. It did not.
`TimestampMember.TypedLocator` is `strftime('%Y-%m-%dT%H:%M:%f', json_extract(...))`, which hands the
stored text to SQLite's own date parser: the trailing offset is folded into UTC and the result is
fixed-width to the millisecond, so it sorts as text. That is the same move Polecat makes with
`CAST(... AS datetimeoffset)` and Marten with `timestamptz`, spelled the way SQLite spells it.

Three decisions in it worth not relitigating:

- **Equality goes through the same normalisation as ordering**, not the exact serializer rendering it
  used before. Two spellings of one instant must not be equal for `>=` and unequal for `==`. The cost
  is that `==` discriminates only to the millisecond — `%f` has no sub-millisecond form — but
  `timestamptz` is microsecond precision, so the siblings truncate a `DateTimeOffset` too. This is
  closer to their behaviour, not further from it.
- **`DateOnly` and `TimeOnly` did not need it and did not get it.** A `DateOnly` is fixed-width
  `yyyy-MM-dd` with no offset and no fraction; a `TimeOnly`'s optional fraction is a strict suffix, so
  trimming shortens the string without changing which of two values compares smaller. They stay on
  `DateMember` and the bare locator.
- **A `DateTime` with `Kind.Unspecified` is not shifted.** STJ writes it with no offset and SQLite
  reads an offsetless string as already UTC, so converting the literal would move it off the values it
  is meant to match.

`querying_documents` pins it end to end with four documents whose *text* order and *instant* order
disagree at every position — that list is the test, and a locator that compared raw text would pass an
assertion built on the wrong one.

**`AllowsRangeComparison` survives**, because building this turned up a second unsortable member: a
string-stored enum. Under `EnumStorage.AsString` the stored value is the member's name, so
`x.Grade > Grade.Pass` and `OrderBy(x => x.Grade)` sorted alphabetically rather than by the enum's
declared order — quietly wrong, no signal. Both now refuse and name `EnumStorage` in the message.
Fisher's default is `AsInteger`, which orders correctly and is unaffected.

The per-row cost this introduced — `strftime` over `json_extract`, which no index could serve — is
what fisher#2 has now closed, and the prediction held: a **generated column** was the better shape
than a written one. `Duplicate(x => x.When)` declares a `VIRTUAL` column whose expression is this very
locator, so the duplication costs index space but not row space and cannot drift from `data`. See
"Duplicated fields" above.

This concerns documents only. The `fi_events` / `fi_streams` timestamp columns are
`SqliteTimestamp`'s fixed-width UTC format precisely so they *do* sort as text.

### String predicates use `instr`, not `LIKE`

The central decision in `Parsing/Methods`. Polecat translates `Contains`/`StartsWith`/`EndsWith` to
`LIKE` patterns; on SQLite that would be wrong twice, both verified against 3.51:

- **`LIKE` is case-insensitive for ASCII by default, while `=` is case-sensitive.** A LIKE-based
  `Contains("frodo")` matches `"Frodo"` on the very same data where `== "frodo"` does not — a query
  surface that contradicts itself, and not what .NET's ordinal `string.Contains` means.
- **`_` and `%` are `LIKE` wildcards**, so a literal needle containing either needs escaping.
  Polecat's `[_]` bracket escaping is T-SQL-only; SQLite needs an `ESCAPE` clause. This is the same
  trap CLAUDE.md records for the document cleaner. `instr` takes its needle literally.

So `Contains` is `instr(loc, ?) > 0`, `StartsWith` is `instr(loc, ?) = 1`, and `EndsWith` is
`substr(loc, -n) = ?` where `n` is the needle's length computed at translation time — the needle is a
constant, so this binds one parameter rather than two. An explicit `StringComparison` of
`OrdinalIgnoreCase` folds both sides with `lower()`. Empty-needle behaviour matches .NET: `instr`
returns 1 for an empty needle so `StartsWith("")` is true, and `EndsWith("")` is special-cased to
`1=1` because `substr(x, 0)` returns the whole string.

`x.Name.Length` uses SQLite's `length`, not SQL Server's `LEN` — `LEN` ignores trailing spaces and
`length` does not, which is what `string.Length` means.

### `Contains` over a collection has two bindings

`array.Contains(x)` binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T)` on modern .NET, not
`Enumerable.Contains` — so `EnumerableContains` matches on the *shape* of the call (a source plus a
document member) rather than on the declaring type. The span operand also cannot be evaluated by
compiling a lambda, because `ReadOnlySpan<T>` is a ref struct and cannot be returned as `object`;
`StripSpanConversion` unwraps back to the underlying array first. Three tests caught this.

### DCB tags

All 32 tag tests pass. The shape, and the parts that are SQLite-specific:

- **One `fi_event_tag_<suffix>` table per registered tag type**, composite primary key leading with
  `value` because a tag query filters on it. That key is also what makes tagging idempotent: both the
  append path and `AssignTagWhere` write `on conflict do nothing` rather than reading first.
- **Tags are written after the batch and inside its transaction.** A tag row is keyed by the `seq_id`
  SQLite assigns on insert, which Fisher only learns from the append's trailing sequence read-back —
  so there is nothing to write until the appends postprocess. Committing separately would leave an
  event visible but untagged, which a tag query cannot tell apart from never-tagged.
- **Queries use `seq_id in (select …)` subselects, not joins.** Joining several tag tables multiplies
  rows when one event carries two matching tags, and the caller expects each event once.
- **Ordering is by `seq_id`** — a tag query spans streams, so version is not a global order.
- **Guid tag values bind as lowercase canonical text.** The raw Guid writes a BLOB and
  Microsoft.Data.Sqlite's string form is uppercase; under the case-sensitive default collation either
  writes a row that can never be read back.

`AssignTagWhere` is a **client of the LINQ `WhereClauseParser`**, exactly as Marten builds it. The
only new piece was `EventMemberFactory`, an `IMemberResolver` resolving `IEvent` members to
`fi_events` columns instead of `json_extract` paths — which is why `IMemberResolver` is an interface.
All six of its compliance tests passed on the first run with no translator written for them.

One asymmetry worth knowing: **`IEvent.Timestamp` allows range comparison where a document's
`DateTimeOffset` member does not.** Same CLR type, different storage — `fi_events.timestamp` is
`SqliteTimestamp`'s fixed-width UTC format, chosen so a string comparison *is* an instant comparison.

### The DCB consistency check

`FetchForWritingByTags` records the highest sequence it saw; `SaveChangesAsync` re-runs the query
**inside its write transaction, before anything is written**, and throws `DcbConcurrencyException` if
anything matching has landed since. Both halves matter: checking after the write would be checking
against our own appends, and checking outside the transaction would prove nothing because
`BEGIN IMMEDIATE` is what holds the write lock.

A boundary over an *empty* result still enforces consistency — `LastSeenSequence` is 0, and any
matching event appearing later has a sequence above it. That is what makes a boundary usable as a
"this must not exist yet" assertion.

### A boundary aggregate needs no identity

An aggregate reached only through a tag boundary is keyed to no stream, so `[BoundaryAggregate]`
(`JasperFx.Events.Aggregation`) is an explicit opt-out of single-stream identity and
`AggregateIdentity.ResolveIdType` answers `typeof(string)` for one — the vestigial `TId` the source
generator already keys a marked type's evolver on. Before #135 Fisher required the `Id` anyway.

**Fisher was the first Critter Stack store to honour it.** #135 filed this as a Fisher-only divergence
because Polecat's DCB page documents the marker as the cross-stack answer — but Polecat's source never
mentioned it, and an identity-less boundary aggregate threw there too, out of `DocumentMapping`'s
constructor. Confirmed by running it against the Polecat tree, and filed as polecat#521, which shipped
in Polecat 5.21.0 a day later. So the marker now behaves the same on both; what Fisher aligned with was
the attribute's documented contract rather than a sibling.

**That gap is invisible from the empty-boundary path**, which is what made it late-breaking rather
than obvious: `FetchForWritingByTags` folds only when the query finds events, so the "this must not
exist yet" assertion above worked either way and the throw arrived on first real use. The shared suite
does not catch it either — every DCB aggregate in it happens to carry an identity, which is filed as
jasperfx#718. An unmarked
identity-less aggregate is still refused, and the message now names the boundary case rather than
sending its author after an `Id` their model has no use for.

### Batched queries exist for parity, not for speed

`IBatchedQuery` matches the siblings' shape — declared reads, tasks that do not complete until
`Execute`. **It buys Fisher essentially nothing in throughput, and that is understood rather than
unfinished.** In Marten and Polecat a batch collapses several network round trips; SQLite is
embedded, so there are none to collapse. It is carried so DCB code ports between Critter Stack stores
unchanged, and so Fisher can enroll in the shared batched-query tests with a real implementation
rather than a test-only shim.

The alternative was considered and rejected: `DcbTagQueryAndConsistencyCompliance` is a single class
with no supported-flag opt-out, so declining batching would have cost all 26 of its tests, not the 4
that touch a batch.

Implemented without statement coalescing on purpose. The one property that does hold is that the
reads run back to back on one connection with nothing interleaved, so a set of boundaries is
established against a coherent view.

### Conversions that fail silently

Every one of these returns *no rows* rather than an error, which is why they are pinned by tests that
run generated locators against genuinely stored documents:

- A bool binds as INTEGER 1/0, not the `"true"`/`"false"` strings `JSON_VALUE`'s nvarchar needs.
- A Guid binds as lowercase canonical text. The raw `Guid` writes a 16-byte BLOB; an uppercase string
  misses under the case-sensitive default collation.
- The JSON path uses the **serializer's** naming policy — `Name` is `$.name` under Fisher's camelCase
  default. An explicit `[JsonPropertyName]` wins verbatim.
- AsString enums are cased by the naming policy, since the serializer wires
  `JsonStringEnumConverter(PropertyNamingPolicy)`.

## Deliberate gaps, so you don't mistake them for bugs

Each of these is a decision with a reason, not an oversight:

> ⚠️ **This section is not the whole gap list, and the README no longer claims it is.** Everything
> below is a *decision*; there is a separate set of Marten features that are simply **absent** —
> `Include()`, full-text search, a session logging seam, `MatchesSql`, `Stats(out QueryStatistics)`,
> `ToAsyncEnumerable()`, and child-collection LINQ only partly. They were missing from this section for
> a structural reason worth knowing: **Fisher's parity baseline was drawn against Polecat**
> (`polecat-gaps.md`), so a feature Marten has and Polecat does not never entered the tracking at all.
> The Marten-facing list lives in `docs/migration-guide.md` under "Marten features Fisher does not
> have" and is the one to keep current. Compiled queries are the one member of that set that *has* been
> decided, on a measurement — [fisher#195](https://github.com/JasperFx/fisher/issues/195).

- **Exclusive appends are the optimistic ones.** `AppendExclusive`, `FetchForExclusiveWriting` and
  `WriteExclusivelyToAggregate` do not lock. SQLite has no row lock; the faithful equivalent would
  hold `BEGIN IMMEDIATE` from fetch to commit, blocking every other writer for as long as a caller
  holds a session. Safety is unchanged — the version guard still runs inside the write transaction —
  but a loser fails instead of waiting. **This is the most likely place a future compliance suite
  disagrees with Fisher.**
- **Hi-Lo gaps are expected, not a bug.** A process that stops mid-allocation abandons the rest of
  its `MaxLo` range, and `SetFloor` rounds up to a whole page. Both match Marten and Polecat.
- **The LINQ surface refuses rather than falling back to client-side evaluation**, which is the
  invariant, not the size of the surface — that has grown a long way past filtering, ordering and
  paging (`Select` and `Distinct` in #23, `GroupBy` and `HAVING` in #24, joins in #25, the aggregates
  in #22 and #54, both pagings in #27, chained joins in #55). What is still refused is refused *by
  name*, with the alternative: `Include`, and everything listed under the join and projection sections
  above.
- **No ordering or range comparison on a string-stored enum.** See the LINQ section — the stored form
  is the member's name, so it sorts alphabetically rather than by declared order. Timestamps used to
  be on this list and no longer are (fisher#1).
- **No message bus.** The side-effect seam is real and both commit paths bracket their transaction,
  but the default `NulloMessageOutbox` drops every message, so nothing in the box delivers one —
  [fisher#8](https://github.com/JasperFx/fisher/issues/8). That is the sibling behaviour, not a stub.
- **Event rewriting does not reach anything derived from the events.** All of it is built —
  `OverwriteEvent` / `CompletelyReplaceEvent`, masking (fisher#9) and stream compacting (fisher#10) —
  and all of it shares one hazard, which is a documented property rather than a gap: the daemon's
  high-water mark is a sequence and a rewrite does not move it, so an async projection that has
  already passed the event keeps what it derived from the old body until it is rebuilt. Marten is the
  same. This is why masking is a data-at-rest operation rather than a correction, and why compacting
  is one-way.
- **A composite projection's stages cannot read each other's writes**, which is the one thing about
  composites that reads like a gap and is not. Composite projections themselves shipped (#19): several
  projections as ordered stages under one shard, rebuilt together in one pass. What a stage boundary
  does *not* buy is a later stage seeing an earlier one's rows through `LoadAsync` — the whole composite
  commits as one batch, so nothing an earlier stage queued is in the database yet. JasperFx's mechanism
  for sharing across stages is the aggregate cache, which aggregation projections participate in and a
  bare `IProjection` does not. Composites are also always asynchronous, deliberately: a stage boundary
  only means something inside a daemon batch.
- **Nothing on `IEventStoreOperations` is partial any more.** Natural keys were the last of it (#40);
  bulk insert and strong-typed ids came off this line earlier (#36, #53, #14).
- **`dotnet_type` is the one metadata column with nowhere to go.** Every other one is projectable onto
  a document member (fisher#11, widened by fisher#29); Weasel's `DocumentDotNetTypeBinder` takes no
  member where every other binder does, so `DocumentMetadata` omits it rather than offering a mapping
  that would silently do nothing. That is an upstream gap, not a Fisher decision — and
  `MetadataForAsync` reads the column regardless, so the value is reachable even though no member can
  hold it.
- **Tenants can be deprovisioned but never deleted**, which is the one deliberate limit left in the
  tenancy story rather than a stage it stops at. **`Advanced.DeleteAllTenantDataAsync` is not a
  softening of that** (fisher#173): wiping a tenant's *rows* destroys nothing a file restore would be
  needed to recover, and under database-per-tenant it clears the tenant's file and keeps it. Removing
  the file stays the operator's act. Both styles ship: conjoined (one file sliced by a
  tenant id column, pinned cross-store by `ConjoinedEventTenancyCompliance`), database-per-tenant
  (#47), and tenants that appear, suspend and resume at runtime (#58) — with the daemon routed per
  database, which is what fisher#57 made those `IEventDatabase` parameters carry. Deleting a tenant
  here would mean deleting a *file*: the cheapest deprovisioning of any Critter Stack store and the
  most irreversible, and Fisher cannot know whether that file is backed up. So the API suspends or
  forgets and an operator removes the file themselves. `DisabledTenantException` is distinct from
  `UnknownTenantException` because "switched off" and "never heard of it" are different operational
  situations.
- **No hot-cold daemon coordination**, and `AddAsyncDaemon(DaemonMode.HotCold)` refuses rather than
  quietly running Solo. Failover means several nodes competing for a leadership lease through the
  database, and a Fisher store is a file SQLite does not make safe to share across nodes.

## Traps that have already cost real time

The full list is in CLAUDE.md. The two most expensive, both of which produced *silently wrong*
behaviour rather than an error:

1. **A `Guid` bound as a TEXT parameter is written UPPERCASE** by Microsoft.Data.Sqlite, while
   `SqliteStorageDialect<T>.ToDatabaseValue` writes lowercase. SQLite's default collation is
   case-sensitive, so mixing them writes rows that can never be read back — every document load
   returned null, and only for Guid-identified types. `SqliteGuidIdentification` exists solely for
   this. The failure mode is nasty: delete tests and "unknown id returns null" tests pass throughout,
   because they assert absence.
2. **`SqliteConnection.ClearAllPools()` is process-wide** and xUnit runs collections in parallel, so
   one test's cleanup disposes connections another test is using. Presents as a rare flake
   (`ObjectDisposedException: SQLitePCL.sqlite3`). `TemporaryDatabase` clears only its own pool.

## Conventions worth not relearning

- Test files and classes are `snake_case`. Pass `TestContext.Current.CancellationToken`.
- MTP extension packages stay on the **1.x** line — xunit.v3 3.2.2 is built against
  Microsoft.Testing.Platform 1.x and 2.x dies at startup.
- CI runs the test executable directly; `dotnet test` cannot emit TRX under MTP.
- Conventional `Apply`/`Create` dispatch is emitted by JasperFx's **source generator**, keyed on
  `(aggregate, id type)`, with no runtime fallback. An aggregate with no `Id` gets no dispatcher.
- Run the full suite on both TFMs before committing, and more than once — two of the bugs above only
  appeared intermittently or on one TFM.

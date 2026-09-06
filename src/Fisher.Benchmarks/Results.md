# Fisher.Benchmarks results

Before/after log for the perf work, in the `EventAppenderPerfTester/Results.md` style: one fenced
block per configuration, newest at the bottom, so the history of what each change bought stays
readable. Append a new section per change; do not rewrite old ones.

All numbers are machine-dependent — record the machine header the harness prints with every run.

## Template for a new entry

```
## <change being measured> (<date>, <commit>)

<machine header from the harness>

<paste of `-- all` output>

<paste of the `-- bdn` summary table>

Notes: <what changed, what moved, anything surprising>
```

---

## Baseline — current `main` (2026-09-05, fa81b40)

Recorded before any of the gated perf fixes (write batching inside the exclusive lock, per-type
table-ensure migration work), as the reference those changes are measured against. Checked-in
short defaults; treat deltas, not absolutes, as the signal.

```
Fisher.Benchmarks — 2026-09-05 17:29 -05:00
  OS:        macOS 26.4.1 (Arm64)   (Apple Silicon, 18 logical CPUs)
  .NET:      .NET 10.0.1
  Config:    Release
```

Timed scenarios (`dotnet run -c Release --project src/Fisher.Benchmarks -- all`):

```
== doc-save (100 docs/commit, 5 rounds) ==
  median commit                                              3.3 ms
  fastest commit                                             1.7 ms
  slowest commit                                             4.4 ms
  docs/sec at median                                       30,344/s

== doc-save (1000 docs/commit, 5 rounds) ==
  median commit                                             23.4 ms
  fastest commit                                            20.8 ms
  slowest commit                                            26.6 ms
  docs/sec at median                                       42,757/s

== event-append many-streams (1000 streams x 3 events) ==
  total                                                    280.0 ms
  commits/sec                                               3,572/s
  events/sec                                               10,716/s

== event-append single-stream (1000 events, 10/commit) ==
  total                                                    104.1 ms
  commits/sec                                                 961/s
  events/sec                                                9,608/s

== daemon-rebuild (10000 events, 1000 streams, async Snapshot<BenchTally>) ==
  seed (not daemon time)                                   837.4 ms
  initial catch-up                                         186.5 ms
  catch-up events/sec                                      53,631/s
  rebuild                                                  109.9 ms
  rebuild events/sec                                       90,960/s

== concurrent-writers (8 writers x 50 commits x 5 docs) ==
  total                                                    134.8 ms
  commits/sec                                               2,968/s
  docs/sec                                                 14,842/s
  fisher.retry events                                             0
  commits that retried                                            0
  max retry attempt                                               0
  failed commits                                                  0

== concurrent-writers (1 writers x 400 commits x 5 docs) ==
  total                                                     74.8 ms
  commits/sec                                               5,350/s
  docs/sec                                                 26,750/s
  fisher.retry events                                             0
  commits that retried                                            0
  max retry attempt                                               0
  failed commits                                                  0

== cold-start (20 document types, 5 rounds) ==
  median first commit (table ensure)                        61.0 ms
  median second commit (warm)                                0.6 ms
  table-ensure overhead                                     60.3 ms
  overhead per type                                          3.0 ms
```

Micro-benchmarks (`dotnet run -c Release --project src/Fisher.Benchmarks -- bdn`, ShortRun,
in-process — see README for the publishable-run configuration):

```
| Method        | Documents | Mean      | Error     | StdDev    | Gen0     | Gen1     | Allocated  |
|-------------- |---------- |----------:|----------:|----------:|---------:|---------:|-----------:|
| SaveDocuments | 100       |  1.481 ms |  1.179 ms | 0.0646 ms |  50.7813 |  11.7188 |   439.1 KB |
| SaveDocuments | 1000      | 13.229 ms | 11.383 ms | 0.6239 ms | 515.6250 | 187.5000 | 4320.19 KB |

| Method                 | EventsPerCommit | Mean      | Error     | StdDev    | Gen0    | Gen1   | Allocated |
|----------------------- |---------------- |----------:|----------:|----------:|--------:|-------:|----------:|
| AppendToExistingStream | 1               |  74.15 us |  35.45 us |  1.943 us |  3.2959 | 0.1221 |  27.23 KB |
| StartNewStream         | 1               | 156.41 us | 209.58 us | 11.488 us |  3.4180 |      - |  28.13 KB |
| AppendToExistingStream | 10              | 434.66 us |  51.07 us |  2.799 us | 33.6914 | 2.4414 | 275.55 KB |
| StartNewStream         | 10              | 544.08 us | 561.55 us | 30.781 us | 34.1797 | 2.4414 | 279.27 KB |
```

Notes:

- **doc-save**: per-commit time scales roughly linearly with N (3.3 ms @ 100 → 23.4 ms @ 1000),
  which is the one-command-and-round-trip-per-operation shape the write-batching fix targets.
- **concurrent-writers**: 8 writers doing the same total work as 1 writer took ~1.8x the wall
  clock (134.8 ms vs 74.8 ms) with zero `fisher.retry` events — the contention cost shows up as
  waiting inside `BEGIN IMMEDIATE` under the connection's busy timeout, not as pipeline retries.
  Exactly the caveat in the scenario's remarks; both numbers matter for the write-lock work.
- **cold-start**: ~3 ms of first-use table-ensure per document type on this machine, ~60 ms for a
  20-type store — the per-type migration cost the table-ensure fix goes after; the warm commit of
  the identical shape is 0.6 ms.
- **micro-benchmarks**: allocations scale linearly with the operation count — ~4.3 KB per document
  saved and ~27 KB per single-event append commit — consistent with the per-operation
  command-building the batching work targets. ShortRun error bars are wide by design; use
  `-- bdn --job medium` before quoting a delta.

---

## Per-type table-ensure delta (2026-09-05, fisher#174)

`EnsureDocumentTableAsync` stopped running `ApplyAllConfiguredChangesToDatabaseAsync` per cache miss
and now diffs only the newly-registered type's own schema objects. Measured on a different machine
from the baseline block above (a little faster overall), so the baseline was re-run here rather than
taken from that block — treat the before/after pair, not the absolutes.

```
Fisher.Benchmarks — 2026-09-05 19:32 -05:00
  OS:        macOS 26.4.1 (Arm64)
  .NET:      .NET 10.0.1
  CPUs:      18
  Config:    Release
```

```
== cold-start (20 document types, 5 rounds) ==
                                     before      after
  median first commit (table ensure)  47.9 ms     6.2 ms
  median second commit (warm)          0.5 ms     0.5 ms
  table-ensure overhead               47.3 ms     5.7 ms     8.3x
  overhead per type                    2.4 ms     0.3 ms

== cold-start (32 document types, 5 rounds) ==
                                     before      after
  median first commit (table ensure) 110.0 ms     6.1 ms
  median second commit (warm)          0.8 ms     0.8 ms
  table-ensure overhead              109.2 ms     5.4 ms    20.2x
  overhead per type                    3.4 ms     0.2 ms
```

Notes:

- **The shape changed, not just the constant.** Before, 1.6x the types cost 2.3x the warm-up
  (47.3 ms → 109.2 ms) — the superlinear curve an O(types × objects) diff predicts, since migration
  #k re-introspects every object from 1..k−1. After, 32 types cost the same as 20 (5.4 ms vs
  5.7 ms, inside the run-to-run noise): the remaining cost is per-type and flat, so the number to
  quote for a larger store is "no worse", not "20x again".
- **The warm commit is unmoved** in both configurations, which is the control: this changes what the
  first use of a type pays and nothing about the steady state.
- Not re-run: `doc-save`, `event-append`, `daemon-rebuild`, `concurrent-writers` and the BDN
  micro-benchmarks. None of them reaches the first-use ensure more than once per type, and the
  scenario that does is the one above.

---

## LINQ config-only caches (2026-09-06, fisher#179)

The rendered select list is cached per storage and the `MemberFactory` per mapping and table alias,
instead of being rebuilt on every query execution. `QueryBenchmarks` is new in this change, so there
is no earlier entry to compare against — the before column is the same harness run against `main`.

```
Fisher.Benchmarks — 2026-09-06 (Apple Silicon, macOS 26.4.1 Arm64, .NET 10.0.1, 18 CPUs, Release)
BenchmarkDotNet ShortRun, in-process toolchain.
```

```
| Method        | Allocated before | Allocated after |        |
|-------------- |-----------------:|----------------:|-------:|
| FilteredPage  |         31.65 KB |        31.35 KB | -0.30  |
| FilteredCount |         13.34 KB |        13.23 KB | -0.11  |
| FirstByMember |         16.17 KB |        15.84 KB | -0.33  |
## Prepared-statement reuse in the write batch — fisher#171 (2026-09-06, `perf/write-batch`)

`FisherSession.ExecuteBatchAsync` prepared and disposed one `SqliteCommand` per queued operation
inside the exclusive `BEGIN IMMEDIATE` transaction. It now keeps the last prepared command and reuses
it while consecutive operations compile to identical SQL, so a run of N same-shape writes costs one
`sqlite3_prepare_v2` rather than N. Each operation still executes on its own against its own reader —
nothing about result-set handling moved. `EventTagWriter` and `NaturalKeyWriter`, which run after the
batch and still inside the lock, share the same coalescing.

```
Fisher.Benchmarks — 2026-09-06 (Apple Silicon)
  OS:        macOS 26.4.1 (Arm64)
  .NET:      .NET 10.0.1
  CPUs:      18
  Config:    Release
```

**Measured paired and alternating**, a detached baseline worktree at `origin/main` (e2f7ede) against
this branch, one run each per pair, back to back. The run-to-run noise on this machine is wider than
the effect, so a before-block and an after-block recorded minutes apart would not have been readable;
what is quoted is the median of the pairs and how many pairs ran the same way.

```
== doc-save, median commit (--rounds 11, 6 pairs) ==
                          before      after
  100 docs/commit          1.2 ms     0.8 ms     ~1.5x    6 of 6 pairs favour after
  1000 docs/commit        13.35 ms    8.75 ms    ~1.5x    6 of 6 pairs favour after

== concurrent-writers, total wall clock (5 docs/commit, 5 pairs) ==
                          before      after
  8 writers x 50 commits  144.9 ms   112.4 ms    ~1.3x    4 of 5 pairs favour after
  1 writer x 400 commits   57.7 ms    49.9 ms
  contention ratio           2.51x      2.25x
  fisher.retry events            0          0
```

Per-pair, so the spread is visible rather than hidden behind a median:

```
doc-save 100      before   1.1   1.1   1.3   1.4   1.5   1.1
                   after   0.8   0.8   1.0   0.8   0.7   1.1

doc-save 1000     before  14.1  12.7  12.0  13.8  13.1  13.6
                   after   8.7   8.6   8.4   8.8   9.2   9.3

concurrent 8w     before 150.3 124.0  98.9 175.8 144.9
                   after  94.0 150.0  85.8 124.0 112.4
```

Notes:

- **`doc-save` is the clean result and `concurrent-writers` is the noisy one.** At 1000 docs every
  pair separates by 3-5 ms against a spread of ~2 ms, which is a real effect. The contended scenario
  overlaps between the two sides on one pair of five and its 1-writer contrast threw a 129 ms outlier
  against a 36-50 ms band, so **1.3x there should be read as "directionally better, same order of
  magnitude"** rather than quoted as a figure.
- **The contention ratio is what this change was aimed at**, not single-writer latency. This harness's
  own baseline note records that contention here is waiting inside `BEGIN IMMEDIATE` under the
  connection's busy timeout with **zero** `fisher.retry` events — still zero on both sides, so the
  only lever is how long the lock is held.
- **The gain scales with the length of a coalesced run**, which is why `concurrent-writers` moves
  least: 5 documents per commit is a run of 5, against 1000 in `doc-save`.
- **Not moved, and not expected to be:** `event-append` (the many-streams shape is one operation per
  stream, so its runs are length 1), `daemon-rebuild`, `cold-start`. Left out rather than padded in.
- **Concatenating the batch into one multi-statement command was measured first and rejected.** It is
  *slower than the code it replaces* — 82-192 ms against 4-6 ms for the same 1000 upserts — because
  `SqliteParameterCollection` rebinds against the whole collection per prepared statement, so N
  statements sharing 3N parameters is O(N²). Chunking traces the curve (10 per command 10 ms, 50 per
  command 22 ms, 250 per command 59 ms) and **every chunk size measured is worse than a command per
  operation**, so there is no sweet spot to tune to. See `FisherSession.ExecuteBatchAsync`'s remarks
  for that and for why the `NextResult` walk it needs could not be made safe on this provider anyway.

---

## Query construction vs execution — the compiled-query measurement (fisher#195)

`QueryConstructionBenchmarks`, macOS 26 / Apple Silicon (Arm64), .NET 10.0.1, in-process toolchain,
10 warmup + 25 measured iterations, one warm session shared by both halves of each pair.

**Construct** is `session.ToSql(query)` — `BuildStatement` + `Statement.Apply` +
`CommandBuilder.Compile`, the same three calls `FisherQueryProvider.CommandFor` makes minus the
ensured-table cache hit. It is exactly the work a compiled query would skip, and if anything a slight
over-count of it (a compiled query would still bind parameter values). **Full** is the ordinary LINQ
terminal on the same session.

```
| Method         |      Mean |   StdDev | Allocated |
|--------------- |----------:|---------:|----------:|
| PageConstruct  |  3.838 us | 0.132 us |    8.9 KB |
| PageFull       | 92.481 us | 1.148 us |  25.58 KB |
| CountConstruct |  3.124 us | 0.151 us |   5.85 KB |
| CountFull      | 70.861 us | 2.021 us |   7.47 KB |
| FirstConstruct |  2.222 us | 0.101 us |    4.3 KB |
| FirstFull      | 20.337 us | 0.393 us |  10.08 KB |
| ByIdConstruct  |  2.176 us | 0.062 us |   3.87 KB |
| ByIdFull       |  9.668 us | 0.202 us |    9.1 KB |
```

Read as shares — the construct half as a fraction of the whole call:

```
shape                              time      allocations
filtered ordered page (10 docs)     4.1%          34.8%
filtered count (0 rows read)        4.4%          78.3%
first by member (200-row scan)     10.9%          42.7%
first by id (index seek)           22.5%          42.5%
```

- **Wall clock says no and allocations say yes**, which is the whole result. Construction is 4-11% of
  an ordinary query and **22.5% of the cheapest query the store can run** — an index seek returning
  one row, which is the ceiling by construction. But it is **35-78% of every query's allocations**,
  because the execute half of an embedded store allocates almost nothing: `CountFull` adds 1.6 KB to
  `CountConstruct`'s 5.85 KB.
- **The ratio really is much higher than Marten's, and that argument still does not carry.** On
  PostgreSQL the construct half sits in front of a network round trip and is a low single-digit
  percentage; here there is no round trip for it to be a fraction of, so it rises to 22.5% at the
  ceiling. The absolute number is what decides it: 2.2 us saved off a 9.7 us call, against
  `ICompiledQuery` / `ICompiledListQuery` and their AspNetCore streaming variants as new public API.
- **This is the number fisher#181 deferred, and it confirms that note.** Caching the config-only
  halves (select list per storage, `MemberFactory` per mapping) moved allocations ~1%, because what is
  left is per-query: the expression walk, an `IQueryableMember` per referenced member, the `Statement`
  and the render. That residue is the 35-78% above.
- **A filter-shape plan cache (marten#5013) collects almost all of it with no public API.** Keyed on
  the shape of the predicate, it removes member resolution, statement construction and the SQL render
  for *every* query, leaving only a cheaper structural walk. A compiled query removes that walk too —
  a fraction of an already-small 2-4 us — for a whole new API surface and a rewrite of each call site.
- **What would change the answer**: a plan cache that lands and leaves a residue still worth 10%+ of a
  query, or a workload dominated by trivially-executing queries at high rate. Re-run this class rather
  than re-arguing it.

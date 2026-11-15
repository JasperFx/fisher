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

---

## Channel<T> single-writer queue vs racing for `BEGIN IMMEDIATE` — fisher#547 (2026-11-15, `channel-writer-benchmark`)

**Measurement only. Whether Fisher adopts a queue is the repo owner's decision, and nothing here
makes it.** Fisher's write path is unchanged: the queue is a benchmark-only wrapper
(`Scenarios/SingleWriterQueue.cs`) around an ordinary session's `SaveChangesAsync`.

```
Fisher.Benchmarks — 2026-11-15 (Apple M5 Max)
  OS:        macOS 26.4.1 (Arm64)
  .NET:      .NET 10.0.14
  CPUs:      18
  Config:    Release
```

### What was measured

`writer-queue` (`Scenarios/WriterQueueScenario.cs`) is `ConcurrentWritersScenario`'s shape: K
writers, each opening a lightweight session, storing N small `BenchDoc`s and committing, in a loop,
against one database file. The total work is fixed and split across the K writers. Three modes, which
differ only in who calls `SaveChangesAsync`:

- **race**: today. Every writer commits for itself and races for `BEGIN IMMEDIATE`. Contention is
  absorbed by SQLite's `busy_timeout` (5000 ms, from Weasel's default PRAGMAs) and, beyond that, by
  the Polly pipeline.
- **queue**: the fisher#547 candidate. Writers hand `() => session.SaveChangesAsync()` to one
  unbounded `Channel<T>` with a single consumer, which runs the commits one at a time. Plain
  serialization, one transaction per unit of work. **Not group commit**: batching several queued
  sessions into one transaction is a different design with different failure semantics, and it was
  not measured.
- **gate**: a control, not a candidate. A `SemaphoreSlim(1)` around the writer's own
  `SaveChangesAsync`. It serializes exactly as the queue does, but the commit stays on the caller's
  async flow, so it separates "serializing in-process" from "a channel and a consumer task".

Per mode the scenario reports:

- wall clock (median and range over rounds) and commits/sec at the median;
- commit latency p50/p95/p99/max, pooled over every commit of every round. It is measured from just
  before the commit is requested to its completion, so it includes the `BEGIN IMMEDIATE` wait for
  **race** and the time spent queued for **queue**;
- bytes allocated per commit (`GC.GetTotalAllocatedBytes(precise: true)` over the timed section);
- the `fisher.write_lock.wait` histogram and the `fisher.write_lock.retries` counter
  (`TrackWriteLockContention()`, read by a `MeterListener` filtered on a per-round store name);
- `fisher.retry` activity events (`RetryCounter`).

Method: `-c Release`, a fresh database file per round, one discarded warmup round per mode, and the
mode order rotated every round so no mode always runs first. Commands:

```bash
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --total-commits 400  --docs-per-commit 5  --rounds 11
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --total-commits 2000 --docs-per-commit 5  --rounds 9
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --total-commits 1000 --docs-per-commit 50 --rounds 7
# the headline writer counts again, 21 rounds per run, run separately
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --writers 1  --rounds 21
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --writers 8  --rounds 21   # x2
dotnet run -c Release --project src/Fisher.Benchmarks -- writer-queue --writers 16 --rounds 21   # x4
```

Without `--writers` it sweeps 1, 2, 4, 8 and 16 writers. 400 commits x 5 documents is the audit's
shape: 8 writers x 50 commits against 1 writer x 400.

### The audit's shape: 400 commits x 5 documents, 11 rounds

```
             wall clock, median (ms)      commit latency, race (ms)    commit latency, queue (ms)   lock wait summed
writers     race    queue    gate        p50    p99     max            p50    p99    max           per round, race
   1        29.0     33.6    30.4        0.1    0.3     1.6            0.1    0.3    2.2              1.0 ms
   2        35.1     31.9    44.7        0.1    1.1    36.1            0.1    0.9    3.2             21.7 ms
   4        42.4     29.4    47.2        0.1    3.7    59.5            0.2    1.9    6.7             66.2 ms
   8        66.5     29.0    46.4        0.1   20.4   116.6            0.5    3.3   24.7            237.3 ms
  16       113.0     30.5    45.4        0.1   54.9   236.2            1.1    3.7    6.8            554.2 ms
```

The same shape again at 21 rounds per run (wall clock medians, race / queue / gate):

```
   1 writer     36.6 / 37.8 / 36.1 ms
   8 writers    86.1 / 24.5 / 38.8      85.7 / 30.5 / 35.6
  16 writers   141.2 / 58.9 / 62.6     138.6 / 24.1 / 29.6     114.3 / 31.0 / 36.8     138.3 / 21.8 / 31.5
```

### Larger runs and heavier commits

```
2000 commits x 5 docs, 9 rounds     wall clock median (ms)        p99 / max latency (ms)
writers                             race   queue   gate           race           queue
   1                               230.2   226.6  210.8          0.5 / 25.5      0.5 / 50.8
   8                               207.2   184.8  205.8          5.0 / 235.8     3.7 / 80.8
  16                               288.6   144.4  222.7         27.2 / 340.4     5.8 / 69.5

1000 commits x 50 docs, 7 rounds
   1                               288.6   326.9  312.2          2.2 / 111.0     2.0 / 54.4
   8                               371.2   263.1  339.3         36.5 / 437.7     6.8 / 13.2
  16                               462.2   280.4  337.7        108.7 / 643.6    11.0 / 14.8
```

Allocations per commit were the same in every mode, within noise: 29.6-30.2 KB for 5 documents and
209.2-209.6 KB for 50. The queue adds about 0.2-0.3 KB per commit (the work item, its
`TaskCompletionSource` and the closure).

**Retries: zero.** `fisher.retry` and `fisher.write_lock.retries` read 0 in every mode of every run.
As the 2026-09-05 baseline found, all of today's contention is absorbed inside `BEGIN IMMEDIATE` by
the busy timeout, and the Polly pipeline is never reached.

**One commit failed and the cause is unknown.** It was in race mode at 16 writers, in the first
400 x 5 sweep: 1 commit of 4,400, with no retry recorded. That run did not capture the exception.
After capture was added, 84 more 16-writer race rounds (33,600 commits) did not reproduce it.

### Notes

- **At one writer the three modes are the same. From four writers up, the queue is faster.** On the
  audit's shape it took about 2.3x less wall clock at 8 writers and 3.7x less at 16. On the larger
  runs the gain was 1.1-2x, and on 50-document commits 1.4-1.6x. At two writers the difference is
  within noise. Racing gets slower as writers are added: 8 writers took 2.3x the 1-writer wall clock
  here, against 1.8x in the baseline. The queue's wall clock stays flat. 8 or 16 queued writers can
  even finish slightly ahead of 1 writer, because building the next session overlaps the current
  commit.
- **The queue mostly improves the tail. Its median is slightly worse.** A racing commit usually gets
  the lock at once (p50 0.1 ms), and the unlucky few wait 20-240 ms (p99 and max at 8-16 writers). A
  queued commit always waits its turn (p50 0.5-4 ms), but never for long (p99 3-11 ms). At low
  concurrency, typical latency is a little worse with the queue. Worst-case latency is much better.
- **The racing writers spend most of their wait asleep, not behind a held lock.** This is inferred
  from the numbers, not traced, but the numbers are hard to read any other way:
  - one commit holds the lock for roughly 0.07 ms (400 commits in about 30 ms single-writer), so 15
    writers queued ahead account for about 1 ms;
  - yet the waits over 1 ms (4-9 % of commits at 8-16 writers) ran to 130 ms and beyond;
  - and the summed wait per round (237 ms at 8 writers, 554 ms at 16) is several times the round's
    whole wall clock.

  The likely mechanism is SQLite's default busy handler, which `PRAGMA busy_timeout` installs. It
  polls the lock with growing sleeps (1, 2, 5, 10, 15, 20, 25 ... 100 ms), so a waiter can be asleep
  while the lock is free. The scenario prints the slept waits bucketed by that schedule
  (`slept waits, busy-handler buckets`, up to 5, 10, 20, 35, 55, 80, 105, 130 and over 130 ms). A
  typical 16-writer run gave `337 91 53 32 24 35 28 21 23`: a long tail rather than one cluster near
  the lock-hold time. So what the queue removes is the busy handler's sleeping, not Polly retries,
  since there are none.
- **The gate matches the queue on the tail but not on throughput.** Its p99 is as good as the queue's.
  Its wall clock beats racing at 8 and 16 writers, but is worse than racing at 2 and 4, and behind the
  queue at 8 and 16 (about 30-46 ms against 22-31 ms, leaving out one noisy 16-writer run where both
  were near 60 ms). A probable reason, not measured: the channel's
  single consumer runs commits back to back, while every semaphore release hands off to another
  thread-pool continuation. In-process serialization is what helps, and a channel is one way to get
  it.
- **A queue in front of one process's sessions does not cover everything that writes.** These all
  still race with the consumer, as they race with each other today: the async daemon's projection
  batches, rebuild teardown, the Hi-Lo advance (`fi_hilo`, on its own connection), first-use table
  migrations, dead-letter writes, a second store over the same file, another process, and every
  enlisted session (see below). None of them were in this scenario.

### What the queue would change for callers

These are the semantic differences the issue asked to be stated. They apply to any design that runs
a session's commit somewhere other than the caller's own `await`.

- **A failure arrives later.** A queued commit's task completes or faults when the consumer gets to
  it, which is after everything queued ahead of it. A caller awaiting it still gets the exception
  (a concurrency failure, a constraint violation, `StreamLockedException`), but only after the queue
  time. A caller that does not await the task never sees the failure.
- **The commit runs on the consumer's async flow, not the caller's.** `Activity.Current`,
  `AsyncLocal` values and logging scopes are whatever the consumer task captured. In particular the
  commit's span no longer has the caller's trace as its parent. The gate keeps the caller's flow. The
  queue would need to capture and restore each caller's `ExecutionContext` to do the same.
- **Cancellation stops reaching a queued commit.** A caller's `CancellationToken` matters only once
  the commit is running. A commit still in the queue cannot be withdrawn unless the queue is built to
  remove it.
- **One slow commit holds up everyone behind it, in strict order.** There is one lock, so this is
  also true today, but today the order is a lottery and with a queue it is first in, first out. A
  large batch, an inline projection, or a transaction participant doing EF Core work delays every
  commit queued after it.
- **A commit hook that commits again would deadlock.** A listener's `AfterCommitAsync`, an outbox or
  a participant that itself commits through the queue would wait for the consumer that is running it,
  and never finish. The gate has the same problem.
- **Enlisted sessions cannot go through the queue.** Under `SessionOptions.ForTransaction` the caller
  already holds the transaction on their own connection, and for a `BEGIN IMMEDIATE` transaction that
  means they already hold the file's write lock. Fisher does not commit that session at all. Queuing
  it would make it wait for a consumer that is waiting for the lock the caller holds. So enlisted
  sessions would have to bypass the queue and keep racing with its consumer, and a queue could not
  promise that commits never race.

Not measured: group commit (coalescing queued sessions into one transaction), event appends (the
scenario writes documents only), the async daemon writing at the same time, a bounded channel with
backpressure, and any machine other than this one.

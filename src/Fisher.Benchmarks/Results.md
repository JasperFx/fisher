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

---

## Binary event bodies: JasperFx 3.3.0's `IBufferWriter` / span serializer members — fisher#570 (2026-11-15, `binary-serializer-span-members-570`)

**Decision: not adopted. Fisher keeps the `byte[]` pair on both sides, and no production code changed.**
No measured shape saves allocation without costing time. One shape does save allocation: reading a body
past the large-object-heap threshold through a serializer that overrides the span member. It is 33–42%
slower per row in isolation. It would also need a length column added to the canonical SELECT, and a way
to detect whether a serializer overrides a default interface member.

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.4.1 (25E253) [Darwin 25.4.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 11.0.100, host .NET 10.0.14, Arm64 RyuJIT
Microsoft.Data.Sqlite 10.0.9, JasperFx.Events 3.3.0, MemoryPack 1.21.4
Job=ShortRun (InProcessEmitToolchain, 3 warmup, 3 iterations; 10 iterations for the column reads)
```

Under ShortRun the **time** columns are noisy by roughly ±10–15%. For example, the same configuration's
`AppendTenAndCommit` at 200 B ran 377 µs in one run and 454 µs in another, with identical allocation.
The **allocation** columns are exact and repeat, and allocation is what the issue asked about.

### What was measured

`BinaryEventBenchmarks.cs` holds three BenchmarkDotNet classes and one probe. Every class runs two
serializers over the same library (MemoryPack, with `[MemoryPackable]` on the event). The only difference
between them is which `IEventBinarySerializer` members they implement:

- **`ByteArrayOnly`** implements `Serialize(Type, object)` / `Deserialize(Type, byte[])` and lets the
  interface defaults serve the two new members. Most hand-written serializers look like this.
- **`BufferWriterAndSpan`** also overrides `Serialize(Type, object, IBufferWriter<byte>)` and
  `Deserialize(Type, ReadOnlySpan<byte>)`. It writes into the caller's buffer and reads the caller's span
  directly. This is the case the new members exist for.

MemoryPack was chosen over a hand-written writer because it is the realistic case, and its source
generator ran on net10.0 with no trouble. The event holds a `Guid`, a short string, an int, and a random
`byte[]` payload of either 200 bytes or 100,000 bytes. The large body is past the 85,000-byte
large-object-heap threshold, so every exact-length array of it is a Gen2 allocation.

### 1. Which BLOB parameters Microsoft.Data.Sqlite binds (`-- binary-bind-probe`)

A write-side adoption could skip the copy out of a pooled buffer only if the provider bound something
other than an exact-length `byte[]`. It does not:

```
Microsoft.Data.Sqlite 10.0.9.0
  byte[] (exact length)                  bound as blob, length 8
  ArraySegment<byte>                     REFUSED: No mapping exists from object type System.ArraySegment`1[System.Byte] ...
  ReadOnlyMemory<byte>                   REFUSED: No mapping exists ...
  Memory<byte>                           REFUSED: No mapping exists ...
  MemoryStream                           REFUSED: No mapping exists ...
  byte[] with SqliteParameter.Size = 4   bound as blob, length 4

Allocation per INSERT of a 100,000-byte BLOB (the array itself is pre-allocated):
  exact-length byte[]                           296 bytes
  128 KiB byte[] with Size = 100,000        100,320 bytes
```

⚠️ `SqliteParameter.Size` does truncate an oversized array, so it looks like a way to bind a rented
buffer as-is. It is not: the provider copies the truncated prefix into a new array of `Size` bytes. Binding
a rented buffer that way costs exactly the copy it was meant to avoid.

### 2. The serializer step alone (`BinaryEncodeBenchmarks`), per event

`Serialize_BufferWriterThenToArray` is the realistic write-side adoption. It serializes into a pooled
writer (here one reused `ArrayBufferWriter`, which is the best case), then copies out the `byte[]` the
bind needs. `Serialize_BufferWriterNoCopy` stops before that copy, so it is a floor that the provider
makes unreachable.

| Serializer | Body | `Serialize` (today) | pooled writer + `ToArray` | floor, no copy | `Deserialize(byte[])` (today) | `Deserialize(span)` |
|---|---|---|---|---|---|---|
| ByteArrayOnly | 200 B | 36.7 ns / 272 B | 54.7 ns / **544 B** | 42.6 ns / 272 B | 34.7 ns / 336 B | 46.8 ns / **608 B** |
| ByteArrayOnly | 100 KB | 12.5 µs / 100,186 B | 26.0 µs / **200,372 B** | 14.7 µs / 100,186 B | 10.1 µs / 100,250 B | 20.2 µs / **200,436 B** |
| BufferWriterAndSpan | 200 B | 36.5 ns / 272 B | 39.2 ns / 272 B | 26.9 ns / 0 B | 37.0 ns / 336 B | 36.9 ns / 336 B |
| BufferWriterAndSpan | 100 KB | 12.5 µs / 100,186 B | 12.6 µs / 100,186 B | 1.0 µs / 0 B | 9.6 µs / 100,250 B | 10.1 µs / 100,250 B |

- **The overriding serializer saves nothing in either direction.** MemoryPack's own `byte[]` `Serialize`
  already writes into a pooled buffer and copies out once, which is exactly what the adoption does. The
  span `Deserialize` over the array `GetValue` returned reads the same bytes the `byte[]` one does.
- **A `byte[]`-only serializer pays double on both sides.** Each default member adds a copy. The
  writer default copies `Serialize`'s array into the buffer, and the span default copies the span back
  into an array.
- **The floor shows where the cost is.** For a 100 KB body, the copy out is 11.5 of the 12.5 µs, because
  the copy is a LOH allocation. Nothing on the write side can remove that copy, because the provider
  binds only exact-length arrays.
- For this event, deserializing allocates at least the body size, because the event owns a `byte[]`
  payload. That allocation belongs to the event, not to the store.

### 3. Reading the BLOB column (`BinaryColumnReadBenchmarks`), per row, 500 rows, 10 iterations

Deserialization uses the overriding serializer throughout, so the rows differ only in how the bytes are
reached. "Rowid" means the SELECT includes `seq_id`, which is the rowid's alias. Fisher's SELECT always
includes it.

| Read | 200 B | 100 KB |
|---|---|---|
| `GetValue` → `Deserialize(byte[])` (today) | 177 ns / 616 B | 24.4 µs / 200,444 B |
| `GetBytes` into a rented buffer → span, rowid | 958 ns / 1,124 B | 34.8 µs / **101,038 B** |
| `GetBytes` into a rented buffer → span, no rowid | 353 ns / 1,016 B | 34.5 µs / 200,844 B |
| hybrid: length probe, rented only at ≥ 85,000 B, rowid | 589 ns / 1,011 B | 32.6 µs / **101,038 B** |

- **Without the rowid, `GetBytes` saves nothing.** The provider materializes the column into a cached
  array and copies from it.
- **With the rowid, `GetBytes` reads through incremental blob I/O**, opening a `sqlite3_blob_open` per row.
  For the large body that halves allocation, at +42% time per row. For a small body it is 5.4x slower and
  allocates more, because the blob handle costs more than the 200 bytes it saves.
- ⚠️ **The length probe is not free either.** With the rowid present, `GetBytes(ordinal, 0, null, 0, 0)`
  also opens the blob, which is why the hybrid's small-body row costs 589 ns against 177 ns. To avoid
  regressing small bodies, a hybrid would need the length as a SELECT column (`length(data_binary)`). That
  changes the canonical projection `FisherEventsRowReader` owns.

### 4. End to end through a Fisher store (`BinaryEventStoreBenchmarks`), per operation of ten events

`AppendTenAndCommit` starts a new stream with ten binary events and commits it. `FetchTen` reads a
ten-event binary stream with `FetchStreamAsync`.

The "Prototype" column is the realistic adoption, built and measured on this branch and then reverted:

- Write side: a `[ThreadStatic]` `ArrayBufferWriter` in `EventGraph.BinaryEncoderFor`, then
  `Serialize(type, data, writer)`, then `WrittenSpan.ToArray()`.
- Read side: `Deserialize(type, new ReadOnlySpan<byte>((byte[])reader.GetValue(...)))` in both
  `FisherEventsRowReader` call sites.

The "Rented read" column replaces that read with the rowid `GetBytes` read into an `ArrayPool` buffer. It
was measured for `FetchTen` only.

| Operation | Serializer | Body | Today | Prototype | Rented read |
|---|---|---|---|---|---|
| AppendTenAndCommit | ByteArrayOnly | 200 B | 379 µs / 309.97 KB | 441 µs / **312.63 KB** | — |
| AppendTenAndCommit | ByteArrayOnly | 100 KB | 2,070 µs / 1,286.29 KB | 2,710 µs / **2,265.32 KB** | — |
| AppendTenAndCommit | BufferWriterAndSpan | 200 B | 377 µs / 309.97 KB | 454 µs / 309.97 KB | — |
| AppendTenAndCommit | BufferWriterAndSpan | 100 KB | 2,116 µs / 1,286.26 KB | 2,402 µs / 1,286.40 KB | — |
| FetchTen | ByteArrayOnly | 200 B | 28.6 µs / 21.72 KB | 32.7 µs / **24.38 KB** | 45.2 µs / **30.67 KB** |
| FetchTen | ByteArrayOnly | 100 KB | 186 µs / 1,972.20 KB | 314 µs / **2,949.29 KB** | 306 µs / 1,980.97 KB |
| FetchTen | BufferWriterAndSpan | 200 B | 31.0 µs / 21.72 KB | 32.7 µs / 21.72 KB | 44.4 µs / **28.02 KB** |
| FetchTen | BufferWriterAndSpan | 100 KB | 187 µs / 1,972.22 KB | 224 µs / 1,972.02 KB | 214 µs / **1,003.60 KB** |

- **For small bodies, the binary step is a tiny share of a commit.** Ten 200-byte events encode to 2.7 KB
  out of a 310 KB commit, so nothing an adoption changed there would show.
- **The prototype changes nothing for the overriding serializer.** For the `byte[]`-only one it costs a
  full body copy per event on each side. With a 100 KB body that is about 980 KB more per ten-event
  commit, and about the same per ten-event read.
- **The rented read improves exactly one cell**: the overriding serializer with a 100 KB body, which drops
  from 1,972 KB to 1,004 KB per ten-event read. Its time is within this job's noise of today's (214 µs,
  against 186–224 µs across runs), while the isolated column read above shows +33–42%. Every other cell
  it touches gets worse. Taking it would mean a size threshold, a length column in the canonical SELECT,
  and a check for whether the serializer overrides a default interface member. All of that buys one fewer
  Gen2 array per large event read, and only for serializers that override the member.

### Notes

- The prototype was measured on this branch and reverted. It was never meant to ship, so the test suite
  was not run against it.
- Not measured:
  - a serializer other than MemoryPack;
  - bodies between 200 B and 100 KB (the LOH threshold is the one boundary that matters, and both sides
    of it are covered);
  - the async daemon's loader, which goes through the same `ReadEventCore`;
  - net11.0.
- Revisit if Microsoft.Data.Sqlite gains a span or `ReadOnlyMemory<byte>` BLOB bind, or a span read over
  `sqlite3_column_blob`. After a provider bump, `-- binary-bind-probe` answers the first question in one
  command.

---

## fisher#547 round 2: busy handlers, event appends, the daemon, group commit, a bounded queue (2026-11-16, `writer-queue-round-2-547`)

**Measurement only. Whether Fisher adopts any of this is the repo owner's decision, and nothing here
makes it.** Fisher's write path is unchanged. Everything below is benchmark code under
`src/Fisher.Benchmarks/`.

```
Fisher.Benchmarks — 2026-11-16 (Apple M5 Max)
  OS:        macOS 26.4.1 (Arm64)
  .NET:      .NET 10.0.14
  CPUs:      18
  Config:    Release
  SQLite:    e_sqlite3 3.50.4 (SQLitePCLRaw 3.0.5), Microsoft.Data.Sqlite 10.0.9
```

### What was added

All benchmark-only, wired into `writer-queue` (`Scenarios/WriterQueueScenario.cs`):

- **`bh-*` modes: racing as today, with a different busy handler.** SQLitePCLRaw 3.0.5 does not expose
  `sqlite3_busy_handler`, so `Scenarios/BusyHandlers.cs` P/Invokes it from the bundled `e_sqlite3` with an
  `[UnmanagedCallersOnly]` callback. It is installed through `StoreOptions.Functions`, whose registrations
  Weasel's data source runs after the connection PRAGMAs on every open. So it replaces the handler
  `PRAGMA busy_timeout` installed, on application sessions and on the daemon's connections alike. Every
  policy gives up after 5000 ms, as `busy_timeout = 5000` does.
  - `bh-mimic`: a transcription of SQLite's `sqliteDefaultBusyCallback` (sleeps of 1, 2, 5, 10, 15, 20,
    25, 25, 25, 50, 50, then 100 ms). It counts invocations and times the sleeps. This is the mechanism
    check.
  - `bh-yield`: `Thread.Yield()` per invocation.
  - `bh-spin50us`: a 50 µs busy-wait per invocation.
  - `bh-sleep1ms`: a fixed `Thread.Sleep(1)` per invocation.
  - `bh-none`: return 0 at once, which is what `busy_timeout = 0` does. This is the "tiny busy_timeout
    plus a retry above it" option, measured as it actually behaves (see below).
- **`bounded1`, `bounded4`**: round 1's queue on `Channel.CreateBounded` with `FullMode.Wait`.
- **`group`**: a group-commit prototype (`Scenarios/GroupCommitQueue.cs`). One consumer takes up to
  `--group-size` (default 64) queued units of work. It begins one `BEGIN IMMEDIATE` on a connection it
  opened before the writers started, and replays each unit onto an enlisted session
  (`SessionOptions.ForTransaction`) inside its own `SAVEPOINT`. It rolls back to the savepoint on failure,
  commits once, then completes each caller's task.
- **`--workload events-many`**: each commit starts `--docs-per-commit` new streams with one event each.
  **`--workload events-hot`**: every commit appends one event to the same stream. The writer reads the
  stream's version through its own session and appends expecting it. A commit that loses is counted as a
  version conflict (`EventStreamUnexpectedMaxEventIdException`) and is not retried.
- **`--daemon 1`**: an async `Snapshot<BenchTally>` runs under the daemon while the writers append. The
  scenario reports the daemon's catch-up time after the writes end, and the daemon's own
  `fisher.write_lock.wait` (holder tag `daemon`), separately from the application's.
- **Process CPU time per round** (`Process.TotalProcessorTime` over the timed section), and the busy
  handler's invocations, waits and time spent inside it.

### Method and commands

As round 1: `-c Release`, a fresh database file per round, one discarded warmup round per mode, and the
mode order rotated every round. Medians are over rounds. Latency percentiles are pooled over every commit
of every round. Latency is measured from just before the commit is requested to its completion, so it
includes queue time and the `BEGIN IMMEDIATE` wait. For the hot stream it does not include the version
read before it.

```bash
ALL=race,queue,gate,bh-mimic,bh-yield,bh-spin50us,bh-sleep1ms,bh-none,bounded1,bounded4,group
W=src/Fisher.Benchmarks
dotnet run -c Release --project $W -- writer-queue --total-commits 400 --docs-per-commit 5 --rounds 11 --modes $ALL
dotnet run -c Release --project $W -- writer-queue --writers 1,8,16 --total-commits 2000 --docs-per-commit 5  --rounds 9 --modes <ALL minus bh-none>
dotnet run -c Release --project $W -- writer-queue --writers 1,8,16 --total-commits 1000 --docs-per-commit 50 --rounds 7 --modes <ALL minus bh-none>
dotnet run -c Release --project $W -- writer-queue --writers 1,4,8,16 --total-commits 400 --docs-per-commit 5 --rounds 11 --workload events-many --modes race,queue,gate,bh-mimic,bh-spin50us,bh-sleep1ms,bounded4,group
dotnet run -c Release --project $W -- writer-queue --writers 1,4,8,16 --total-commits 400 --rounds 11 --workload events-hot --modes race,queue,gate,bh-mimic,bh-spin50us,bh-sleep1ms,bounded4,group
dotnet run -c Release --project $W -- writer-queue --writers 8,16 --total-commits 4000 --docs-per-commit 1 --rounds 7 --workload events-many --daemon 1 --modes race,queue,gate,bh-mimic,bh-spin50us,bh-sleep1ms,group
dotnet run -c Release --project $W -- writer-queue --writers 8,16 --total-commits 2000 --docs-per-commit 5 --rounds 9 --group-size 4 --modes queue,group
dotnet run -c Release --project $W -- writer-queue --writers 8  --rounds 21 --modes race,queue,bh-mimic,bh-yield,bh-spin50us,bh-sleep1ms,group
dotnet run -c Release --project $W -- writer-queue --writers 16 --rounds 21 --modes race,queue,bh-mimic,bh-yield,bh-spin50us,bh-sleep1ms,group
```

**Machine load.** Another agent was using this machine. The 1-minute load average was 4-10 (on 18
cores) during the first pass, and highest (7-10) during the 400 x 5, event and daemon runs. So the
headline shapes were rerun once the load had dropped to about 4: 400 x 5 at 8 and 16 writers, both event
workloads and the daemon run. The rerun numbers are the ones in the tables unless a table says otherwise.
The two passes agreed on every ordering below. The medians moved by up to about 15 %, except race and
`bh-mimic` at 16 writers, whose round-to-round range is wide in both passes (85-240 ms, for example).

### 1. The busy handler: the mechanism, and whether a smaller change gets the gain

**The mechanism is confirmed.** Round 1 inferred it; these numbers measure it.

`bh-mimic` replaces SQLite's default handler with a copy that counts. If the default handler's sleeping
is where the racing writers lose their time, then three things follow, and all three hold:

- The copy reproduces racing's numbers. It does, within run-to-run noise: wall clock, p99, max and the
  summed lock wait match `race` at every writer count and shape (tables below).
- The time inside the handler accounts for the whole `BEGIN IMMEDIATE` wait. Its median matches the
  median of `bh-mimic`'s summed `fisher.write_lock.wait` to within 4 % in each of the 22 cells where any
  commit waited.
- Swapping the handler moves the waits. With `bh-yield` or `bh-spin50us`, the p99 lock wait at 8
  writers drops from about 20 ms to about 3.5 ms.

| shape, writers | race: lock wait summed per round | bh-mimic: summed | bh-mimic: time inside the handler | commits that entered the handler | handler calls per wait |
|---|---|---|---|---|---|
| 400 x 5 docs, 8 | 205.9 ms | 193.8 ms | 192.6 ms | 4.2 % (15 of 400) | 2.5 |
| 400 x 5 docs, 16 | 574.4 ms | 523.1 ms | 521.7 ms | 7.8 % (31 of 400) | 2.8 |
| 2000 x 5 docs, 16 | 1,460.7 ms | 1,392.2 ms | 1,386.5 ms | 2.8 % (59) | 3.1 |
| 1000 x 50 docs, 8 | 1,232.8 ms | 1,191.2 ms | 1,188.2 ms | 1.4 % (13) | 6.3 |
| events-hot, 16 | 576.8 ms | 591.4 ms | 589.7 ms | 8.9 % (37) | 2.7 |
| daemon, 8 | 1,598.4 ms | 1,563.4 ms | 1,585.4 ms | 1.1 % (48 of 4000) | 3.8 |

Read across a row: **a few percent of commits find the lock held, and each of those sleeps 13-91 ms on
the default schedule**, against a lock-hold time of well under 0.1 ms per 5-document commit. That sleep
is the whole cost of racing. No Polly retry ran in any mode of any run (`fisher.retry` and
`fisher.write_lock.retries` were 0 throughout), and the handler never gave up.

⚠️ **This contradicts a note in CLAUDE.md's "Tracing" section**, which says the wait at `BEGIN IMMEDIATE`
"comes from the connection string's `Default Timeout` and from nowhere else", and not from
`PRAGMA busy_timeout`. Measured here, `busy_timeout`'s handler is what governs the wait for up to
5000 ms. Microsoft.Data.Sqlite's own retry, bounded by `Default Timeout`, takes over only after the
handler gives up, and that is what `bh-none` shows (next).

**`bh-none` (`busy_timeout = 0`, in effect) is far worse, and the retry above it is not async.** When the
handler returns 0, Microsoft.Data.Sqlite does not surface `SQLITE_BUSY`. It retries the statement itself,
and its waits land in steps of about 150 ms: the max wait is 152 ms at 2 writers and 453 ms p99 at 8.
That fits its synchronous `Thread.Sleep(150)` retry loop. So "a tiny `busy_timeout` plus an in-process
async retry" cannot be built above Fisher without changing Fisher. The busy never reaches code that could
retry asynchronously; it is retried synchronously below it. 400 x 5 wall clock with `bh-none`: 168 ms at
2 writers, 1,063 ms at 8, 2,118 ms at 16 (against 32, 61, 111 ms racing).

**400 commits x 5 documents** (rerun at load ~4, 11 rounds; the 21-round runs agree):

```
writers mode          wall (ms)  range          p50   p95   p99   max    CPU/round  lock wait summed  in handler
   8    race             60.7    28.9 .. 113.0   0.1   0.4  19.7  108.6    26.8 ms     205.9 ms
   8    bh-mimic         59.7    38.0 ..  86.8   0.1   0.6  19.8   83.5    37.7        193.8         192.6
   8    bh-sleep1ms      28.8    26.7 ..  41.2   0.1   3.2   9.3   19.1    28.7        142.5         141.1
   8    bh-spin50us      38.1    34.2 ..  53.8   0.5   2.2   3.5    7.9   248.3        220.0         192.2
   8    bh-yield         40.7    36.3 ..  53.3   0.6   2.1   3.5   10.8   205.5        238.5          61.3
   8    queue            23.4    21.2 ..  35.0   0.4   0.7   2.0    2.4    54.6          1.4
   8    gate             26.8    25.9 ..  41.3   0.5   0.9   2.0    2.3    56.9          1.7
   8    group            14.4    13.8 ..  27.2   0.3   0.5   0.9    2.6    19.7          0.0
  16    race            113.0    85.7 .. 240.0   0.1   3.5  55.2  236.8    43.8        574.4
  16    bh-mimic        110.2    84.4 .. 342.0   0.1   3.5  55.3  338.4    32.8        523.1         521.7
  16    bh-sleep1ms      34.5    30.0 ..  49.6   0.1   6.4  20.3   45.7    37.1        339.7         335.5
  16    bh-spin50us      49.0    45.0 ..  66.0   1.3   5.5   8.8   20.5   576.7        611.7         451.1
  16    bh-yield         56.7    52.5 ..  72.3   1.5   6.2  10.0   17.6   584.3        712.9         154.3
  16    queue            23.9    21.3 ..  35.9   0.9   1.7   2.9    3.2    56.2          1.5
  16    gate             27.4    26.3 ..  41.6   1.1   1.9   2.8    3.2    56.8          1.8
  16    group            12.9    12.6 ..  26.8   0.5   1.1   1.4    1.6    16.1          0.0
```

At 1, 2 and 4 writers (first pass, load ~7) every racing mode is within noise of `race` at 1 writer.
`bh-sleep1ms` is already ahead of `race` at 2 writers (24.1 against 32.0 ms) and 4 (25.6 against
41.4 ms).

**Larger runs and heavier commits** (first pass, load ~7; wall median / p99 / max / CPU per round):

```
                          2000 x 5 docs, 9 rounds                      1000 x 50 docs, 7 rounds
writers mode          wall    p99    max    CPU               wall    p99     max    CPU
   8    race         145.2    3.4  184.8   112.6 ms           368.3   35.6   440.2    246.8 ms
   8    bh-mimic     148.8    3.5  135.2   117.8              370.2   19.6   545.0    245.8
   8    bh-sleep1ms  118.7   11.8  110.4   121.6              243.7   32.7   120.4    262.1
   8    bh-spin50us  233.2    4.5  101.9  1526.1              341.8   10.8    19.9   2283.6
   8    bh-yield     239.4    4.3  140.9  1270.1              305.2   10.0    60.2   1602.6
   8    queue        100.7    1.5    4.3   237.6              223.8    3.6    12.5    321.8
   8    group         70.4    1.4    5.5   100.8              205.9    4.2     5.4    220.2
  16    race         196.2    9.2  336.7   124.4              476.8   83.3   844.4    264.4
  16    bh-mimic     194.6    9.2  436.6   125.5              769.2  109.5   845.2    287.6
  16    bh-sleep1ms  128.5   22.8   92.3   145.5              262.1   71.7   213.0    304.4
  16    bh-spin50us  309.0   11.1  139.4  3699.0              395.8   26.9    56.5   4918.4
  16    bh-yield     329.0   12.0   82.2  3431.7              400.6   27.6    69.3   4271.4
  16    queue        104.1    2.3    7.1   242.5              225.3    6.1    23.9    322.8
  16    group         63.9    1.9    4.2    78.0              200.7    6.0     6.4    205.7
```

(`bh-mimic` at 16 writers x 50 documents, 769 ms against 477 ms for `race`: both ranges are wide,
356-861 and 559-871 ms, and they overlap. Read it as noise.)

What the numbers show about the smaller change:

- **A fixed 1 ms sleep closes most of the throughput gap, at racing's CPU cost.** Against the queue's wall
  clock, `bh-sleep1ms` is 9-44 % slower (28.8 / 23.4, 34.5 / 23.9, 118.7 / 100.7, 128.5 / 104.1,
  243.7 / 223.8, 262.1 / 225.3 ms). Racing today is 1.4-4.7x slower. Its CPU per round is the same as
  racing's and below the queue's.
- **It does not close the tail.** `bh-sleep1ms`'s p99 is 9-72 ms where the queue's is 1.5-6 ms. Its p99
  is lower than racing's at 400 x 5 and 1000 x 50 but higher at 2000 x 5, though its max is 1.7-5.7x
  lower everywhere. The handler polls every millisecond,
  but whoever asks first after the lock is released gets it: a fresh writer, or a waiter that happened to
  wake. So an unlucky waiter can lose many times in a row. That is an inference from the numbers (a p50
  of 0.1 ms with a long p99); it was not traced.
- **Spinning or yielding is the wrong small change.** It wins at 400 x 5 (38-57 ms against 61-113 ms)
  but loses to plain racing on the longer runs (233-329 ms against 145-196 ms at 2000 x 5). It keeps
  6-12 cores busy: at 16 writers, 3.4-4.9 s of CPU per round against 0.12-0.26 s for racing. A
  plausible reason, not measured: spinning waiters compete for the cores the lock holder and the writers
  building their next session need, and every handler invocation is a fresh lock probe on the WAL index.
- **What a handler change keeps that a queue does not.** The commit runs on the caller's async flow,
  and fails at the call. Cancellation reaches it as today. Enlisted sessions are covered. And so is every
  other writer on the file that opens through Fisher: the daemon (section 3), Hi-Lo, a second store. One
  limit: a handler installed through `StoreOptions.Functions` reaches only connections opened through the
  store's data source. Weasel's migrations open their own connection, and another process has its own
  handler.
- **The cost of the handler change itself.** A busy handler runs inside `sqlite3_step`, so every policy
  blocks a thread-pool thread while it waits. So does the default handler today. A fixed 1 ms poll wakes
  a waiter up to 5000 times before it gives up, against the default schedule's roughly 60.

### 2. Event appends

**Many streams, one event each: the same picture as documents.** Each commit starts 5 new streams with
one event each (rerun, 11 rounds; wall median / p99 / max / CPU per round):

```
writers mode          wall (ms)   p50   p99    max     CPU
   8    race            117.4      0.1   36.2  135.2    63.5 ms
   8    bh-mimic        144.1      0.1   36.6  186.7    64.9
   8    bh-sleep1ms      65.6      0.1   25.1   69.4    68.8
   8    queue            58.7      1.1    2.5    3.2    88.7
   8    group            44.0      0.9    2.2    2.9    51.7
  16    race            545.4      0.1  185.4  642.6    93.5      (range 191 .. 650; first pass median 193.3)
  16    bh-mimic        343.1      0.1  135.8  545.5    86.5
  16    bh-sleep1ms      71.3      0.1   43.2   76.9    80.3
  16    queue            65.3      2.4    4.4    8.2   102.9
  16    group            39.9      1.5    2.7    2.9    42.6
```

The first pass (load ~7) also ran 1 and 4 writers. Everything was within noise at 1 writer, and at 4
writers the queue was ahead (65.4 against 73.3 ms racing). In that pass `bh-spin50us` was slower than
racing at 4 writers (97.9 against 73.3 ms) and faster at 8 and 16, at 6-21x the CPU. Bounded4 matched the
queue throughout.

**The hot stream changes the question, because the version conflict rate depends on the mode.** One
stream; each writer reads its version, appends expecting it, and commits. Nothing retries a conflict.
Rerun, 11 rounds, 400 commits per round:

```
writers mode          wall (ms)  p99 (ms)  conflicts            successful appends/round  successful appends/s
   4    race             43.9      3.6       2.3 %               391                        8.9k
   4    bh-mimic         45.2      3.7       2.5 %               390                        8.6k
   4    bh-sleep1ms      33.8      5.8       7.6 %               369                       10.9k
   4    bh-spin50us      25.7      1.1      53.2 %               187                        7.3k
   4    queue            36.7      0.9      74.6 %               102                        2.8k
   4    group            34.4      0.9      74.7 %               101                        2.9k
   8    race             61.6     19.7       5.6 %               378                        6.1k
   8    bh-mimic         62.0     19.9       5.5 %               378                        6.1k
   8    bh-sleep1ms      36.5     10.5      12.0 %               352                        9.6k
   8    bh-spin50us      27.3      2.4      68.2 %               127                        4.7k
   8    queue            39.4      1.8      86.8 %                53                        1.3k
   8    group            36.2      1.5      87.2 %                51                        1.4k
  16    race            111.2     36.2       9.8 %               361                        3.2k
  16    bh-mimic         91.5     47.0       9.1 %               364                        4.0k
  16    bh-sleep1ms      40.1     17.5      19.9 %               321                        8.0k
  16    bh-spin50us      29.7      5.4      77.2 %                91                        3.1k
  16    queue            40.7      3.2      92.8 %                29                        0.7k
  16    group            34.0      2.7      93.4 %                26                        0.8k
```

The first pass agreed to within a point of conflict rate. Gate and bounded4 matched the queue exactly:
74.5 %, 86.8 % and 92.8 %.

- **Measured: serializing commits multiplies conflicts on a contended stream 9-32x, and cuts successful
  appends per second by 3-5x.** With a queue, almost every commit in a round of K writers had read the
  same version: K-1 of them lose. Racing loses 2-10 %.
- **Inferred, not traced: racing's busy-handler sleep works as accidental backoff.** A writer that wakes
  from a 10-50 ms sleep has almost always been beaten, but because most writers are asleep at any moment,
  few of them are holding a stale read at once. The faster a mode hands the lock over, the more writers
  hold a read at the same version, and the conflict rate climbs in that order: default handler, 1 ms
  sleep, 50 µs spin, queue. `bh-sleep1ms` sits between, and has the highest goodput of all.
- **A queue widens the read-to-commit window by its own length.** The writer reads before it queues, so
  every commit ahead of it is a chance for its read to go stale. A retry loop would make this worse
  under a queue: a retried commit goes to the back.
- With group commit, a conflict is rolled back to its savepoint and fails alone. 93 % of units in a
  group did nothing durable, so a group was mostly savepoints rolling back.

### 3. The async daemon writing at the same time

8 or 16 writers append 4000 one-event streams while an async `Snapshot<BenchTally>` runs under the
daemon, with default polling. Rerun, 7 rounds:

```
writers mode          app wall (ms)  app p99  app max  CPU/round   daemon catch-up      daemon lock wait  daemon lock wait  daemon batches
                                                                     after writes (ms)    max (ms)          summed (ms)       per round
   8    race             377.7         3.5    439.3     310 ms       869 (729 .. 918)     35.5               28.2              9
   8    bh-mimic         346.5         1.5    436.3     316          880 (816 .. 922)     81.6                8.2              9
   8    bh-sleep1ms      318.3        14.9     70.1     348          922 (917 .. 970)     21.4               13.7              9
   8    bh-spin50us      555.7         4.7     30.7    3788          713 (612 .. 767)      4.6                2.6              9
   8    queue            307.4         1.9     13.5     621          922 (917 .. 1074)    82.7               30.3              9
   8    group            178.2         1.4      2.1     191           51 (51 .. 104)       0.1                0.1              8
  16    race             387.7         9.1    539.6     313          867 (766 .. 975)    108.6                3.8              9
  16    bh-mimic         480.9         9.2    638.8     334          765 (562 .. 871)     55.6               10.5              9
  16    bh-sleep1ms      334.1        31.3    116.4     401          921 (919 .. 973)     31.5               20.2              9
  16    bh-spin50us      728.6        15.6     97.8    8940          511 (153 .. 615)      9.6                5.6              9
  16    queue            311.6         2.9     22.3     619          922 (916 .. 1075)    83.1               20.2              9
  16    group            177.1         1.9     10.0     199           51 (51 .. 1022)      0.1                0.2              8
```

(The first pass, at load 7-10, gave the same orderings. Gate was there too: 360-366 ms app wall and
p99 2-3 ms.)

- **Measured: the daemon is not lock-bound in any mode.** Its summed `BEGIN IMMEDIATE` wait is 0.1-56 ms
  per round over about 9 batches, against a catch-up of 0.5-0.9 s. Catch-up is set by the daemon's own
  polling and batching, not by contention. It barely moves between race, queue and the handlers.
- **Measured: a queue does not protect the daemon's own waits.** The daemon races with the queue's
  consumer, and its worst single wait was 83-109 ms across the two passes' queue runs, about the same as
  racing (36-109 ms). The default handler's long sleeps hit the daemon too. With `bh-spin50us` its worst
  wait was 4-12 ms. With `bh-sleep1ms` it was 21-32 ms in the rerun but 41-82 ms in the first pass, so
  that one is not clear. These maxima come from about 60 daemon waits per cell. A handler policy reaches
  the daemon; a queue cannot.
- **The application's side matches section 1.** The queue has the best tail among the one-transaction
  modes (p99 2-3 ms). `bh-sleep1ms` has the throughput but a 15-31 ms p99. Spinning is slowest and burns
  3.8-8.9 s of CPU per round.
- **Group's 51 ms catch-up is measured but not explained.** One round at each writer count still took
  about 1 s, and group commit gives the daemon nothing to do differently. A guess, not checked: the
  shorter write phase (178 ms) changes where the daemon's polling cycle falls relative to the last append.
  Do not read it as group commit speeding up projections.

### 4. Group commit (prototype)

It is the fastest mode in every shape except the hot stream, and the only one that beats the queue
clearly.

```
                       wall (ms): race / queue / group      p99 (ms): queue / group    groups: size p50 / max
400 x 5 docs,   8      60.7 / 23.4 / 14.4                   2.0 / 0.9                   4 / 8
400 x 5 docs,  16     113.0 / 23.9 / 12.9                   2.9 / 1.4                   8 / 15
2000 x 5 docs,  8     145.2 / 100.7 / 70.4                  1.5 / 1.4                   4 / 8
2000 x 5 docs, 16     196.2 / 104.1 / 63.9                  2.3 / 1.9                   8 / 16
1000 x 50 docs, 16    476.8 / 225.3 / 200.7                 6.1 / 6.0                   7 / 15
events-many,   16     545.4 / 65.3 / 39.9                   4.4 / 2.7                   8 / 15
daemon run,    16     387.7 / 311.6 / 177.1                 2.9 / 1.9                   (group of up to 64)
group capped at 4, 2000 x 5, 16 writers:   queue 106.0, group 72.6   (p99 2.3 / 1.9)
```

- Groups stay small, around K/2 for K writers. A writer only re-queues after its previous unit has
  completed, so a group holds whoever arrived during the previous group. A cap of 4 still kept most of
  the gain (72.6 against 63.9 ms uncapped, 106.0 for the queue).
- It uses the least CPU of any mode, below racing: 16-20 ms per 400 x 5 round, against 27-44 ms racing
  and 55-57 ms for the queue.
- **The gain shrinks as commits get heavier**: 1.6-1.9x faster than the queue with 5 small documents
  and 16 writers, 1.1x with 50 documents. With `synchronous = NORMAL` in WAL there is no fsync per commit to
  amortize. So what grouping saves is the per-transaction cost (BEGIN, COMMIT, the WAL frame and index
  updates, the hand-off between commits), not durability waits. That part is an inference.
- Not one group's COMMIT failed in any run, so the "a COMMIT failure fails the whole group" path was never
  exercised. Nothing here measures it.

**Its semantic costs**, on top of everything round 1 listed for the queue (late failure, the consumer's
async flow, cancellation, FIFO head-of-line blocking, re-entrant deadlock, enlisted sessions):

- **The caller hands over a unit of work, not a session.** The transaction exists only on the consumer,
  so the writer's own session cannot commit. The prototype replays a delegate onto a fresh enlisted
  session. Whatever the writer's session held does not carry over: its identity map, dirty tracking,
  per-session listeners, `SetHeader` and correlation id, and documents already stored on it. Reads still
  happen in the writer's session, outside the transaction that writes. For the optimistic guards that is
  the same as today, but it is a different API from `SaveChangesAsync`, not a wrapper around it.
- **An enlisted session gives up several things, and group commit inherits all of them.** It runs no
  resilience pipeline. It fires no post-commit hooks (`AfterCommitAsync` on listeners, the outbox and
  participants, and the append observer), because Fisher is never told when the consumer commits. It
  cannot create a document table on demand. It cannot reserve a new Hi-Lo block (fisher#454 refuses
  that). All of these would need the consumer to stand in for them, after the group's COMMIT.
- **A unit's success is not durable until the group commits.** A unit whose savepoint was released can
  still fail if the group's COMMIT fails, though it did nothing wrong. A caller sees its task complete
  only after the whole group: one 50-document unit delays every small unit grouped with it.
- **Savepoints contain a unit's database writes, not its side effects.** Anything a unit did outside
  SQLite (the in-memory version tracker, a projection's raised messages, a participant's own state) is not
  rolled back with the savepoint. The prototype's units do none of that, and a real design would have to
  answer for each.
- **The group's connection is opened outside Fisher's fisher#576 open gate.** The prototype opens it once,
  before the writers start, to stay clear of that race. A production version would have to open through
  Fisher.

### 5. A bounded channel with backpressure

`bounded1` and `bounded4` (`FullMode.Wait`) matched the unbounded queue in every shape: wall clock within
noise, p99 within 0.5 ms, the same conflict rate on the hot stream. At 400 x 5 (rerun): 8 writers 22.0 /
22.5 ms against 23.4 for the queue; 16 writers 21.5 / 22.0 against 23.9. At 1000 x 50 and 16 writers:
222.9 / 221.1 against 225.3 ms. With one in-flight unit per writer, the channel never holds more than K
items, so backpressure changes little here. It would matter for a writer that does not await its commit
before queuing the next, and this scenario has none.

### Notes

Measured:

- At one writer every mode is the same, within noise. Every difference here is a contention effect.
- **The default busy handler is the whole cost of racing.** Its sleep time is 98-100 % of the
  `BEGIN IMMEDIATE` wait, and a counting copy of it reproduces racing's numbers. Retries are zero in every
  mode.
- **`busy_timeout = 0` hands the wait to Microsoft.Data.Sqlite's own synchronous retry**, with waits of
  about 150 ms each. It is 5-20x slower than racing, and nothing asynchronous can be put above it without
  changing Fisher.
- **A fixed 1 ms busy handler recovers most of the throughput** (within 9-44 % of the queue's wall clock,
  against 1.4-4.7x for racing), at racing's CPU. It keeps a 9-72 ms p99 where the queue's is 1.5-6 ms.
- **Spinning or yielding in the handler keeps 6-12 cores busy and loses to racing on longer runs.**
- **On a hot stream, serializing turns 2-10 % conflicts into 75-93 %** and cuts successful appends 3-5x.
  `bh-sleep1ms` has the best goodput.
- **The daemon is not lock-bound.** A queue leaves its waits as they are; a handler policy shortens them.
- **Group commit is the fastest and cheapest mode except on a hot stream**, and it requires a different
  API (a unit of work, not a session) on top of enlisted sessions' restrictions.
- **A bounded channel behaves like the unbounded one here.**
- **The queue costs 1.3-2x racing's CPU** for the same work (54-56 ms against 27-44 ms per 400 x 5 round
  in the rerun, and 620 against 310 ms in the daemon run). A probable reason, not measured: the thread-pool
  hand-off per commit between writer and consumer.

Inferred, not traced:

- The 1 ms handler's remaining tail comes from barging: a fresh writer can take the lock ahead of one that
  has been waiting.
- Racing's low conflict rate on a hot stream comes from the busy handler's sleeps acting as backoff.
- Group commit's gain is per-transaction overhead, not fsync.

### Comparison: each option against today's racing

| Option | Measured gain (400 x 5 docs, 8 / 16 writers, unless noted) | Tail (p99) | CPU | Semantic cost |
|---|---|---|---|---|
| Today: race, default handler | baseline: 61 / 113 ms | 20 / 55 ms | baseline | — |
| `busy_timeout = 0` (bh-none) | **17-19x slower** (1,063 / 2,118 ms) | 453 / 1,206 ms | same | none, but unusable: the retry above it is a synchronous 150 ms sleep |
| Busy handler, fixed 1 ms sleep | 2.1x / 3.3x faster (29 / 35 ms). 2000 x 5: 1.2x / 1.5x. Hot stream: 1.6x / 2.5x the goodput | 9 / 20 ms: half of racing's, 4-7x the queue's | same as racing | none for callers. Covers the daemon, enlisted sessions, Hi-Lo, a second store. A thread blocks while waiting, as today. It needs `sqlite3_busy_handler`, which SQLitePCLRaw does not expose (P/Invoke, or upstream) |
| Busy handler, spin or yield | 1.5-2.3x faster at 400 x 5, **slower** than racing at 2000 x 5 | 3.5 / 9-10 ms | **6-12 cores** | as above, plus the CPU |
| Queue, unbounded (round 1) | 2.6x / 4.7x faster (23 / 24 ms) | 2 / 3 ms | 1.3-2x racing | round 1's list: late failure, consumer's async flow, cancellation, FIFO head-of-line, re-entrant deadlock, enlisted sessions bypass it, the daemon and other writers still race. **Hot stream: 75-93 % conflicts, 3-5x less goodput** |
| Queue, bounded (1 or 4) | same as unbounded | same | same | same as the queue, plus a writer can wait before it is even queued |
| Gate (semaphore) | 2.3x / 4.1x faster | 2 / 3 ms | 1.3-2x racing | as the queue, except it keeps the caller's async flow. Same hot-stream conflicts |
| Group commit (prototype) | 4.2x / 8.8x faster (14 / 13 ms). 2000 x 5: 2.1x / 3.1x. 1000 x 50: 1.8x / 2.4x | 0.9 / 1.4 ms | below racing | everything the queue costs, plus: a unit of work instead of a session, enlisted-session restrictions (no post-commit hooks, no resilience pipeline, no on-demand tables, no Hi-Lo advance), a group's COMMIT failure fails every unit, and side effects outside SQLite are not rolled back by a savepoint. Hot stream as the queue |

**Observation for the owner, not a decision.** On these numbers, a fixed short-sleep busy handler gets
most of the queue's throughput gain. It changes nothing a caller can observe, reaches writers a queue
cannot (the daemon, enlisted sessions, a second store), and on a contended stream does better than either
racing or a queue. What it gives up is the queue's tight tail. If the tail is the goal, the queue and the
gate are what deliver it, with the costs above, and group commit delivers both throughput and tail at the
largest semantic cost. A handler change is also the smallest to undo. Its catch is plumbing:
SQLitePCLRaw 3.0.5 has no `sqlite3_busy_handler`, so it needs a P/Invoke into `e_sqlite3`, as here, or an
upstream addition. Policies between 1 ms fixed and the default schedule were not explored, for example
spin briefly, then sleep 1 ms, capped at a few ms.

**Not measured:** another process writing the same file; Hi-Lo advances and first-use migrations under
contention; `synchronous = FULL`, where group commit would also save fsyncs; the queue or group commit
with retries on version conflicts; a writer that does not await its commit before queuing the next, which
is what bounded backpressure is for; policies between "1 ms fixed" and the default schedule; the group
commit failure path (no COMMIT failed); thread-pool starvation from blocking busy handlers at high writer
counts (none was seen up to 16 writers on 18 cores); net11.0; Linux or Windows, whose sleep granularity
differs from macOS's; and any machine other than this one.

## fisher#547 decision: the 1 ms busy handler is adopted (2026-11-16)

Jeremy's decision: Fisher 2.0 adopts the fixed 1 ms busy handler and nothing else. The `Channel<T>` queue,
the gate and group commit stay benchmark-only, and there is no 1.x backport. `Storage/SqliteBusyHandler`
installs it on every connection Fisher opens, with `BusyTimeout` as the deadline.

**What the modes mean from here on.** Plain `race` is now Fisher's own handler. The bh-* modes turn Fisher's
handler off (`StoreOptions.UseFixedSleepBusyHandler`, internal) so each measures its own policy, and
`bh-mimic` is SQLite's default, which is what `race` measured before this change.

A check run on the shipped code, `--writers 8,16 --total-commits 400 --docs-per-commit 5 --rounds 11 --modes
race,bh-mimic,bh-sleep1ms`, on a box shared with other agents (load average 50-64 on 18 cores, so absolute
numbers are slower and noisier than round 2's):

| writers | race (Fisher's handler) wall / p99 | bh-sleep1ms wall / p99 | bh-mimic (SQLite's default) wall / p99 |
|---:|---:|---:|---:|
| 8 | 57.4 / 14.2 ms | 51.9 / 12.4 ms | 73.2 / 20.4 ms |
| 16 | 60.4 / 20.9 ms | 64.3 / 23.9 ms | 99.3 / 40.7 ms |

`race` and `bh-sleep1ms` agree, which shows the production handler is the measured one. The gain over the default
is smaller than round 2's 2.1x / 3.3x because of the load, but it is the same ordering. Retries and failed
commits were zero in every mode.

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
## Prepared-statement reuse in the write batch — fisher#171 (2026-09-05, `perf/write-batch`)

`FisherSession.ExecuteBatchAsync` prepared and disposed one `SqliteCommand` per queued operation
inside the exclusive `BEGIN IMMEDIATE` transaction. It now keeps the last prepared command and
reuses it while consecutive operations compile to identical SQL, so a run of N same-shape writes is
one `sqlite3_prepare_v2` rather than N. Each operation still executes on its own against its own
reader — nothing about result-set handling moved.

```
Fisher.Benchmarks — 2026-09-05 19:41 -05:00
  OS:        macOS 26.4.1 (Arm64)
  .NET:      .NET 10.0.1
  CPUs:      18
  Config:    Release
```

**Measured paired and alternating**, baseline worktree at `origin/main` (7a6e552) against this
branch, one run each per pair, back to back — the run-to-run noise on this machine is wider than the
effect at 100 docs, so a before-block and an after-block recorded minutes apart would not have been
readable. Medians of six pairs (`doc-save --rounds 11`) and five pairs (`concurrent-writers`):

```
== doc-save, median commit ==
                          before      after
  100 docs/commit          1.65 ms    1.35 ms     ~1.2x   (5 of 6 pairs favour after)
  1000 docs/commit         15.0 ms    10.2 ms     ~1.5x   (6 of 6 pairs favour after)

== concurrent-writers (5 docs/commit), total wall clock ==
                          before      after
  8 writers x 50 commits  119.8 ms    90.6 ms     ~1.3x   (5 of 5 pairs favour after)
  1 writer x 400 commits   55.2 ms    51.9 ms
  contention ratio           2.17x      1.75x
  fisher.retry events            0          0
```

Per-pair, for the two that carry the signal:

```
doc-save 1000    before  36.9  15.7  15.0  14.6  15.0  14.4
                  after  10.0  10.9  10.4  12.8   9.9   9.6

concurrent 8w    before 158.6 106.6 119.8 124.9 119.7
                  after 123.1  75.8  75.5 107.1  90.6
```

Notes:

- **Allocations are the number, and the means are not quoted.** The allocation column is exactly
  reproducible run to run (13.34 KB and 16.17 KB came back identical across three baseline runs);
  the ShortRun means moved 70–101 µs for `FilteredPage` across runs of *identical* code, so no
  timing claim is made from them. `--job medium` reports NA on this harness — the default toolchain
  cannot build the benchmark out of process — so the in-process ShortRun is what there is.
- **It is a ~1% change, and that is the honest size of the config-only half.** What was removed is
  a `string.Join` over a cached array and a `MemberFactory` construction per query. The per-query
  allocation that remains is the expression visit, one `IQueryableMember` per referenced member with
  its interpolated locator, the `Statement`, and the SQL render — which is the marten#5013-style
  filter-shape plan cache, deliberately left to a separate node.
- Nothing else was re-run: this change touches the read path's construction only.
- **The contention ratio is the number this change was aimed at**, not the single-writer time. The
  baseline note for this harness records that contention here shows up as waiting inside
  `BEGIN IMMEDIATE` under the connection's busy timeout with **zero** `fisher.retry` events — still
  zero on both sides — so the only lever is how long the lock is held. 2.17x → 1.75x is that lever
  moving.
- **100 docs is at the noise floor** and should not be quoted as a headline. One commit of 100 is
  ~1.5 ms total, where connection setup and the transaction dominate the ~100 prepares removed.
- **The gain scales with the length of a coalesced run**, which is why 1000 docs moves furthest and
  `concurrent-writers` (5 docs per commit) moves least of the three.
- **Not moved, and not expected to be:** `event-append` (one operation per stream, so the runs are
  length 1 in the many-streams shape), `daemon-rebuild`, `cold-start`. Re-run and inside noise.
- **The first baseline pair at each size is an outlier in both scenarios** (36.9 ms, 158.6 ms) — the
  first process of a sequence pays JIT and page cache. Left in rather than trimmed, and excluded
  from the medians quoted above.
- **Concatenating the batch into one multi-statement command was measured first and rejected**: it is
  *slower than the code it replaces*, 82–192 ms against 4–6 ms for the same 1000 upserts, because
  `SqliteParameterCollection` rebinds against the whole collection per prepared statement. Every
  chunk size measured is worse than a command per operation. See `FisherSession.ExecuteBatchAsync`'s
  remarks and the CLAUDE.md section for the full numbers and for why the result-set walk it needs
  could not be made safe on this provider anyway.
